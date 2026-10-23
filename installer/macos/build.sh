#!/usr/bin/env bash
# macOS packages of the cross-platform app, for Apple silicon (arm64) and Intel (x64). Needs the .NET 10 SDK.
#   dist/Hearthpulse-<version>-macOS-<arch>.zip   Hearthpulse.app + "Install Hearthpulse" / "Uninstall Hearthpulse" (any OS can build it)
# On a Mac it also builds:
#   dist/Hearthpulse-<version>-macOS-<arch>.dmg   drag-to-Applications disk image with the uninstaller
#   dist/Hearthpulse-<version>-macOS-<arch>.pkg   setup wizard that also adds a Desktop shortcut
set -euo pipefail

VERSION="${1:-0.11.1}"
shift || true
ARCHS=("${@:-arm64 x64}")
read -r -a ARCHS <<< "${ARCHS[*]}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HERE="$ROOT/installer/macos"
ASSETS="$ROOT/installer/assets"
DIST="$ROOT/dist"
mkdir -p "$DIST"
ON_MAC=0
[ "$(uname -s)" = "Darwin" ] && ON_MAC=1

for ARCH in "${ARCHS[@]}"; do
  WORK="$DIST/work/macos-$ARCH"
  rm -rf "$WORK"
  APP="$WORK/Hearthpulse.app"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

  echo "== Publishing the app (osx-$ARCH, self-contained)"
  dotnet publish "$ROOT/src/Hearthpulse.Desktop/Hearthpulse.Desktop.csproj" -c Release -r "osx-$ARCH" --self-contained true \
    -p:Version="$VERSION" -p:UseAppHost=true -o "$APP/Contents/MacOS" -nologo -v q
  chmod +x "$APP/Contents/MacOS/Hearthpulse"
  sed "s/@VERSION@/$VERSION/g" "$HERE/Info.plist" > "$APP/Contents/Info.plist"
  cp "$ASSETS/hearthpulse.icns" "$APP/Contents/Resources/hearthpulse.icns"
  printf 'APPL????' > "$APP/Contents/PkgInfo"

  if [ "$ON_MAC" -eq 1 ]; then
    # Apple silicon only runs signed code: an ad-hoc signature (no Apple account) is enough to launch.
    codesign --force --deep --sign - "$APP"
  fi

  # ---- .zip with install / uninstall scripts (built anywhere) --------------------------------
  echo "== Building .zip"
  PKG="$WORK/Hearthpulse"
  mkdir -p "$PKG"
  cp -R "$APP" "$PKG/"
  cp "$HERE/Install Hearthpulse.command" "$HERE/Uninstall Hearthpulse.command" "$HERE/README.txt" "$PKG/"
  chmod +x "$PKG/Install Hearthpulse.command" "$PKG/Uninstall Hearthpulse.command"
  ZIP="$DIST/Hearthpulse-$VERSION-macOS-$ARCH.zip"
  rm -f "$ZIP"
  if [ "$ON_MAC" -eq 1 ]; then
    (cd "$WORK" && ditto -c -k --sequesterRsrc --keepParent Hearthpulse "$ZIP")
  else
    # -y keeps symlinks inside the .NET runtime; Info-ZIP stores Unix permissions, so the app stays executable.
    (cd "$WORK" && zip -qry -9 "$ZIP" Hearthpulse)
  fi
  echo "wrote $ZIP"

  # ---- Portable .zip: run Hearthpulse.app from anywhere; data goes to "Hearthpulse Data" next to it ----
  echo "== Building portable .zip"
  PORT="$WORK/portable"
  rm -rf "$PORT"
  mkdir -p "$PORT/Hearthpulse"
  cp -R "$APP" "$PORT/Hearthpulse/"
  echo "Portable copy: settings and history are kept in the 'Hearthpulse Data' folder next to Hearthpulse.app." \
    > "$PORT/Hearthpulse/Hearthpulse.app/Contents/MacOS/portable.txt"
  if [ "$ON_MAC" -eq 1 ]; then
    # The added file changes the bundle, so it is signed again.
    codesign --force --deep --sign - "$PORT/Hearthpulse/Hearthpulse.app"
  fi
  PZIP="$DIST/Hearthpulse-Portable-$VERSION-macOS-$ARCH.zip"
  rm -f "$PZIP"
  if [ "$ON_MAC" -eq 1 ]; then
    (cd "$PORT" && ditto -c -k --sequesterRsrc --keepParent Hearthpulse "$PZIP")
  else
    (cd "$PORT" && zip -qry -9 "$PZIP" Hearthpulse)
  fi
  echo "wrote $PZIP"

  if [ "$ON_MAC" -eq 0 ]; then
    continue
  fi

  # ---- .dmg ----------------------------------------------------------------------------------
  echo "== Building .dmg"
  DMGROOT="$WORK/dmg"
  mkdir -p "$DMGROOT"
  cp -R "$APP" "$DMGROOT/"
  ln -s /Applications "$DMGROOT/Applications"
  cp "$HERE/Uninstall Hearthpulse.command" "$HERE/README.txt" "$DMGROOT/"
  DMG="$DIST/Hearthpulse-$VERSION-macOS-$ARCH.dmg"
  rm -f "$DMG"
  hdiutil create -volname "Hearthpulse" -srcfolder "$DMGROOT" -fs HFS+ -format UDZO -ov "$DMG" >/dev/null
  echo "wrote $DMG"

  # ---- .pkg setup wizard -----------------------------------------------------------------------
  echo "== Building .pkg"
  PKGROOT="$WORK/pkgroot"
  mkdir -p "$PKGROOT/Applications" "$WORK/scripts" "$WORK/resources"
  cp -R "$APP" "$PKGROOT/Applications/"
  cat > "$WORK/scripts/postinstall" <<'SH'
