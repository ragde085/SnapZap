#!/usr/bin/env bash
# Export the Falconsai/nsfw_image_detection ViT to ONNX for SnapZap.
#
# The model is NOT committed to the repo (~350 MB) and cannot be fetched in CI. Run this once
# on a machine with Python to produce models/nsfw.onnx + preprocessor_config.json, which the
# app loads as a sidecar. Apache-2.0 licensed, fully offline after download.
#
# Exports directly with torch.onnx (no dependency on optimum's shifting CLI).
#
# Usage:  scripts/get-nsfw-model.sh [OUT_DIR]
#   OUT_DIR defaults to <repo>/models regardless of where you run this from.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
OUT_DIR="${1:-$REPO_ROOT/models}"
mkdir -p "$OUT_DIR"

VENV=/tmp/pc_onnx_export
echo "Setting up a throwaway venv for the export…"
rm -rf "$VENV"
python3 -m venv "$VENV"
# shellcheck disable=SC1091
source "$VENV/bin/activate"
pip install --quiet --upgrade pip
pip install --quiet torch transformers onnx onnxruntime pillow

echo "Exporting Falconsai/nsfw_image_detection → ONNX…"
OUT_DIR="$OUT_DIR" python3 - <<'PY'
import os, torch
from transformers import AutoModelForImageClassification, AutoImageProcessor

out = os.environ["OUT_DIR"]
model_id = "Falconsai/nsfw_image_detection"

model = AutoModelForImageClassification.from_pretrained(model_id).eval()
proc  = AutoImageProcessor.from_pretrained(model_id)

# Wrap so the ONNX graph has a single clean 'logits' output (not a dataclass).
class Wrap(torch.nn.Module):
    def __init__(self, m): super().__init__(); self.m = m
    def forward(self, pixel_values): return self.m(pixel_values=pixel_values).logits

dummy = torch.randn(1, 3, 224, 224)
onnx_path = os.path.join(out, "nsfw.onnx")
torch.onnx.export(
    Wrap(model), dummy, onnx_path,
    input_names=["pixel_values"], output_names=["logits"],
    dynamic_axes={"pixel_values": {0: "batch"}, "logits": {0: "batch"}},
    opset_version=17,  # ViT uses scaled_dot_product_attention (needs opset >= 14)
    verbose=False,
)

# The app reads these real constants; never assume defaults.
proc.save_pretrained(out)

# Report the label order so you can confirm nsfw is index 1 (the app's default).
print("id2label:", model.config.id2label)
print("wrote:", onnx_path)
PY

deactivate

echo
echo "Done:"
echo "  $OUT_DIR/nsfw.onnx"
echo "  $OUT_DIR/preprocessor_config.json   (real mean/std/size — the app reads these)"
echo
echo "If id2label above is {0: 'normal', 1: 'nsfw'}, the app's default label index (1) is correct."
echo
echo "Validate scores against your own labeled images:"
echo "  PC_NSFW_MODEL=\"$OUT_DIR/nsfw.onnx\" \\"
echo "  PC_NSFW_FIXTURES=/path/to/fixtures \\  # fixtures/nsfw/*.jpg and fixtures/sfw/*.jpg"
echo "  dotnet test --filter Category=NsfwModelValidation"
