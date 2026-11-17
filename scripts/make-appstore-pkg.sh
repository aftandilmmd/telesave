#!/usr/bin/env bash
# Builds the Mac App Store package: dist/appstore/TeleSave-<version>.pkg
#
# Env:
#   VERSION, BUILD            marketing version and (always increasing) build number
#   TEAM_ID                   Apple Developer team id
#   APP_IDENTITY              "Apple Distribution: …" (or "3rd Party Mac Developer Application: …")
#   INSTALLER_IDENTITY        "3rd Party Mac Developer Installer: …"
#   PROVISIONING_PROFILE      Mac App Store provisioning profile for com.telesave.app
# Upload afterwards with scripts/upload-appstore.sh.
set -euo pipefail
cd "$(dirname "$0")/.."
: "${VERSION:?}" "${BUILD:?}" "${TEAM_ID:?}" "${APP_IDENTITY:?}" "${INSTALLER_IDENTITY:?}" "${PROVISIONING_PROFILE:?}"
BUNDLE_ID="${BUNDLE_ID:-com.telesave.app}"

mkdir -p dist/appstore
sed -e "s/__TEAM_ID__/$TEAM_ID/g" -e "s/__BUNDLE_ID__/$BUNDLE_ID/g" Config/TeleSave-AppStore.entitlements > dist/appstore/entitlements.plist

APP=dist/appstore/TeleSave.app \
ENTITLEMENTS=dist/appstore/entitlements.plist \
SWIFT_FLAGS="-Xswiftc -DAPPSTORE" \
SIGN_IDENTITY="$APP_IDENTITY" \
PROVISIONING_PROFILE="$PROVISIONING_PROFILE" \
VERSION="$VERSION" BUILD="$BUILD" BUNDLE_ID="$BUNDLE_ID" \
  scripts/build-app.sh

# Export compliance: TDLib uses encryption (TLS and Telegram's MTProto) to talk to Telegram's servers.
/usr/libexec/PlistBuddy -c "Add :ITSAppUsesNonExemptEncryption bool ${USES_NON_EXEMPT_ENCRYPTION:-true}" dist/appstore/TeleSave.app/Contents/Info.plist 2>/dev/null \
  || /usr/libexec/PlistBuddy -c "Set :ITSAppUsesNonExemptEncryption ${USES_NON_EXEMPT_ENCRYPTION:-true}" dist/appstore/TeleSave.app/Contents/Info.plist
# Info.plist changed after signing: sign again.
codesign --force --options runtime --entitlements dist/appstore/entitlements.plist --sign "$APP_IDENTITY" dist/appstore/TeleSave.app
codesign --verify --strict dist/appstore/TeleSave.app

PKG="dist/appstore/TeleSave-$VERSION.pkg"
productbuild --component dist/appstore/TeleSave.app /Applications --sign "$INSTALLER_IDENTITY" "$PKG"
echo "✓ $PKG"
