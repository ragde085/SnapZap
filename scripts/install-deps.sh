#!/usr/bin/env bash
# Install SnapZap's optional sidecar — macOS / Linux.
#
#   scripts/install-deps.sh                 install into the repo (dev)
#   scripts/install-deps.sh --dest DIR      install beside a published binary
#   scripts/install-deps.sh --model-only    same thing; kept so older notes still work
#   scripts/install-deps.sh --force         re-download even if already present
#
# It is optional: SnapZap scans, finds duplicates, exports and deletes without it. The model
# unlocks NSFW scoring, and nothing else. (czkawka_cli used to be installed here too;
# similar-photo detection moved in-process — see docs/DEDUP-V2.md.)
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

# Prints the header block above verbatim, so --help can never drift from the file's own docs.
 usage() { sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-0}"; }

DEST=""; FORCE=0
while [ $# -gt 0 ]; do
  case "$1" in
    --dest)         DEST="${2:?--dest needs a directory}"; shift 2 ;;
    # Accepted and ignored: there is only one sidecar left, and failing on a flag that used to
    # work would break every note, script and shell history that still passes it.
    --model-only)   shift ;;
    --czkawka-only)
      echo "  ! --czkawka-only no longer does anything: similar-photo detection is built in." >&2
      echo "    See docs/DEDUP-V2.md." >&2
      exit 0 ;;
    --force)        FORCE=1; shift ;;
    -h|--help)      usage 0 ;;
    *)              echo "unknown option: $1" >&2; usage 1 ;;
  esac
done

# Where the app looks. With no --dest we install into the repo and let the build copy the model
# into the output directory (see SnapZap.App.csproj), so `dotnet run` picks it up with no
# environment variables and no manual copying.
if [ -n "$DEST" ]; then
  mkdir -p "$DEST"
  DEST="$(cd "$DEST" && pwd)"
  MODEL_DIR="$DEST/models"
else
  MODEL_DIR="$REPO_ROOT/models"
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

echo "SnapZap optional sidecar"
echo

echo "NSFW scoring model → $MODEL_DIR"
fetch "$MODEL_URL"  "$MODEL_DIR/nsfw.onnx"                 "$MODEL_SHA"  "nsfw.onnx (328 MB)"
fetch "$CONFIG_URL" "$MODEL_DIR/preprocessor_config.json"  "$CONFIG_SHA" "preprocessor_config.json"
echo

echo "Done."
if [ -z "$DEST" ]; then
  echo "The build copies it into the app's output directory, so:"
  echo
  echo "    dotnet run --project src/SnapZap.App"
  echo
  echo "will find it. Confirm under Setup in the app's left rail."
else
  echo "Installed beside the binary in $DEST. Start the app and check Setup in the left rail."
fi
