# Roadmap & Next Steps

Living status doc for SnapZap. For architecture and rationale see
[../DESIGN.md](../DESIGN.md); for the Windows hardware checklist see
[../WINDOWS-VERIFY.md](../WINDOWS-VERIFY.md).

Last updated: 2026-07-24.

---

## Current status

**All 11 planned build steps are complete.** The app runs end-to-end on macOS, cross-compiles
to a self-contained `win-x64` single-file `.exe`, and has been driven through its full flow in
a browser: scan → dedup → NSFW → filter → select → export (copy/move/hardlink) → delete →
undo/restore.

- **Tests:** 28 pass, 2 gated on external assets (real model + labeled fixtures).
- **NSFW pipeline:** validated against HuggingFace's reference pipeline with the real Falconsai
  model — C# scores match within 0.002. Preprocessing is proven correct.
- **Safety invariants:** enforced and tested (verify-before-destroy, never-overwrite,
  recycle-not-delete, full undo).

### What's done

| Area | State |
|---|---|
| Scan · hash · thumbnails · two-tier cache | ✅ tested |
| Exact dedup (from hashes) | ✅ tested |
| Similar dedup (Czkawka sidecar) | ⚠️ built; JSON parser needs real-output validation |
| NSFW scoring (Falconsai ONNX, CPU) | ✅ validated vs reference |
| Blur (Laplacian) + EXIF | ✅ tested |
| SPA: grid, badges, preview, selection, filters | ✅ browser-tested |
| Export engine + UI + pre-flight + manifest | ✅ tested (incl. hardlinks) |
| Delete + undo/restore | ✅ tested (real Finder trash) |
| Windows platform services | ⚠️ implemented, cross-compiled, runtime-unverified |
| Self-contained `.exe` packaging | ✅ builds |

---

## Next steps

Ordered roughly by what unblocks a real Windows release first. Each item notes rough effort
and whether it needs Windows hardware.

### P0 — Required before a real Windows release

1. **Windows hardware verification** · _needs Windows_ · ~half day
   Walk [../WINDOWS-VERIFY.md](../WINDOWS-VERIFY.md): Recycle Bin, Shell restore, hardlinks,
   and the double-click → browser launch. These paths compile and cross-build but have never
   executed. This is the single gate between "builds" and "shippable".

2. **Czkawka similar-detection validation** · _needs czkawka_cli_ · ~2 hours
   The JSON parser in `CzkawkaFinder` is defensive but was written without real
   `czkawka_cli -C` output to test against. Install czkawka, run `image -C out.json` on a
   folder, confirm the parser maps groups correctly, and adjust if the schema differs.
   Exact dedup is unaffected — this only gates the "similar images" feature.

### P1 — High-value polish

3. **Full-resolution preview** · cross-platform · ~2 hours
   The preview modal currently shows the thumbnail. Add a `GET /api/full/{id}` endpoint that
   streams the original file (guarded to cataloged paths), and point the modal at it.

4. **Scan/score cancellation in the UI** · cross-platform · ~2 hours
   The backend already honors `CancellationToken`; wire a Cancel button that aborts the SSE
   request so long scans can be stopped.

5. **Grid virtualization** · cross-platform · ~half day
   `loading="lazy"` handles thousands of thumbnails, but tens of thousands will strain the
   DOM. Add windowed rendering (render only visible rows) for very large libraries.

6. **DirectML GPU acceleration** · _needs Windows_ · ~half day
   Swap `Microsoft.ML.OnnxRuntime` → `Microsoft.ML.OnnxRuntime.DirectML` in the Windows
   publish and wire `IInferenceProvider` to select the DirectML execution provider. CPU
   inference works today; this is a speed upgrade for large libraries. Measure before/after.

### P2 — Nice to have

7. **Photino native window** · _needs Windows_ · ~half day
   Replace the browser-tab launch with an embedded WebView2 window via `IAppHost` for a true
   desktop feel. Current browser launch is functional; this is UX polish.

8. **Installer / distribution** · _needs Windows_ · ~half day
   Package the `.exe` + `wwwroot` + optional sidecars into an installer (e.g. Inno Setup or
   MSIX), or ship a zip with a first-run helper that fetches the NSFW model.

9. **NSFW judgment tuning** · user-driven
   The pipeline is validated; whether the model's *decisions* meet your bar is a content
   question only you can judge. Assemble a labeled `nsfw/` + `sfw/` fixture set and run
   `dotnet test --filter Category=NsfwModelValidation` to pick a default threshold.

### Backlog (from DESIGN §12, deliberately deferred)

- Plex path-scoped refresh (unnecessary — Plex watches the export destination).
- NudeNet granular body-part detection (single score covers the use case).
- Video support, RAW formats, face recognition, screenshot detection.

---

## Known limitations to keep in mind

- **Windows-only paths are unverified on hardware** — see P0.1.
- **Similar-image JSON parser is unproven** against real Czkawka output — see P0.2.
- **NSFW is CPU inference** — correct but not GPU-accelerated yet (P1.6).
- **Launch is a browser tab**, not a native window yet (P2.7).
- **Preview shows the thumbnail**, not full-res yet (P1.3).

## How to pick up any item

1. Read the relevant section of [../DESIGN.md](../DESIGN.md).
2. The seam is almost always already there (an interface, an endpoint, or a `// TODO`).
3. Add or extend a test in `tests/SnapZap.Tests` first where practical.
4. For UI work, drive it in a browser against the `/tmp/pc_demo` synthetic library
   (`scripts`-style generator in scratchpad, or any real folder).
