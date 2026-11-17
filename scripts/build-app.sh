#!/usr/bin/env bash
# Builds dist/TeleSave.app: release binary, Info.plist, icon, sandbox entitlements, code signature.
#
# Env:
#   VERSION        marketing version (default 0.1.0)
#   BUILD          build number (default: timestamp)
#   BUNDLE_ID      bundle identifier (default com.telesave.app)
#   ARCHS          "arm64 x86_64" (universal, default) or "arm64"
#   SIGN_IDENTITY  codesign identity; default: first "Developer ID Application", else "Apple Development", else ad-hoc
#   Config/Secrets.env with TG_API_ID / TG_API_HASH bundles an API key (optional).
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="${VERSION:-0.1.0}"
BUILD="${BUILD:-$(date +%Y%m%d%H%M)}"
BUNDLE_ID="${BUNDLE_ID:-com.telesave.app}"
ARCHS="${ARCHS:-arm64 x86_64}"
APP="${APP:-dist/TeleSave.app}"
ENTITLEMENTS="${ENTITLEMENTS:-Config/TeleSave.entitlements}"
SWIFT_FLAGS=(${SWIFT_FLAGS:-})   # e.g. "-Xswiftc -DAPPSTORE"
PROVISIONING_PROFILE="${PROVISIONING_PROFILE:-}"

TG_API_ID=""; TG_API_HASH=""; TG_CONFIG_URLS=""; TG_CONFIG_PUBLIC_KEY=""
if [[ -f Config/Secrets.env ]]; then
  # shellcheck disable=SC1091
  source Config/Secrets.env
fi
API_HASH_OBF=""
if [[ -n "${TG_API_ID}" && -n "${TG_API_HASH}" ]]; then
  # Must match TeleSaveCore.Obfuscator.
  API_HASH_OBF=$(python3 -c 'import sys,base64;k=b"TeleSave.obfuscation.v1";d=sys.argv[1].encode();print(base64.b64encode(bytes(b^k[i%len(k)] for i,b in enumerate(d))).decode())' "$TG_API_HASH")
  echo "› Bundling API key (api_id ${TG_API_ID})"
else
  echo "› No Config/Secrets.env: users will enter their own api_id/api_hash on first run"
fi

if [[ -n "${TG_CONFIG_URLS}" && -n "${TG_CONFIG_PUBLIC_KEY}" ]]; then
  echo "› Remote config enabled: ${TG_CONFIG_URLS}"
elif [[ -n "${TG_CONFIG_URLS}${TG_CONFIG_PUBLIC_KEY}" ]]; then
  echo "TG_CONFIG_URLS and TG_CONFIG_PUBLIC_KEY must be set together"; exit 1
fi

ARCH_FLAGS=()
for a in $ARCHS; do ARCH_FLAGS+=(--arch "$a"); done

echo "› Building release ($ARCHS)"
swift build -c release --product TeleSave "${ARCH_FLAGS[@]}" ${SWIFT_FLAGS[@]+"${SWIFT_FLAGS[@]}"} 2>&1 | grep -vE "ld: warning: object file .* was built for newer" || true
BIN_DIR=$(swift build -c release --product TeleSave "${ARCH_FLAGS[@]}" ${SWIFT_FLAGS[@]+"${SWIFT_FLAGS[@]}"} --show-bin-path)
[[ -x "$BIN_DIR/TeleSave" ]] || { echo "build failed: $BIN_DIR/TeleSave missing"; exit 1; }

echo "› Assembling $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN_DIR/TeleSave" "$APP/Contents/MacOS/TeleSave"
# SwiftPM resource bundles (UI translations). Bundle.module looks for them in Contents/Resources.
for bundle in "$BIN_DIR"/*.bundle; do
  [[ -d "$bundle" ]] && cp -R "$bundle" "$APP/Contents/Resources/"
done
strip -x "$APP/Contents/MacOS/TeleSave"
sed -e "s/__BUNDLE_ID__/${BUNDLE_ID}/" \
    -e "s/__VERSION__/${VERSION}/" \
    -e "s/__BUILD__/${BUILD}/" \
    -e "s/__API_ID__/${TG_API_ID}/" \
    -e "s#__API_HASH__#${API_HASH_OBF}#" \
    -e "s#__CONFIG_URLS__#${TG_CONFIG_URLS}#" \
    -e "s#__CONFIG_PUBLIC_KEY__#${TG_CONFIG_PUBLIC_KEY}#" \
    Config/Info.plist > "$APP/Contents/Info.plist"
plutil -lint "$APP/Contents/Info.plist" >/dev/null
printf 'APPL????' > "$APP/Contents/PkgInfo"

# Finder "Send to TeleSave" menu title per language, from the same translation files the UI uses.
python3 - "$APP/Contents/Resources" <<'PY'
import json, pathlib, sys
resources = pathlib.Path(sys.argv[1])
for file in sorted(pathlib.Path("Sources/TeleSaveCore/Resources/Localization").glob("*.json")):
    title = json.loads(file.read_text())["strings"]["TeleSave'e gönder"].replace('"', '\\"')
    lproj = resources / f"{file.stem}.lproj"
    lproj.mkdir(exist_ok=True)
    (lproj / "ServicesMenu.strings").write_text(f'"Send to TeleSave" = "{title}";\n', encoding="utf-8")
PY

if [[ ! -f Config/AppIcon.icns ]]; then
  echo "› Rendering icon"
  swift scripts/make-icon.swift Config/AppIcon.icns
fi
cp Config/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"

if [[ -z "${SIGN_IDENTITY:-}" ]]; then
  SIGN_IDENTITY=$(security find-identity -v -p codesigning | grep -o '"Developer ID Application[^"]*"' | head -1 | tr -d '"' || true)
  [[ -z "$SIGN_IDENTITY" ]] && SIGN_IDENTITY=$(security find-identity -v -p codesigning | grep -o '"Apple Development[^"]*"' | head -1 | tr -d '"' || true)
  [[ -z "$SIGN_IDENTITY" ]] && SIGN_IDENTITY="-"
fi
echo "› Signing with: $SIGN_IDENTITY"
if [[ -n "$PROVISIONING_PROFILE" ]]; then
  cp "$PROVISIONING_PROFILE" "$APP/Contents/embedded.provisionprofile"
fi
SIGN_ARGS=(--force --options runtime --entitlements "$ENTITLEMENTS" --sign "$SIGN_IDENTITY")
[[ "$SIGN_IDENTITY" == Developer\ ID* ]] && SIGN_ARGS+=(--timestamp)
codesign "${SIGN_ARGS[@]}" "$APP"
codesign --verify --strict --verbose=1 "$APP"

echo "✓ $APP ($(du -sh "$APP" | cut -f1))"
