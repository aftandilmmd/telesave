#!/usr/bin/env bash
# Builds the app, packs dist/TeleSave-<version>.dmg and, if NOTARY_PROFILE is set, notarizes + staples it.
#
# One-time notarization setup (Developer ID account required):
#   xcrun notarytool store-credentials telesave --apple-id <apple-id> --team-id <team-id>
# Then:  NOTARY_PROFILE=telesave scripts/make-dmg.sh
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="${VERSION:-0.1.0}"
export VERSION
scripts/build-app.sh

DMG="dist/TeleSave-${VERSION}.dmg"
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
cp -R dist/TeleSave.app "$STAGE/"
ln -s /Applications "$STAGE/Applications"
rm -f "$DMG"
hdiutil create -volname "TeleSave" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null

IDENTITY=$(codesign -dvv dist/TeleSave.app 2>&1 | sed -n 's/^Authority=\(Developer ID Application.*\)/\1/p' | head -1)
if [[ -n "$IDENTITY" ]]; then
  codesign --force --timestamp --sign "$IDENTITY" "$DMG"
fi

if [[ -n "${NOTARY_PROFILE:-}" ]]; then
  [[ -n "$IDENTITY" ]] || { echo "Notarization needs a Developer ID Application signature"; exit 1; }
  echo "› Notarizing"
  xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
  xcrun stapler staple "$DMG"
  spctl -a -t open --context context:primary-signature -v "$DMG"
else
  echo "› NOTARY_PROFILE not set: skipping notarization (Gatekeeper will warn on other Macs)"
fi
echo "✓ $DMG"
