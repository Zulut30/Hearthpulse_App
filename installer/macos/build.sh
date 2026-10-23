#!/usr/bin/env bash
# macOS packages of the cross-platform app, for Apple silicon (arm64) and Intel (x64). Needs the .NET 10 SDK.
#   dist/Hearthlens-<version>-macOS-<arch>.zip   Hearthlens.app + "Install Hearthlens" / "Uninstall Hearthlens" (any OS can build it)
# On a Mac it also builds:
#   dist/Hearthlens-<version>-macOS-<arch>.dmg   drag-to-Applications disk image with the uninstaller
#   dist/Hearthlens-<version>-macOS-<arch>.pkg   setup wizard that also adds a Desktop shortcut
set -euo pipefail

VERSION="${1:-0.7.8}"
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
  APP="$WORK/Hearthlens.app"
  mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

  echo "== Publishing the app (osx-$ARCH, self-contained)"
  dotnet publish "$ROOT/src/Hearthlens.Desktop/Hearthlens.Desktop.csproj" -c Release -r "osx-$ARCH" --self-contained true \
    -p:Version="$VERSION" -p:UseAppHost=true -o "$APP/Contents/MacOS" -nologo -v q
  chmod +x "$APP/Contents/MacOS/Hearthlens"
  sed "s/@VERSION@/$VERSION/g" "$HERE/Info.plist" > "$APP/Contents/Info.plist"
  cp "$ASSETS/hearthlens.icns" "$APP/Contents/Resources/hearthlens.icns"
  printf 'APPL????' > "$APP/Contents/PkgInfo"

  if [ "$ON_MAC" -eq 1 ]; then
    # Apple silicon only runs signed code: an ad-hoc signature (no Apple account) is enough to launch.
    codesign --force --deep --sign - "$APP"
  fi

  # ---- .zip with install / uninstall scripts (built anywhere) --------------------------------
  echo "== Building .zip"
  PKG="$WORK/Hearthlens"
  mkdir -p "$PKG"
  cp -R "$APP" "$PKG/"
  cp "$HERE/Install Hearthlens.command" "$HERE/Uninstall Hearthlens.command" "$HERE/README.txt" "$PKG/"
  chmod +x "$PKG/Install Hearthlens.command" "$PKG/Uninstall Hearthlens.command"
  ZIP="$DIST/Hearthlens-$VERSION-macOS-$ARCH.zip"
  rm -f "$ZIP"
  if [ "$ON_MAC" -eq 1 ]; then
    (cd "$WORK" && ditto -c -k --sequesterRsrc --keepParent Hearthlens "$ZIP")
  else
    # -y keeps symlinks inside the .NET runtime; Info-ZIP stores Unix permissions, so the app stays executable.
    (cd "$WORK" && zip -qry -9 "$ZIP" Hearthlens)
  fi
  echo "wrote $ZIP"

  if [ "$ON_MAC" -eq 0 ]; then
    continue
  fi

  # ---- .dmg ----------------------------------------------------------------------------------
  echo "== Building .dmg"
  DMGROOT="$WORK/dmg"
  mkdir -p "$DMGROOT"
  cp -R "$APP" "$DMGROOT/"
  ln -s /Applications "$DMGROOT/Applications"
  cp "$HERE/Uninstall Hearthlens.command" "$HERE/README.txt" "$DMGROOT/"
  DMG="$DIST/Hearthlens-$VERSION-macOS-$ARCH.dmg"
  rm -f "$DMG"
  hdiutil create -volname "Hearthlens" -srcfolder "$DMGROOT" -fs HFS+ -format UDZO -ov "$DMG" >/dev/null
  echo "wrote $DMG"

  # ---- .pkg setup wizard -----------------------------------------------------------------------
  echo "== Building .pkg"
  PKGROOT="$WORK/pkgroot"
  mkdir -p "$PKGROOT/Applications" "$WORK/scripts" "$WORK/resources"
  cp -R "$APP" "$PKGROOT/Applications/"
  cat > "$WORK/scripts/postinstall" <<'SH'
