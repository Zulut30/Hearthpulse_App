#!/usr/bin/env bash
# Linux packages of the cross-platform app (needs the .NET 10 SDK; dpkg-deb and rpmbuild for the packages):
#   dist/hearthlens_<version>_<amd64|arm64>.deb          Debian, Ubuntu, Mint, Pop!_OS…
#   dist/hearthlens-<version>-1.<x86_64|aarch64>.rpm     Fedora, openSUSE, RHEL…
#   dist/Hearthlens-<version>-linux-<x64|arm64>.tar.gz   any distribution: unpack and run ./install.sh
#   dist/Hearthlens-<version>-linux-<x64|arm64>.AppImage when appimagetool is installed
# Every package adds a menu entry, the icon and a desktop shortcut, and uninstalls cleanly.
set -euo pipefail

VERSION="${1:-0.7.8}"
ARCH="${2:-x64}"
case "$ARCH" in
  x64) DEB_ARCH=amd64; RPM_ARCH=x86_64; APPIMAGE_ARCH=x86_64 ;;
  arm64) DEB_ARCH=arm64; RPM_ARCH=aarch64; APPIMAGE_ARCH=aarch64 ;;
  *) echo "architecture must be x64 or arm64" >&2; exit 2 ;;
esac

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HERE="$ROOT/installer/linux"
ASSETS="$ROOT/installer/assets"
DIST="$ROOT/dist"
WORK="$DIST/work/linux-$ARCH"
rm -rf "$WORK"
mkdir -p "$WORK" "$DIST"

echo "== Publishing the app (linux-$ARCH, self-contained)"
dotnet publish "$ROOT/src/Hearthlens.Desktop/Hearthlens.Desktop.csproj" -c Release -r "linux-$ARCH" --self-contained true \
  -p:Version="$VERSION" -o "$WORK/app" -nologo -v q
chmod +x "$WORK/app/Hearthlens"

# ---- Shared system layout for .deb and .rpm -------------------------------------------------
SYS="$WORK/sysroot"
mkdir -p "$SYS/opt/hearthlens" "$SYS/usr/bin" "$SYS/usr/share/applications" "$SYS/usr/share/pixmaps"
cp -a "$WORK/app" "$SYS/opt/hearthlens/app"
ln -s /opt/hearthlens/app/Hearthlens "$SYS/usr/bin/hearthlens"
install -m 644 "$HERE/hearthlens.desktop" "$SYS/usr/share/applications/hearthlens.desktop"
for size in 16 24 32 48 64 128 256 512; do
  install -D -m 644 "$ASSETS/icons/hearthlens-$size.png" "$SYS/usr/share/icons/hicolor/${size}x${size}/apps/hearthlens.png"
done
install -m 644 "$ASSETS/icons/hearthlens-256.png" "$SYS/usr/share/pixmaps/hearthlens.png"

# Scripts shared by both package formats: desktop shortcut for the installing user, caches, clean removal.
cat > "$WORK/after-install.sh" <<'SH'
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t /usr/share/icons/hicolor || true
# Desktop shortcut for the person installing (sudo or a graphical installer via pkexec).
user="${SUDO_USER:-}"
[ -z "$user" ] && [ -n "${PKEXEC_UID:-}" ] && user="$(getent passwd "$PKEXEC_UID" | cut -d: -f1)"
if [ -n "$user" ] && [ "$user" != root ]; then
  home="$(getent passwd "$user" | cut -d: -f6)"
  desk="$(su "$user" -s /bin/sh -c 'xdg-user-dir DESKTOP' 2>/dev/null || true)"
  { [ -n "$desk" ] && [ "$desk" != "$home" ]; } || desk="$home/Desktop"
  if [ -d "$desk" ]; then
    install -m 755 -o "$user" /usr/share/applications/hearthlens.desktop "$desk/hearthlens.desktop" || true
    su "$user" -s /bin/sh -c "gio set '$desk/hearthlens.desktop' metadata::trusted true" 2>/dev/null || true
    mkdir -p /var/lib/hearthlens
    echo "$desk/hearthlens.desktop" >> /var/lib/hearthlens/desktop-shortcuts
  fi
fi
true
SH
cat > "$WORK/before-remove.sh" <<'SH'
pkill -x Hearthlens 2>/dev/null || true
if [ -f /var/lib/hearthlens/desktop-shortcuts ]; then
  while read -r shortcut; do rm -f "$shortcut"; done < /var/lib/hearthlens/desktop-shortcuts
  rm -rf /var/lib/hearthlens
fi
true
SH
cat > "$WORK/after-remove.sh" <<'SH'
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
true
SH

SUMMARY="Hearthstone companion: deck tracker, Battlegrounds advisor and in-game overlay"
DESCRIPTION="Hearthlens reads Hearthstone's own log files (the game runs under Wine, Lutris, Bottles or Steam/Proton) and shows a deck tracker, Battlegrounds advice, combat odds and your match history, plus an in-game overlay on X11 and XWayland. It never changes the game and never shows hidden information."

# ---- .deb ------------------------------------------------------------------------------------
if command -v dpkg-deb >/dev/null; then
  echo "== Building .deb"
  DEB="$WORK/deb"
  cp -a "$SYS" "$DEB"
  mkdir -p "$DEB/DEBIAN"
  SIZE_KB="$(du -sk "$DEB" | cut -f1)"
  ICU="libicu78 | libicu77 | libicu76 | libicu75 | libicu74 | libicu73 | libicu72 | libicu71 | libicu70 | libicu69 | libicu68 | libicu67 | libicu66"
  cat > "$DEB/DEBIAN/control" <<CONTROL
