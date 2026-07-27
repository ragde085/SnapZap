#!/bin/bash
# Runs when the optional "explicit-content scoring model" component is selected. Delegates to
# scripts/install-deps.sh (bundled alongside this script by scripts/build-installer-mac.sh)
# rather than re-implementing the pinned URL / SHA-256 / atomic-move logic a second time — one
# fetcher, used by `dotnet run`, a manual install, and this installer alike.
#
# A failed or missing download must never fail the whole package install: this component is
# optional, and SnapZap is fully usable without it (see install-deps.sh's own header).
set -euo pipefail

DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=notify.sh
source "$DIR/notify.sh"

APP_DIR="/Applications/SnapZap.app/Contents/Resources/app"

# The distribution forces a system-wide install (see build-installer-mac.sh), which installs
# SnapZap-app.pkg before this one — but a few seconds of slack here is cheap insurance against
# any installer-version quirk in that ordering, versus failing outright on a single check.
for _ in $(seq 1 10); do
  [ -d "$APP_DIR" ] && break
  sleep 1
done

if [ ! -d "$APP_DIR" ]; then
  notify_user "SnapZap model" \
    "SnapZap.app was not found in /Applications, so the explicit-content model was not installed."
  exit 0
fi

# The installer's own progress bar has already finished by this point — it doesn't reflect a
# postinstall script's own work — so without this banner there is nothing on screen for however
# long the download takes, and "Installer shows no progress" reads identically to "Installer is
# stuck" to anyone watching it.
notify_banner "SnapZap" \
  "Downloading the explicit-content model (328 MB) — can take a few minutes. SnapZap itself is already installed and usable; this only adds one filter."

if "$DIR/install-deps.sh" --dest "$APP_DIR" >/tmp/snapzap-model-install.log 2>&1; then
  notify_banner "SnapZap" "Explicit-content model installed."
  exit 0
fi

notify_user "SnapZap model" \
  "Downloading the explicit-content model failed. SnapZap works fully without it — rerun scripts/install-deps.sh --dest \\\"$APP_DIR\\\" any time to retry."
exit 0