#!/bin/bash
# Runs as root after the files are in place: clear quarantine, sign locally, Desktop shortcut for the signed-in user.
APP="/Applications/Hearthlens.app"
xattr -dr com.apple.quarantine "$APP" >/dev/null 2>&1 || true
codesign --force --deep --sign - "$APP" >/dev/null 2>&1 || true
user="$(stat -f%Su /dev/console)"
if [ -n "$user" ] && [ "$user" != "root" ]; then
  home="$(dscl . -read "/Users/$user" NFSHomeDirectory | awk '{print $2}')"
  sudo -u "$user" rm -rf "$home/Desktop/Hearthlens"
  sudo -u "$user" ln -s "$APP" "$home/Desktop/Hearthlens"
  sudo -u "$user" mkdir -p "$home/Library/Application Support/Hearthlens"
  cp "/Applications/Hearthlens.app/Contents/Resources/Uninstall Hearthlens.command" \
     "$home/Library/Application Support/Hearthlens/Uninstall Hearthlens.command" 2>/dev/null || true
  chown "$user" "$home/Library/Application Support/Hearthlens/Uninstall Hearthlens.command" 2>/dev/null || true
fi
exit 0
SH
  chmod +x "$WORK/scripts/postinstall"
  # Ship the uninstaller inside the bundle so the postinstall can hand it to the user.
  cp "$HERE/Uninstall Hearthlens.command" "$PKGROOT/Applications/Hearthlens.app/Contents/Resources/"
  codesign --force --deep --sign - "$PKGROOT/Applications/Hearthlens.app"

  cp "$ASSETS/hearthlens.png" "$WORK/resources/background.png"
  cat > "$WORK/resources/welcome.html" <<HTML
<html><body style="font-family:-apple-system,Helvetica;font-size:13px">
<h2>Hearthlens $VERSION</h2>
<p>A Hearthstone companion: deck tracker, Battlegrounds advisor, combat odds, in-game overlay and match history.</p>
<p>It reads the log files Hearthstone writes and never changes the game. Hearthlens is installed in Applications and a shortcut is added to your Desktop.</p>
<p>Not affiliated with Blizzard Entertainment.</p>
</body></html>
HTML
  cat > "$WORK/resources/conclusion.html" <<'HTML'
<html><body style="font-family:-apple-system,Helvetica;font-size:13px">
<h2>Hearthlens is installed</h2>
<p>Open it from Applications or the Desktop shortcut. Restart Hearthstone once so it starts writing the logs Hearthlens reads.</p>
<p>To uninstall, run "Uninstall Hearthlens" in ~/Library/Application Support/Hearthlens.</p>
</body></html>
HTML
  HOST_ARCH=$([ "$ARCH" = "arm64" ] && echo arm64 || echo x86_64)
  cat > "$WORK/distribution.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="2">
    <title>Hearthlens</title>
    <welcome file="welcome.html"/>
    <conclusion file="conclusion.html"/>
    <background file="background.png" alignment="bottomleft" scaling="proportional"/>
    <options customize="never" require-scripts="false" hostArchitectures="$HOST_ARCH"/>
    <domains enable_localSystem="true"/>
    <choices-outline><line choice="default"><line choice="app"/></line></choices-outline>
    <choice id="default"/>
    <choice id="app" visible="false"><pkg-ref id="app.hearthlens.desktop"/></choice>
    <pkg-ref id="app.hearthlens.desktop" version="$VERSION" onConclusion="none">component.pkg</pkg-ref>
</installer-gui-script>
XML
  pkgbuild --root "$PKGROOT" --identifier app.hearthlens.desktop --version "$VERSION" --install-location / \
    --scripts "$WORK/scripts" "$WORK/component.pkg" >/dev/null
  OUT_PKG="$DIST/Hearthlens-$VERSION-macOS-$ARCH.pkg"
  productbuild --distribution "$WORK/distribution.xml" --resources "$WORK/resources" --package-path "$WORK" "$OUT_PKG" >/dev/null
  echo "wrote $OUT_PKG"
done
