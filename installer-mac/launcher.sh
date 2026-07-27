#!/bin/bash
# SnapZap's macOS launcher (becomes Contents/MacOS/SnapZap in the .app bundle).
#
# It does not exec the app directly. The native apphost that `dotnet publish` produces is
# ad-hoc signed and unnotarized, and gets SIGKILLed (exit 137, no output, no crash report) by
# endpoint security the instant it launches — verified for both self-contained and
# framework-dependent publishes on this machine, see CLAUDE.md's macOS build section. Handing
# execution to the machine's own signed, notarized `dotnet` instead sidesteps that entirely.
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RESOURCES="$DIR/../Resources"
APP_DLL="$RESOURCES/app/SnapZap.App.dll"

# shellcheck source=find-dotnet.sh
source "$RESOURCES/find-dotnet.sh"

DOTNET="$(find_dotnet || true)"
if [ -z "$DOTNET" ]; then
  osascript -e 'display alert "SnapZap needs the .NET runtime" message "Install the .NET 10 runtime from https://dotnet.microsoft.com/download/dotnet/10.0, then reopen SnapZap." as critical' || true
  exit 1
fi

exec "$DOTNET" "$APP_DLL"
