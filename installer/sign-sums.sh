#!/usr/bin/env bash
# Signs a release's SHA256SUMS.txt with the update key (Ed25519, RFC 8032 pure) and checks the signature against the
# public keys the app carries. Called by the release workflow after "Checksums", before publishing.
#
#   sign-sums.sh <dist folder> <update-signing-key.pub>
#
# Environment:
#   UPDATE_SIGNING_KEY  PKCS#8 PEM private key (secret); absent -> no SHA256SUMS.txt.sig
#   PUBLISHING          true when this run publishes a release (tag push or publish=true)
#   SIGN_SELFTEST       true (trial runs only): sign and check with a throwaway key; without the real secret its
#                       signature is left in the folder so the file count with a .sig is exercised too
#
# Rules (docs: .claude/skills/hearthpulse-release/SKILL.md, "Release signature"):
#   secret present      -> SHA256SUMS.txt.sig; at least one key from the .pub file must verify it, otherwise error;
#                          a .pub without keys -> checked with the secret's own public key, warning (transition)
#   no secret, keys in the .pub, publishing -> error: clients with the key would stop updating
#   no secret otherwise -> warning, no .sig
# Format (read by src/Hearthpulse.Core/Updates): one line, base64 of the 64-byte signature over the exact bytes of
# SHA256SUMS.txt.
set -euo pipefail

DIST="${1:?usage: sign-sums.sh <dist folder> <update-signing-key.pub>}"
KEYFILE="${2:?usage: sign-sums.sh <dist folder> <update-signing-key.pub>}"
SUMS="$DIST/SHA256SUMS.txt"
SIG="$DIST/SHA256SUMS.txt.sig"

error() {
  if [ -n "${GITHUB_ACTIONS:-}" ]; then echo "::error::$*"; else echo "error: $*" >&2; fi
  exit 1
}
warn() {
  if [ -n "${GITHUB_ACTIONS:-}" ]; then echo "::warning::$*"; else echo "warning: $*"; fi
}

[ -s "$SUMS" ] || error "$SUMS is missing"
[ ! -e "$SIG" ] || error "$SIG already exists"
if [ "${SIGN_SELFTEST:-}" = "true" ] && [ "${PUBLISHING:-}" = "true" ]; then error "the signing selftest is for trial runs only"; fi

TMP="$(mktemp -d)"
chmod 700 "$TMP"
trap 'rm -rf "$TMP"' EXIT

# Every PEM public-key block of the key file into its own file; # lines and anything outside the blocks are ignored.
KEYS=()
if [ -f "$KEYFILE" ]; then
  awk -v dir="$TMP" '
    /^-----BEGIN PUBLIC KEY-----/ { n++; f = sprintf("%s/embedded-%d.pem", dir, n); inblock = 1 }
    inblock { sub(/\r$/, ""); print > f }
    /^-----END PUBLIC KEY-----/ { inblock = 0; close(f) }
  ' "$KEYFILE"
  for k in "$TMP"/embedded-*.pem; do [ -e "$k" ] && KEYS+=("$k"); done
fi
echo "update keys in $(basename "$KEYFILE"): ${#KEYS[@]}"

# sign <private pem> <out .sig>: base64 one line, as the contract says.
sign() {
  openssl pkeyutl -sign -inkey "$1" -rawin -in "$SUMS" -out "$TMP/sig.bin"
  [ "$(wc -c < "$TMP/sig.bin")" -eq 64 ] || error "the signature is not 64 bytes: is the key Ed25519?"
  base64 -w0 "$TMP/sig.bin" > "$2"
  printf '\n' >> "$2"
}
# verify <public pem> <.sig> <data>
verify() {
  base64 -d "$2" > "$TMP/check.bin" 2>/dev/null || return 1
  openssl pkeyutl -verify -pubin -inkey "$1" -rawin -in "$3" -sigfile "$TMP/check.bin" >/dev/null 2>&1
}

made=""
if [ -n "${UPDATE_SIGNING_KEY:-}" ]; then
  printf '%s\n' "$UPDATE_SIGNING_KEY" > "$TMP/key.pem"
  openssl pkey -in "$TMP/key.pem" -noout 2>/dev/null || error "UPDATE_SIGNING_KEY is not a PEM private key"
  sign "$TMP/key.pem" "$SIG"
  if [ "${#KEYS[@]}" -eq 0 ]; then
    openssl pkey -in "$TMP/key.pem" -pubout -out "$TMP/own.pem"
    verify "$TMP/own.pem" "$SIG" "$SUMS" || error "SHA256SUMS.txt.sig does not verify with its own key"
    warn "SHA256SUMS.txt signed, but this build carries no update key: clients do not check it yet"
  else
    ok=""
    for k in "${KEYS[@]}"; do
      if verify "$k" "$SIG" "$SUMS"; then ok="$(basename "$k")"; break; fi
    done
    [ -n "$ok" ] || { rm -f "$SIG"; error "SHA256SUMS.txt.sig verifies with none of the ${#KEYS[@]} keys in $(basename "$KEYFILE"): UPDATE_SIGNING_KEY is not the embedded key"; }
    echo "SHA256SUMS.txt signature verified ($ok)"
  fi
  made=1
elif [ "${#KEYS[@]}" -gt 0 ] && [ "${PUBLISHING:-}" = "true" ]; then
  error "the app carries an update key but the UPDATE_SIGNING_KEY secret is missing: a release without SHA256SUMS.txt.sig would stop updates for those builds"
else
  warn "SHA256SUMS.txt is not signed (no UPDATE_SIGNING_KEY secret)"
fi

if [ "${SIGN_SELFTEST:-}" = "true" ]; then
  echo "== Update signature selftest (throwaway key)"
  openssl genpkey -algorithm ed25519 -out "$TMP/selftest.pem"
  openssl pkey -in "$TMP/selftest.pem" -pubout -out "$TMP/selftest.pub"
  sign "$TMP/selftest.pem" "$TMP/selftest.sig"
  verify "$TMP/selftest.pub" "$TMP/selftest.sig" "$SUMS" || error "selftest: the signature does not verify"
  [ "$(tr -d '\n' < "$TMP/selftest.sig" | wc -c)" -eq 88 ] || error "selftest: the .sig is not 88 base64 characters"
  # One changed byte of SHA256SUMS.txt must fail, and so must another key.
  cp "$SUMS" "$TMP/tampered.txt"
  printf 'x' | dd of="$TMP/tampered.txt" bs=1 seek=0 conv=notrunc status=none
  if verify "$TMP/selftest.pub" "$TMP/selftest.sig" "$TMP/tampered.txt"; then error "selftest: a changed SHA256SUMS.txt still verifies"; fi
  openssl genpkey -algorithm ed25519 -out "$TMP/other.pem"
  openssl pkey -in "$TMP/other.pem" -pubout -out "$TMP/other.pub"
  if verify "$TMP/other.pub" "$TMP/selftest.sig" "$SUMS"; then error "selftest: another key verifies the signature"; fi
  echo "selftest: signature verified, a changed byte and another key rejected"
  if [ -z "$made" ]; then
    cp "$TMP/selftest.sig" "$SIG"
    echo "selftest: left the throwaway SHA256SUMS.txt.sig in $DIST (trial run, nothing is published)"
  fi
fi