#!/bin/bash
# Runs as root after the files are in place: clear quarantine, sign locally, Desktop shortcut for the signed-in user.
APP="/Applications/Hearthpulse.app"
xattr -dr com.apple.quarantine "$APP" >/dev/null 2>&1 || true
codesign --force --deep --sign - "$APP" >/dev/null 2>&1 || true
user="$(stat -f%Su /dev/console)"
if [ -n "$user" ] && [ "$user" != "root" ]; then
  home="$(dscl . -read "/Users/$user" NFSHomeDirectory | awk '{print $2}')"
  sudo -u "$user" rm -rf "$home/Desktop/Hearthpulse"
  sudo -u "$user" ln -s "$APP" "$home/Desktop/Hearthpulse"
  sudo -u "$user" mkdir -p "$home/Library/Application Support/Hearthpulse"
  cp "/Applications/Hearthpulse.app/Contents/Resources/Uninstall Hearthpulse.command" \
     "$home/Library/Application Support/Hearthpulse/Uninstall Hearthpulse.command" 2>/dev/null || true
  chown "$user" "$home/Library/Application Support/Hearthpulse/Uninstall Hearthpulse.command" 2>/dev/null || true
fi
exit 0
SH
  chmod +x "$WORK/scripts/postinstall"
  # Ship the uninstaller inside the bundle so the postinstall can hand it to the user.
  cp "$HERE/Uninstall Hearthpulse.command" "$PKGROOT/Applications/Hearthpulse.app/Contents/Resources/"
  codesign --force --deep --sign - "$PKGROOT/Applications/Hearthpulse.app"

  cp "$ASSETS/hearthpulse.png" "$WORK/resources/background.png"
  cat > "$WORK/resources/welcome.html" <<HTML
<html><body style="font-family:-apple-system,Helvetica;font-size:13px">
<h2>Hearthpulse $VERSION</h2>
<p>A Hearthstone companion: deck tracker, Battlegrounds advisor, combat odds, in-game overlay and match history.</p>
<p>It reads the log files Hearthstone writes and never changes the game. Hearthpulse is installed in Applications and a shortcut is added to your Desktop.</p>
<p>Not affiliated with Blizzard Entertainment.</p>
</body></html>
HTML
  cat > "$WORK/resources/conclusion.html" <<'HTML'
<html><body style="font-family:-apple-system,Helvetica;font-size:13px">
<h2>Hearthpulse is installed</h2>
<p>Open it from Applications or the Desktop shortcut. Restart Hearthstone once so it starts writing the logs Hearthpulse reads.</p>
<p>To uninstall, run "Uninstall Hearthpulse" in ~/Library/Application Support/Hearthpulse.</p>
</body></html>
HTML
  HOST_ARCH=$([ "$ARCH" = "arm64" ] && echo arm64 || echo x86_64)
  cat > "$WORK/distribution.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="2">
    <title>Hearthpulse</title>
    <welcome file="welcome.html"/>
    <conclusion file="conclusion.html"/>
    <background file="background.png" alignment="bottomleft" scaling="proportional"/>
    <options customize="never" require-scripts="false" hostArchitectures="$HOST_ARCH"/>
    <domains enable_localSystem="true"/>
    <choices-outline><line choice="default"><line choice="app"/></line></choices-outline>
    <choice id="default"/>
    <choice id="app" visible="false"><pkg-ref id="app.hearthpulse.desktop"/></choice>
    <pkg-ref id="app.hearthpulse.desktop" version="$VERSION" onConclusion="none">component.pkg</pkg-ref>
</installer-gui-script>
XML
  pkgbuild --root "$PKGROOT" --identifier app.hearthpulse.desktop --version "$VERSION" --install-location / \
    --scripts "$WORK/scripts" "$WORK/component.pkg" >/dev/null
  OUT_PKG="$DIST/Hearthpulse-$VERSION-macOS-$ARCH.pkg"
  productbuild --distribution "$WORK/distribution.xml" --resources "$WORK/resources" --package-path "$WORK" "$OUT_PKG" >/dev/null
  echo "wrote $OUT_PKG"
done
