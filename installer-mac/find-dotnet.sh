# Resolves an absolute path to `dotnet`. Sourced (not executed) by both SnapZap.app's launcher
# and the installer's postinstall check, so the two can never disagree about what counts as
# "installed".
#
# Why absolute paths at all: GUI-launched processes on macOS — a double-clicked .app, or an
# installer's postinstall script run by installd — don't inherit a login shell's PATH, so
# `command -v dotnet` alone finds nothing even when the runtime is present. Checked first because
# it's where Microsoft's own installer puts it regardless of Intel/Apple Silicon; the rest cover
# a user-local `dotnet-install.sh --home` and Homebrew's two current cask layouts.
find_dotnet() {
  local candidates=(
    "/usr/local/share/dotnet/dotnet"
    "$HOME/.dotnet/dotnet"
    "/opt/homebrew/share/dotnet/dotnet"
    "/opt/homebrew/opt/dotnet/libexec/dotnet"
  )
  local c
  for c in "${candidates[@]}"; do
    if [ -x "$c" ]; then
      echo "$c"
      return 0
    fi
  done
  if command -v dotnet >/dev/null 2>&1; then
    command -v dotnet
    return 0
  fi
  return 1
}