Package: hearthlens
Version: $VERSION
Architecture: $DEB_ARCH
Maintainer: Hearthlens <aqquastudios28@gmail.com>
Section: games
Priority: optional
Installed-Size: $SIZE_KB
Depends: libx11-6, libxext6, libxi6, libxrandr2, libxcursor1, libice6, libsm6, libfontconfig1, $ICU
Description: $SUMMARY
 $DESCRIPTION
CONTROL
  { echo '#!/bin/sh'; echo 'set -e'; cat "$WORK/after-install.sh"; } > "$DEB/DEBIAN/postinst"
  { echo '#!/bin/sh'; cat "$WORK/before-remove.sh"; } > "$DEB/DEBIAN/prerm"
  { echo '#!/bin/sh'; cat "$WORK/after-remove.sh"; } > "$DEB/DEBIAN/postrm"
  chmod 755 "$DEB/DEBIAN/postinst" "$DEB/DEBIAN/prerm" "$DEB/DEBIAN/postrm"
  dpkg-deb --root-owner-group -Zxz --build "$DEB" "$DIST/hearthlens_${VERSION}_${DEB_ARCH}.deb" >/dev/null
  echo "wrote $DIST/hearthlens_${VERSION}_${DEB_ARCH}.deb"
fi

# ---- .rpm ------------------------------------------------------------------------------------
if command -v rpmbuild >/dev/null; then
  echo "== Building .rpm"
  TOP="$WORK/rpm"
  mkdir -p "$TOP"/{BUILD,RPMS,SOURCES,SPECS,SRPMS}
  cat > "$TOP/SPECS/hearthlens.spec" <<SPEC
Name:           hearthlens
Version:        $VERSION
Release:        1
Summary:        $SUMMARY
License:        Proprietary
URL:            https://github.com/Dev-NotAqua/HeartLens
AutoReqProv:    no
Requires:       libX11, libXext, libXi, libXrandr, libXcursor, libICE, libSM, fontconfig, libicu
%global debug_package %{nil}
%global _build_id_links none
%global __os_install_post %{nil}

%description
$DESCRIPTION

%install
mkdir -p %{buildroot}
cp -a $SYS/. %{buildroot}/

%files
/opt/hearthlens
/usr/bin/hearthlens
/usr/share/applications/hearthlens.desktop
/usr/share/icons/hicolor/*/apps/hearthlens.png
/usr/share/pixmaps/hearthlens.png

%post
$(cat "$WORK/after-install.sh")

%preun
if [ "\$1" = 0 ]; then
$(cat "$WORK/before-remove.sh")
fi

%postun
$(cat "$WORK/after-remove.sh")
SPEC
  # The architecture comes from --target only: a BuildArch line makes rpmbuild refuse to
  # package arm64 on an x86_64 machine ("No compatible architectures found for build").
  rpmbuild --quiet --define "_topdir $TOP" --target "$RPM_ARCH" -bb "$TOP/SPECS/hearthlens.spec" >/dev/null
  cp "$TOP/RPMS/$RPM_ARCH/hearthlens-$VERSION-1.$RPM_ARCH.rpm" "$DIST/"
  echo "wrote $DIST/hearthlens-$VERSION-1.$RPM_ARCH.rpm"
fi

# ---- Portable .tar.gz with install / uninstall scripts ---------------------------------------
echo "== Building .tar.gz"
NAME="Hearthlens-$VERSION-linux-$ARCH"
PKG="$WORK/$NAME"
mkdir -p "$PKG/icons"
cp -a "$WORK/app" "$PKG/app"
cp "$HERE/install.sh" "$HERE/uninstall.sh" "$HERE/hearthlens.desktop" "$PKG/"
cp "$ASSETS"/icons/hearthlens-*.png "$PKG/icons/"
cp "$HERE/README.txt" "$PKG/README.txt"
chmod +x "$PKG/install.sh" "$PKG/uninstall.sh"
tar -C "$WORK" -czf "$DIST/$NAME.tar.gz" "$NAME"
echo "wrote $DIST/$NAME.tar.gz"

# ---- AppImage (optional) ---------------------------------------------------------------------
if command -v appimagetool >/dev/null; then
  echo "== Building AppImage"
  APPDIR="$WORK/Hearthlens.AppDir"
  mkdir -p "$APPDIR/usr"
  cp -a "$WORK/app" "$APPDIR/usr/lib"
  cp "$HERE/hearthlens.desktop" "$APPDIR/hearthlens.desktop"
  cp "$ASSETS/icons/hearthlens-256.png" "$APPDIR/hearthlens.png"
  cat > "$APPDIR/AppRun" <<'SH'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/lib/Hearthlens" "$@"
SH
  chmod +x "$APPDIR/AppRun"
  # Optional extra: a failure here (e.g. no network for the AppImage runtime) must not lose the other packages.
  if ARCH="$APPIMAGE_ARCH" appimagetool --no-appstream "$APPDIR" "$DIST/Hearthlens-$VERSION-linux-$ARCH.AppImage" >/dev/null; then
    echo "wrote $DIST/Hearthlens-$VERSION-linux-$ARCH.AppImage"
  else
    echo "AppImage not built (appimagetool failed)" >&2
  fi
fi
