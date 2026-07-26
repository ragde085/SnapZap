#!/usr/bin/env bash
# Install SnapZap's two optional sidecars — macOS / Linux.
#
#   scripts/install-deps.sh                 install both into the repo (dev)
#   scripts/install-deps.sh --dest DIR      install beside a published binary
#   scripts/install-deps.sh --model-only    NSFW model only
#   scripts/install-deps.sh --czkawka-only  similar-photo detection only
#   scripts/install-deps.sh --force         re-download even if already present
#
# Both are optional: SnapZap scans, finds exact duplicates, exports and deletes without
# either one. The model unlocks NSFW scoring; czkawka_cli unlocks similar-photo detection.
#
# Every download is pinned to an exact revision and SHA-256 verified before it is put in
# place. Nothing is installed from a checksum that does not match.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# --- pinned versions -----------------------------------------------------------------
# The model: Falconsai/nsfw_image_detection (Apache-2.0), ONNX conversion by onnx-community,
# pinned to an immutable revision so the checksums below stay meaningful.
MODEL_REPO="onnx-community/nsfw_image_detection-ONNX"
MODEL_REV="1ceb3c7fe1e9f3f2507e6df577437f23a9149fd5"
MODEL_URL="https://huggingface.co/$MODEL_REPO/resolve/$MODEL_REV/onnx/model.onnx"
MODEL_SHA="a4316a4fb750169ac4fcabaabee1fcbd982b0ee8c0cc63fe3e944954bb9a7d9c"
CONFIG_URL="https://huggingface.co/$MODEL_REPO/resolve/$MODEL_REV/preprocessor_config.json"
CONFIG_SHA="ae9bb157b9629887cc74913a4e7c12c9308f374f0930e8072320e8f2e1583c5e"

# czkawka: pinned to 12.0.0, the release CzkawkaFinder's JSON parser is tested against.
CZKAWKA_VER="12.0.0"

# Prints the header block above verbatim, so --help can never drift from the file's own docs.
usage() { sed -n '2,14p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }

DEST=""; WANT_MODEL=1; WANT_CZKAWKA=1; FORCE=0
while [ $# -gt 0 ]; do
  case "$1" in
    --dest)         DEST="${2:?--dest needs a directory}"; shift 2 ;;
    --model-only)   WANT_CZKAWKA=0; shift ;;
    --czkawka-only) WANT_MODEL=0; shift ;;
    --force)        FORCE=1; shift ;;
    -h|--help)      usage 0 ;;
    *)              echo "unknown option: $1" >&2; usage 1 ;;
  esac
done

# Where the app looks. With no --dest we install into the repo and let the build copy both
# into the output directory (see SnapZap.App.csproj), so `dotnet run` picks them up with no
# environment variables and no manual copying.
if [ -n "$DEST" ]; then
  mkdir -p "$DEST"
  DEST="$(cd "$DEST" && pwd)"
  MODEL_DIR="$DEST/models"
  CZKAWKA_DIR="$DEST"
else
  MODEL_DIR="$REPO_ROOT/models"
  CZKAWKA_DIR="$REPO_ROOT/tools"
fi

sha256_of() {
  if command -v shasum >/dev/null 2>&1; then shasum -a 256 "$1" | cut -d' ' -f1
  else sha256sum "$1" | cut -d' ' -f1; fi
}

# Download to a temp file, verify, then move into place — so an interrupted or corrupted
# transfer can never leave a half-written model where the app will load it.
fetch() {
  local url="$1" out="$2" want="$3" label="$4"

  if [ "$FORCE" -eq 0 ] && [ -f "$out" ] && [ "$(sha256_of "$out")" = "$want" ]; then
    echo "  ✓ $label — already installed"
    return
  fi

  echo "  ↓ $label"
  mkdir -p "$(dirname "$out")"
  local tmp="$out.partial"
  curl -fL --retry 3 --retry-delay 2 -C - --progress-bar -o "$tmp" "$url"

  local got; got="$(sha256_of "$tmp")"
  if [ "$got" != "$want" ]; then
    rm -f "$tmp"
    echo "  ✗ $label — checksum mismatch, not installed" >&2
    echo "      expected $want" >&2
    echo "      got      $got" >&2
    exit 1
  fi
  mv "$tmp" "$out"
  echo "  ✓ $label"
}

echo "SnapZap optional sidecars"
echo

if [ "$WANT_MODEL" -eq 1 ]; then
  echo "NSFW scoring model → $MODEL_DIR"
  fetch "$MODEL_URL"  "$MODEL_DIR/nsfw.onnx"                 "$MODEL_SHA"  "nsfw.onnx (328 MB)"
  fetch "$CONFIG_URL" "$MODEL_DIR/preprocessor_config.json"  "$CONFIG_SHA" "preprocessor_config.json"
  echo
fi

if [ "$WANT_CZKAWKA" -eq 1 ]; then
  echo "Similar-photo detection → $CZKAWKA_DIR"
  case "$(uname -s)/$(uname -m)" in
    Darwin/arm64)      ASSET=mac_czkawka_cli_arm64;      SHA=9a08888d329fe39d5b00a15bf0bbbfdc80c5f480465edc63a94a13a3b4e1f312 ;;
    Linux/x86_64)      ASSET=linux_czkawka_cli_x86_64;   SHA=ad21a5428aee09fad88fb6d35fb1c656b9e0b8cdafee2de107618ddb5a9997ff ;;
    Linux/aarch64|Linux/arm64) ASSET=linux_czkawka_cli_arm64; SHA=2d7a66cf626d64ae578e2ef502df5ea9e82aeab3d890853a2e15118c430d8a37 ;;
    Darwin/x86_64)
      # czkawka publishes no Intel-Mac binary. Everything else still works without it.
      echo "  ! No prebuilt czkawka_cli for Intel Macs." >&2
      echo "    Build it with:  cargo install czkawka_cli --version $CZKAWKA_VER" >&2
      echo "    then copy the binary to $CZKAWKA_DIR/czkawka_cli" >&2
      ASSET="" ;;
    *)
      echo "  ! No prebuilt czkawka_cli for $(uname -s)/$(uname -m)." >&2
      echo "    See https://github.com/qarmin/czkawka/releases" >&2
      ASSET="" ;;
  esac

  if [ -n "$ASSET" ]; then
    fetch "https://github.com/qarmin/czkawka/releases/download/$CZKAWKA_VER/$ASSET" \
          "$CZKAWKA_DIR/czkawka_cli" "$SHA" "czkawka_cli $CZKAWKA_VER"
    chmod +x "$CZKAWKA_DIR/czkawka_cli"
    # Gatekeeper quarantines anything downloaded; without this macOS kills it on first run.
    if [ "$(uname -s)" = "Darwin" ]; then
      xattr -d com.apple.quarantine "$CZKAWKA_DIR/czkawka_cli" 2>/dev/null || true
    fi
  fi
  echo
fi

echo "Done."
if [ -z "$DEST" ]; then
  echo "The build copies both into the app's output directory, so:"
  echo
  echo "    dotnet run --project src/SnapZap.App"
  echo
  echo "will find them. Confirm under Setup in the app's left rail."
else
  echo "Installed beside the binary in $DEST. Start the app and check Setup in the left rail."
fi
