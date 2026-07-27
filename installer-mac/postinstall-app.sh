#!/bin/bash
# Runs after SnapZap.app lands in /Applications. Purely informative — the macOS analogue of the
# Windows installer's WebView2 check, but weaker by necessity: there is no bundled runtime
# installer to fall back to here (see scripts/build-installer-mac.sh), so this can only tell the
# user what's missing, not fix it for them.
set -euo pipefail

DIR="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=find-dotnet.sh
source "$DIR/find-dotnet.sh"
# shellcheck source=notify.sh
source "$DIR/notify.sh"

if ! find_dotnet >/dev/null; then
  notify_user "SnapZap installed" \
    "SnapZap also needs the .NET 10 runtime, which was not found on this Mac. Install it from https://dotnet.microsoft.com/download/dotnet/10.0, then launch SnapZap."
fi

exit 0
