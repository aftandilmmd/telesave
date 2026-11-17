#!/usr/bin/env bash
# Uploads a .pkg to App Store Connect with an App Store Connect API key.
#   ASC_KEY_ID, ASC_ISSUER_ID   (key file at ~/.appstoreconnect/private_keys/AuthKey_<ASC_KEY_ID>.p8)
#   upload-appstore.sh dist/appstore/TeleSave-<version>.pkg
set -euo pipefail
: "${ASC_KEY_ID:?}" "${ASC_ISSUER_ID:?}"
xcrun altool --validate-app -f "$1" -t macos --apiKey "$ASC_KEY_ID" --apiIssuer "$ASC_ISSUER_ID"
xcrun altool --upload-app -f "$1" -t macos --apiKey "$ASC_KEY_ID" --apiIssuer "$ASC_ISSUER_ID"
