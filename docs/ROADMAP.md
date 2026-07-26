# Roadmap & Next Steps

Living status doc for SnapZap. For architecture and rationale see
[DESIGN.md](DESIGN.md); for the Windows hardware checklist see
[WINDOWS-VERIFY.md](WINDOWS-VERIFY.md).

Last updated: 2026-07-26 (dedup v2: czkawka sidecar removed, three kinds, in-process matching).

---

## Current status

**All 11 planned build steps are complete, and the UI has been migrated to Blazor Server.**
The app runs end-to-end on macOS, cross-compiles to a self-contained `win-x64` single-file
`.exe`, and has been driven through its full flow in a browser: scan → dedup → NSFW → filter →
select → export (copy/move/hardlink) → delete → undo/restore.

- **Tests:** 29 pass, 2 gated on external assets (real model + labeled fixtures) — 31 total.
- **NSFW pipeline:** validated against HuggingFace's reference pipeline with the real Falconsai
  model — C# scores match within 0.002. Preprocessing is proven correct.
- **Safety invariants:** enforced and tested (verify-before-destroy, never-overwrite,
  recycle-not-delete, full undo).

### What's done

| Area | State |
|---|---|
| Scan · hash · thumbnails · two-tier cache | ✅ tested |
| Exact dedup (from hashes) | ✅ tested |
| Variant + burst dedup (in-process, 272-bit hash) | ✅ tested — see [DEDUP-V2.md](DEDUP-V2.md) |
| NSFW scoring (Falconsai ONNX, CPU) | ✅ validated vs reference |
| Blur (Laplacian) + EXIF | ✅ tested |
| Blazor UI: grid, badges, preview, selection, filters | ✅ browser-tested |
| Grid windowing (custom, not `<Virtualize>`) | ✅ verified at 38,668 photos |
| Full-resolution preview (`/api/full/{id}`) | ✅ done |
| Keyboard triage + shortcuts | ✅ browser-tested |
| Optional-sidecar validation + Setup panel | ✅ browser-tested |
| Export engine + UI + pre-flight + manifest | ✅ tested (incl. hardlinks) |
| Delete + undo (toast + history panel) | ✅ tested (real Finder trash) |
| Duplicate group review + keeper override | ✅ verified against DB |
| Cancel long operations (scan/NSFW/export) | ✅ verified resume-from-cache |
| Grid sort + folder tree + scan-issue reporting | ✅ browser-tested |
| Windows platform services | ⚠️ implemented, cross-compiled, runtime-unverified |
| Self-contained `.exe` packaging | ✅ builds |
| macOS `.app` bundle | ✅ runs locally (framework-dependent, script launcher) |

---

## Next steps

Ordered roughly by what unblocks a real Windows release first. Each item notes rough effort
and whether it needs Windows hardware.

### P0 — Required before a real Windows release

1. **Windows hardware verification** · _needs Windows_ · ~half day
   Walk [WINDOWS-VERIFY.md](WINDOWS-VERIFY.md): Recycle Bin, Shell restore, hardlinks,
   and the double-click → browser launch. These paths compile and cross-build but have never
   executed. This is the single gate between "builds" and "shippable".

~~2. **Czkawka similar-detection validation**~~ — ✅ **done 2026-07-25** against czkawka 12.0.0.
   The parser's assumed schema was correct and needed no change. Validation found three real
   integration bugs, all fixed: czkawka canonicalises paths while the catalog stores what the
   user typed (so scanning `/tmp/x` on macOS matched nothing and reported "0 similar groups"
   silently); an explicitly configured binary path fell through to `PATH` instead of being
   authoritative; and the keeper tie-break could retain a compressed copy over its original.
   Real output is now locked in by `DedupTests.Parses_real_czkawka_12_output`.

   Left open deliberately: `--max-difference` is 10, but czkawka 12 defaults `--hash-size`
   to 16, for which it recommends up to 20 — so we under-detect. Conservative on purpose for
   a tool that deletes things; revisit if similar-detection feels too quiet.

### UI direction — done

The UI has been **ported to Blazor Server** and restyled ("Darkroom"). Plan and outcome in
[BLAZOR-MIGRATION.md](BLAZOR-MIGRATION.md). UI-layer only — Core, engines and tests untouched.
This absorbed the former P1 items for full-resolution preview and grid virtualization, both
now done. One deviation worth knowing: `<Virtualize>` could not be used (it reports a
zero-capacity viewport in this layout and renders nothing), so the grid windows rows itself
from geometry measured in `interop.js`.

### P1 — High-value polish

~~3. **Scan/score cancellation in the UI**~~ — ✅ **done 2026-07-25.** A `CancellationTokenSource`
   in `AppState.RunAsync` drives a Stop button in the progress bar and in the export dialog.
   Work already committed is kept: stopping a 500-photo scan at 481 kept 490, and re-running
   reported "16 new, 484 cached".

4. **DirectML GPU acceleration** · _needs Windows_ · ~half day
   Swap `Microsoft.ML.OnnxRuntime` → `Microsoft.ML.OnnxRuntime.DirectML` in the Windows
   publish and wire `IInferenceProvider` to select the DirectML execution provider. CPU
   inference works today; this is a speed upgrade for large libraries. Measure before/after.

### P2 — Nice to have

5. **Photino native window** · _needs Windows_ · ~half day
   Replace the browser-tab launch with an embedded WebView2 window via `IAppHost` for a true
   desktop feel. Current browser launch is functional; this is UX polish.

6. **Installer / distribution** · _needs Windows_ · ~half day
   Package the `.exe` + `wwwroot` + optional sidecars into an installer (e.g. Inno Setup or
   MSIX), or ship a zip with a first-run helper that fetches the NSFW model. A macOS `.app`
   bundle already exists for local use; making it distributable would additionally require
   Developer ID signing and notarization (see the macOS notes in the README).

7. **NSFW judgment tuning** · user-driven
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
- **NSFW is CPU inference** — correct but not GPU-accelerated yet (P1.4).
- **Launch is a browser tab**, not a native window yet (P2.5).
- **The macOS build is for local use only** — framework-dependent (needs the .NET 10 runtime)
  and unnotarized, so it can't be handed to another machine as-is. It also can't be published
  self-contained or single-file: endpoint security SIGKILLs the apphost, so the `.app` bundle
  runs `dotnet <dll>` from a script launcher.

## How to pick up any item

1. Read the relevant section of [DESIGN.md](DESIGN.md).
2. The seam is almost always already there (an interface, an endpoint, or a `// TODO`).
3. Add or extend a test in `tests/SnapZap.Tests` first where practical.
4. For UI work, drive it in a browser against a synthetic library. ImageMagick makes one
   quickly, and distinct colours make windowing and selection easy to eyeball:

   ```bash
   mkdir -p /tmp/pc_demo && cd /tmp/pc_demo
   for i in $(seq 1 300); do magick -size 400x300 "xc:hsl($(( (i*13) % 360 )),55%,50%)" "p_$i.jpg"; done
   ```

   Note the UI reads its own state from `AppState`, so anything you add must subscribe to
   `AppState.Changed` to re-render — see the UI notes in `CLAUDE.md`.
