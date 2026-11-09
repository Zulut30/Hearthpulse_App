#!/usr/bin/env bash
# Authenticode signing for the Windows packages (jsign, Apache-2.0: https://github.com/ebourg/jsign; downloaded at build
# time with a pinned SHA-256, never shipped). Called by installer/windows/build.sh and by NSIS (!uninstfinalize).
#
#   sign.sh --prepare     picks the way to sign once per build and says so in the log (one line)
#   sign.sh --mode        prints the way: pfx, azure, selftest or none
#   sign.sh <file>...     signs each file with a timestamp (nothing to do when the way is none)
#   sign.sh --show <file>...  selftest only: osslsigncode verify, prints the signer
#
# The way, first match wins:
#   pfx       WINDOWS_SIGN_PFX (base64 of the .pfx) + WINDOWS_SIGN_PFX_PASSWORD, timestamp DigiCert
#   azure     AZURE_SIGNING_ENDPOINT, AZURE_SIGNING_ACCOUNT, AZURE_SIGNING_PROFILE, AZURE_TENANT_ID, AZURE_CLIENT_ID,
#             AZURE_CLIENT_SECRET (Azure Trusted Signing), timestamp Microsoft
#   selftest  SIGN_SELFTEST=true: a throwaway root CA and code-signing certificate made here (trial runs only)
#   none      a warning, exit 0: the packages are built unsigned
# With signing secrets, a failure (jsign not downloaded or not matching its pin, no java, a bad .pfx/password, Azure
# refusing) fails the build when PUBLISHING=true (a tag or publish=true): a release never goes out unsigned by accident.
# In a trial run it is a warning and the build goes on unsigned. A selftest failure always fails the build.
# Passwords and tokens reach jsign through the environment (--storepass env:NAME), never through its command line.
# State (mode, jsign.jar, the decoded .pfx) lives in HEARTHPULSE_SIGN_DIR, which build.sh removes at the end.
set -euo pipefail

JSIGN_VERSION="7.1"
# SHA-256 of the GitHub release asset jsign-7.1.jar (2025-02-14). Checked 2026-11-09 against an independent copy: the
# Maven Central artifact https://repo1.maven.org/maven2/net/jsign/jsign/7.1/jsign-7.1.jar has the same SHA-256, and
# its published jsign-7.1.jar.sha1 (348919ddaeed5100e1dd6a05b98da6c61e192afe) matches the GitHub jar.
JSIGN_SHA256="cfb48b07fdd2ee199bfc9e71d8dccdde67a799c4793602e446c7a101be62b3c4"
JSIGN_URL="https://github.com/ebourg/jsign/releases/download/$JSIGN_VERSION/jsign-$JSIGN_VERSION.jar"
SELFTEST_SUBJECT="Hearthpulse sign selftest"
# System CA bundle that holds the timestamp authority's root (Ubuntu's path; override for a local check).
TSA_CAFILE="${SIGN_TSA_CAFILE:-/etc/ssl/certs/ca-certificates.crt}"

SIGN_DIR="${HEARTHPULSE_SIGN_DIR:-${TMPDIR:-/tmp}/hearthpulse-sign}"

# GitHub Actions turns ::warning:: into an annotation; elsewhere a plain line is enough.
warn() {
  if [ -n "${GITHUB_ACTIONS:-}" ]; then echo "::warning::$*"; else echo "warning: $*"; fi
}

# A signing problem: fatal for the selftest and for a publishing run with secrets, else a warning.
fail_or_warn() {
  local mode="$1"; shift
  if [ "$mode" = selftest ] || [ "${PUBLISHING:-}" = "true" ]; then
    if [ -n "${GITHUB_ACTIONS:-}" ]; then echo "::error::$*"; else echo "error: $*" >&2; fi
    exit 1
  fi
  warn "$*"
}

pick_mode() {
  if [ -n "${WINDOWS_SIGN_PFX:-}" ]; then
    echo pfx
  elif [ -n "${AZURE_SIGNING_ENDPOINT:-}" ] && [ -n "${AZURE_SIGNING_ACCOUNT:-}" ] && [ -n "${AZURE_SIGNING_PROFILE:-}" ] \
    && [ -n "${AZURE_TENANT_ID:-}" ] && [ -n "${AZURE_CLIENT_ID:-}" ] && [ -n "${AZURE_CLIENT_SECRET:-}" ]; then
    echo azure
  elif [ "${SIGN_SELFTEST:-}" = "true" ]; then
    echo selftest
  else
    echo none
  fi
}

fetch_jsign() {
  local jar="$SIGN_DIR/jsign.jar"
  command -v java >/dev/null || { echo "java is needed to sign (jsign)" >&2; return 1; }
  curl -fsSL --retry 3 -o "$jar" "$JSIGN_URL"
  echo "$JSIGN_SHA256  $jar" | sha256sum -c --quiet - || { echo "jsign.jar does not match the pinned SHA-256" >&2; rm -f "$jar"; return 1; }
}

