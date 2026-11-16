#!/usr/bin/env bash
# Packages a published Linux build as AppImage, .deb, .rpm and .tar.gz.
#   package.sh <x64|arm64> <version> <publish dir> <output dir>
set -euo pipefail
ARCH="$1"; VERSION="$2"; PUBLISH="$(cd "$3" && pwd)"; OUT="$(mkdir -p "$4" && cd "$4" && pwd)"
HERE="$(cd "$(dirname "$0")" && pwd)"
ICON="$HERE/../../src/TeleSave.Shell/icon.png"
case "$ARCH" in
  x64)   DEB_ARCH=amd64; RPM_ARCH=x86_64;  AI_ARCH=x86_64 ;;
  arm64) DEB_ARCH=arm64; RPM_ARCH=aarch64; AI_ARCH=aarch64 ;;
  *) echo "unknown arch $ARCH"; exit 1 ;;
esac
NAME="TeleSave-$VERSION-linux-$ARCH"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

desktop_entry() { # $1 = Exec command
  cat <<DESKTOP
[Desktop Entry]
Type=Application
Name=TeleSave
Comment=Upload files to Telegram
Exec=$1 --send %F
Icon=telesave
Terminal=false
Categories=Network;FileTransfer;
MimeType=application/octet-stream;inode/directory;image/png;image/jpeg;video/mp4;application/pdf;
StartupWMClass=telesave
DESKTOP
}

# Shared filesystem layout: /opt/telesave + /usr/bin/telesave + menu entry + icon.
ROOT="$WORK/root"
mkdir -p "$ROOT/opt/telesave" "$ROOT/usr/bin" "$ROOT/usr/share/applications" "$ROOT/usr/share/icons/hicolor/256x256/apps"
cp -a "$PUBLISH/." "$ROOT/opt/telesave/"
chmod 755 "$ROOT/opt/telesave/telesave"
ln -s ../../opt/telesave/telesave "$ROOT/usr/bin/telesave"
desktop_entry /usr/bin/telesave > "$ROOT/usr/share/applications/telesave.desktop"
cp "$ICON" "$ROOT/usr/share/icons/hicolor/256x256/apps/telesave.png"

# tar.gz (portable)
mkdir -p "$WORK/tar/telesave"
cp -a "$PUBLISH/." "$WORK/tar/telesave/"
tar -C "$WORK/tar" -czf "$OUT/$NAME.tar.gz" telesave

# .deb (Debian, Ubuntu, Mint, …)
mkdir -p "$ROOT/DEBIAN"
cat > "$ROOT/DEBIAN/control" <<CONTROL
Package: telesave
Version: $VERSION
Architecture: $DEB_ARCH
Maintainer: TeleSave <https://github.com/aftandilmmd/telesave>
Depends: libc6 (>= 2.35), libfontconfig1, libx11-6, libice6, libsm6
Recommends: libsecret-tools, libnotify-bin, xclip | wl-clipboard
Section: net
Priority: optional
Homepage: https://github.com/aftandilmmd/telesave
Description: Drop a file, it lands in Telegram
 Tray app that uploads files you drop on it to your Telegram Saved Messages
 or any chat, group or channel you choose.
CONTROL
dpkg-deb --root-owner-group --build "$ROOT" "$OUT/$NAME.deb"
rm -rf "$ROOT/DEBIAN"

# .rpm (Fedora, RHEL, openSUSE, …)
mkdir -p "$WORK/rpm/"{BUILD,RPMS,SOURCES,SPECS,SRPMS}
cat > "$WORK/rpm/SPECS/telesave.spec" <<SPEC
Name: telesave
Version: $VERSION
Release: 1
Summary: Drop a file, it lands in Telegram
License: Proprietary
URL: https://github.com/aftandilmmd/telesave
AutoReqProv: no
Requires: fontconfig, libX11, libICE, libSM
Recommends: libsecret, libnotify

%description
Tray app that uploads files you drop on it to your Telegram Saved Messages
or any chat, group or channel you choose.

%install
cp -a $ROOT/. %{buildroot}/

%files
/opt/telesave
/usr/bin/telesave
/usr/share/applications/telesave.desktop
/usr/share/icons/hicolor/256x256/apps/telesave.png
SPEC
rpmbuild --define "_topdir $WORK/rpm" --target "$RPM_ARCH" -bb "$WORK/rpm/SPECS/telesave.spec" >/dev/null
cp "$WORK"/rpm/RPMS/*/*.rpm "$OUT/$NAME.rpm"

# AppImage (any distribution)
APPDIR="$WORK/TeleSave.AppDir"
mkdir -p "$APPDIR/usr/lib/telesave"
cp -a "$PUBLISH/." "$APPDIR/usr/lib/telesave/"
cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/sh
exec "$APPDIR/usr/lib/telesave/telesave" "$@"
APPRUN
chmod 755 "$APPDIR/AppRun"
desktop_entry telesave > "$APPDIR/telesave.desktop"
cp "$ICON" "$APPDIR/telesave.png"
TOOL="$WORK/appimagetool"
curl -fsSL -o "$TOOL" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-$AI_ARCH.AppImage"
chmod +x "$TOOL"
ARCH="$AI_ARCH" APPIMAGE_EXTRACT_AND_RUN=1 "$TOOL" --no-appstream "$APPDIR" "$OUT/$NAME.AppImage" >/dev/null

ls -la "$OUT"
