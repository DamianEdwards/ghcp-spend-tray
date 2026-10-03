#!/bin/bash
set -euo pipefail
artifact="${1:?Supply the signed archive or disk image}"
label="${2:?Supply app or dmg}"
case "$label" in app|dmg) ;; *) echo "Invalid notarization artifact label." >&2; exit 1 ;; esac
receipt="$PWD/artifacts/macos-notarization/$label-submission.json"
result="$PWD/artifacts/macos-notarization/$label-status.json"
mkdir -p "$PWD/artifacts/macos-notarization"
if [[ -f "$receipt" ]]; then
    echo "A notarization submission is already recorded here. Check its status before uploading again." >&2
    exit 1
fi
xcrun notarytool submit "$artifact" --key "${GHCP_SIGNING_TEMP:?}/notary.p8" \
    --key-id "${MACOS_NOTARY_KEY_ID:?}" --issuer "${MACOS_NOTARY_ISSUER:?}" \
    --output-format json > "$receipt"
submission_id="$(python3 - "$receipt" <<'PY'
import json, sys, uuid
with open(sys.argv[1]) as file:
    receipt = json.load(file)
print(uuid.UUID(receipt["id"]))
PY
)"
printf 'Notarization %s submission ID: %s\n' "$label" "$submission_id"
if ! xcrun notarytool wait "$submission_id" --key "$GHCP_SIGNING_TEMP/notary.p8" \
    --key-id "$MACOS_NOTARY_KEY_ID" --issuer "$MACOS_NOTARY_ISSUER" \
    --timeout 60m --output-format json > "$result"; then
    echo "Notarization did not complete successfully. The submission ID is retained in the macos-notarization artifact. A wait timeout does not cancel Apple's processing; check this submission before uploading again." >&2
    exit 1
fi
python3 - "$result" <<'PY'
import json, sys
with open(sys.argv[1]) as file:
    result = json.load(file)
if result.get("status") != "Accepted":
    raise SystemExit(f"Notarization is not accepted (status: {result.get('status', 'missing')}). Inspect the saved submission before retrying.")
print("Notarization accepted.")
PY