make_selftest_pfx() {
  # A throwaway two-level chain valid from a day ago for three days, as a real certificate is: a root CA and a code-signing certificate
  # it issued. jsign leaves self-signed (root) certificates out of the signature, so a lone self-signed certificate
  # gave a signature without any certificate and osslsigncode could not find the signer. The private keys never
  # leave this folder; the root (selftest-ca.pem) is what --show trusts.
  # Both start a day back: the timestamp authority's clock may be seconds behind this machine's, and osslsigncode
  # checks the chain at the timestamp's time ("certificate is not yet valid" otherwise). openssl ca, because
  # OpenSSL 3.0 (Ubuntu 24.04) has no -not_before for req/x509.
  (
    cd "$SIGN_DIR"
    local start end
    start="$(date -u -d '1 day ago' +%y%m%d%H%M%SZ)"
    end="$(date -u -d '2 days' +%y%m%d%H%M%SZ)"
    : > index.txt
    openssl rand -hex 16 > serial
    cat > ca.cnf <<'CNF'
[ ca ]
default_ca = selftest
[ selftest ]
dir = .
database = index.txt
new_certs_dir = .
serial = serial
default_md = sha256
policy = any
unique_subject = no
copy_extensions = none
[ any ]
commonName = supplied
[ v3_ca ]
basicConstraints = critical,CA:TRUE
keyUsage = critical,keyCertSign,cRLSign
subjectKeyIdentifier = hash
[ v3_sign ]
basicConstraints = critical,CA:FALSE
keyUsage = critical,digitalSignature
extendedKeyUsage = codeSigning
subjectKeyIdentifier = hash
authorityKeyIdentifier = keyid
CNF
    openssl req -new -newkey rsa:3072 -nodes -subj "/CN=$SELFTEST_SUBJECT CA" \
      -keyout selftest-ca-key.pem -out selftest-ca.csr 2>/dev/null
    openssl ca -batch -notext -config ca.cnf -selfsign -keyfile selftest-ca-key.pem -in selftest-ca.csr \
      -extensions v3_ca -startdate "$start" -enddate "$end" -out selftest-ca.pem 2>/dev/null
    openssl req -new -newkey rsa:3072 -nodes -subj "/CN=$SELFTEST_SUBJECT" \
      -keyout selftest-key.pem -out selftest.csr 2>/dev/null
    openssl ca -batch -notext -config ca.cnf -cert selftest-ca.pem -keyfile selftest-ca-key.pem -in selftest.csr \
      -extensions v3_sign -startdate "$start" -enddate "$end" -out selftest-cert.pem 2>/dev/null
    openssl pkcs12 -export -inkey selftest-key.pem -in selftest-cert.pem -certfile selftest-ca.pem \
      -out sign.pfx -passout pass:selftest -name selftest
  )
}

prepare() {
  rm -rf "$SIGN_DIR"
  mkdir -p "$SIGN_DIR"
  chmod 700 "$SIGN_DIR" 2>/dev/null || true
  local mode
  mode="$(pick_mode)"
  case "$mode" in
    none)
      echo none > "$SIGN_DIR/mode"
      warn "Windows packages are not code-signed (no signing secrets)"
      return 0 ;;
    pfx)
      if ! printf '%s' "$WINDOWS_SIGN_PFX" | base64 -d > "$SIGN_DIR/sign.pfx" 2>/dev/null; then
        echo none > "$SIGN_DIR/mode"
        fail_or_warn pfx "Windows packages are not code-signed: WINDOWS_SIGN_PFX is not base64"
        return 0
      fi ;;
    selftest)
      make_selftest_pfx || fail_or_warn selftest "sign selftest: the throwaway .pfx could not be made" ;;
    azure) ;;
  esac
  if ! fetch_jsign; then
    echo none > "$SIGN_DIR/mode"
    fail_or_warn "$mode" "Windows packages are not code-signed: jsign could not be prepared ($mode)"
    return 0
  fi
  echo "$mode" > "$SIGN_DIR/mode"
  echo "Authenticode: signing the Windows packages ($mode, jsign $JSIGN_VERSION)"
}

current_mode() {
  [ -f "$SIGN_DIR/mode" ] || prepare >&2
  cat "$SIGN_DIR/mode"
}

azure_token() {
  # Client-credentials token for Azure Trusted Signing; the response is parsed without printing it. The request body
  # (with the client secret) is built by python3 from the environment and reaches curl on stdin, never in argv.
  local token
  token="$(python3 -c 'import os, sys, urllib.parse
sys.stdout.write(urllib.parse.urlencode({
    "grant_type": "client_credentials",
    "client_id": os.environ["AZURE_CLIENT_ID"],
    "client_secret": os.environ["AZURE_CLIENT_SECRET"],
    "scope": "https://codesigning.azure.net/.default"}))' \
    | curl -fsS --retry 3 -X POST -H "Content-Type: application/x-www-form-urlencoded" --data @- \
      "https://login.microsoftonline.com/$AZURE_TENANT_ID/oauth2/v2.0/token" \
    | python3 -c 'import json, sys; print(json.load(sys.stdin)["access_token"])')"
  [ -n "${GITHUB_ACTIONS:-}" ] && echo "::add-mask::$token" >&2
  printf '%s' "$token"
}

sign_files() {
  local mode="$1"; shift
  local jar="$SIGN_DIR/jsign.jar"
  case "$mode" in
    # jsign reads "env:NAME" as the variable's value: the secret never appears in the process's arguments.
    pfx)
      HP_SIGN_STOREPASS="${WINDOWS_SIGN_PFX_PASSWORD:-}" java -jar "$jar" --storetype PKCS12 \
        --keystore "$SIGN_DIR/sign.pfx" --storepass env:HP_SIGN_STOREPASS \
        --alg SHA-256 --tsaurl http://timestamp.digicert.com --tsmode RFC3161 --tsretries 3 --replace "$@" ;;
    selftest)
      HP_SIGN_STOREPASS=selftest java -jar "$jar" --storetype PKCS12 \
        --keystore "$SIGN_DIR/sign.pfx" --storepass env:HP_SIGN_STOREPASS \
        --alg SHA-256 --tsaurl http://timestamp.digicert.com --tsmode RFC3161 --tsretries 3 --replace "$@" ;;
    azure)
      local host="${AZURE_SIGNING_ENDPOINT#https://}" token
      host="${host%%/*}"
      token="$(azure_token)" && [ -n "$token" ] || return 1
      HP_SIGN_STOREPASS="$token" java -jar "$jar" --storetype TRUSTEDSIGNING --keystore "$host" \
        --storepass env:HP_SIGN_STOREPASS --alias "$AZURE_SIGNING_ACCOUNT/$AZURE_SIGNING_PROFILE" \
        --alg SHA-256 --tsaurl http://timestamp.acs.microsoft.com --tsmode RFC3161 --tsretries 3 --replace "$@" ;;
  esac
}

show() {
  # Selftest proof in the log: who signed each file, checked against the throwaway certificate.
  command -v osslsigncode >/dev/null || { echo "osslsigncode is not installed: signer not shown"; return 0; }
  local f out
  for f in "$@"; do
    echo "== osslsigncode verify $(basename "$f")"
    # The signature chains to the throwaway root; the timestamp to the system's CAs (DigiCert's TSA root).
    if ! out="$(osslsigncode verify -CAfile "$SIGN_DIR/selftest-ca.pem" -TSA-CAfile "$TSA_CAFILE" -in "$f" 2>&1)"; then
      echo "$out"
      echo "sign selftest: $(basename "$f") does not verify" >&2
      exit 1
    fi
    # The signer (first certificate printed), when, and both verdicts.
    echo "$out" | grep -m2 -E 'Subject:|Issuer ?:' || true
    echo "$out" | grep -E 'Timestamp time|verification: ok' || true
    # The proof: the signer is the throwaway leaf (not just its CA), the signature and the timestamp both verified.
    # osslsigncode 2.7 (Ubuntu 24.04) prints "Subject: /CN=...", newer ones "Subject: CN=...".
    echo "$out" | grep -Eq "Subject: /?CN ?= ?$SELFTEST_SUBJECT\$" \
      && echo "$out" | grep -q "^Signature verification: ok" \
      && echo "$out" | grep -q "^Timestamp Server Signature verification: ok" \
      || { echo "sign selftest: $(basename "$f") is not signed and timestamped by the selftest certificate" >&2; exit 1; }
  done
}

case "${1:-}" in
  --prepare) prepare ;;
  --mode) current_mode ;;
  --show)
    shift
    if [ "$(current_mode)" = selftest ]; then show "$@"; fi ;;
  "" ) echo "usage: sign.sh --prepare | --mode | <file>... | --show <file>..." >&2; exit 2 ;;
  *)
    mode="$(current_mode)"
    [ "$mode" = none ] && exit 0
    if sign_files "$mode" "$@"; then
      for f in "$@"; do echo "signed $(basename "$f") ($mode)"; done
    else
      fail_or_warn "$mode" "Authenticode signing failed for $(for f in "$@"; do printf '%s ' "$(basename "$f")"; done)($mode) - left unsigned"
    fi ;;
esac
