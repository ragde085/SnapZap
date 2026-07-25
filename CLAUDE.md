# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

SnapZap is a Windows desktop app (.NET 10, C#) that finds duplicates, NSFW images, and blurry photos in a folder, then exports the clean set to a destination (e.g., for Plex to watch). The app is:
- Developed on macOS, shipped as a self-contained `win-x64` executable
- ~90% cross-platform; four Windows-only paths behind `IPlatformServices` interfaces (Recycle Bin, hardlinks, DirectML GPU, native window host)
- No cloud, no subscriptions, no paid dependencies
- Safety-critical: nothing is hard-deleted until hash-verified; source folder untouched unless explicitly requested

See [DESIGN.md](DESIGN.md) for full architecture and rationale, and [WINDOWS-VERIFY.md](WINDOWS-VERIFY.md) for the Windows validation checklist.

---

## Common Commands

### Run (development)
```bash
# Start the dev server (opens browser automatically)
dotnet run --project src/SnapZap.App

# Headless (for CI/automated testing)
PC_NO_BROWSER=1 dotnet run --project src/SnapZap.App
```

**Environment variables:**
- `PC_NSFW_MODEL=/path/to/nsfw.onnx` — NSFW model location (default: `models/nsfw.onnx` beside binary)
- `PC_NO_BROWSER=1` — skip auto-opening browser
- `PC_TEST_ONNX=1` — enable ONNX plumbing test (requires `PC_NSFW_MODEL` to exist)
- `PC_NSFW_FIXTURES=/path/to/fixtures` — labeled images for model validation (`fixtures/nsfw/*.jpg` and `fixtures/sfw/*.jpg`)

### Test
```bash
# Full suite
dotnet test

# Single test class (e.g., scanner tests)
dotnet test --filter "ClassName=ScannerTests"

# With filter expression (e.g., skip NSFW validation)
dotnet test --filter "Category!=NsfwModelValidation"

# Verbose output
dotnet test --logger "console;verbosity=detailed"

# Watch mode (requires dotnet-watch tool)
dotnet watch test
```

Two tests are conditional:
1. **ONNX plumbing** — skips unless `PC_TEST_ONNX` is set and model exists
2. **NSFW model validation** — skips unless `PC_NSFW_MODEL` and `PC_NSFW_FIXTURES` provided

### Build (Windows executable)
```bash
# From macOS or Windows, publish as self-contained win-x64 executable
dotnet publish src/SnapZap.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o artifacts/win-x64
```

Output: `artifacts/win-x64/SnapZap.App.exe` (~130 MB, includes .NET runtime, requires no installation)

### Sidecar assets (optional, ship beside the binary)
```bash
# Get NSFW ONNX model (~350 MB, Apache-2.0, not committed)
scripts/get-nsfw-model.sh          # writes models/nsfw.onnx + preprocessor_config.json

# Validate model's scores against labeled fixtures
PC_NSFW_MODEL="$PWD/models/nsfw.onnx" \
PC_NSFW_FIXTURES=/path/to/fixtures \
dotnet test --filter "Category=NsfwModelValidation"
```

Windows sidecar layout:
```
SnapZap.App.exe
wwwroot/                    (published automatically)
models/nsfw.onnx            (optional — enables NSFW scoring)
models/preprocessor_config.json
czkawka_cli.exe             (optional — enables similar-image detection)
```

Graceful degradation: missing NSFW model disables NSFW scoring; missing `czkawka_cli.exe` disables near-duplicate detection. Only exact duplicates (from our SHA-256 content hash) always work.

---

## Project Structure

| Path | Purpose |
|---|---|
| `src/SnapZap.Core/` | Portable core logic: scanning, hashing, dedup, NSFW, blur/EXIF, export, delete, platform interfaces |
| `src/SnapZap.App/` | ASP.NET Core minimal-API host + SPA (`wwwroot/`) |
| `tests/SnapZap.Tests/` | xUnit test suite + fixtures |
| `DESIGN.md` | Architecture, decisions, data model, pipeline, safety invariants |
| `WINDOWS-VERIFY.md` | Checklist for four Windows-only code paths |
| `scripts/get-nsfw-model.sh` | One-time export of NSFW ONNX model |
| `artifacts/win-x64/` | Publish output (built, not committed) |
| `models/` | Sidecar assets — NSFW ONNX model + config (built, not committed) |

### Key subdirectories in `Core/`

- **Scanning/** — file enumeration, SHA-256 hashing (`Hasher.cs`), cache-probe optimization
- **Dedup/** — `ExactDuplicateFinder` (SQL on our hashes), `CzkawkaFinder` (JSON parse of subprocess output)
- **Nsfw/** — ONNX Runtime inference (`OnnxNsfwClassifier`), image preprocessing, score thresholds
- **Analysis/** — EXIF extraction (`ExifExtractor`), blur detection via Laplacian variance (`BlurDetector`)
- **Export/** — `ExportEngine` (copy/move/hardlink modes), manifest writer, hash verification, collision-safe naming
- **Delete/** — recycle-bin operations, undo/restore, platform-specific `ITrashService`
- **Data/** — SQLite schema, `ImageRepository`, `DupeRepository`, cache layer
- **Platform/** — `IPlatformServices` interface + macOS impl (`MacOsServices`), Windows impl stub (`WindowsServices`)
- **Imaging/** — `SkiaImageService` for decode, EXIF geometry, thumbnail generation (never use ImageSharp — Six Labors split license trap)

---

## Architecture Highlights

### Four-interface platform abstraction

All Windows-specific logic is behind `IPlatformServices` (in `Platform/IPlatformServices.cs`):

| Method | Purpose | Status |
|---|---|---|
| `ITrashService` | Recycle bin operations (delete → recycle → restore) | macOS impl working; Windows impl pending (#10) |
| `ILinkService` | Hardlink creation + stat (for export hardlink mode) | macOS impl (libc `link()`) working; Windows impl pending (#11) |
| `IInferenceProvider` | NSFW ONNX inference (CPU on macOS, optionally DirectML on Windows) | CPU impl working; DirectML pending (#11) |
| `IAppHost` | Window host + browser launch (Photino/WebView2 on Windows) | Browser-launch impl working; Photino window pending (#9) |

Development on macOS with Windows-impl stubs means:
- **Cross-platform code paths execute fully** (scan, dedup, export, delete all work on Mac)
- **Windows-only paths (4 above) must be verified on Windows hardware** (see `WINDOWS-VERIFY.md`)

### Key invariants (safety-critical)

1. **No destructive step precedes hash-verification.**
   - Export: file is copied/moved, then immediately hash-verified against cached hash. Only after verification is the source deleted (move mode) or rejected item recycled (reject_action).
   - Delete: file recycled (not hard-deleted) so undo/restore is always possible.

2. **Source folder is never touched unless explicitly requested.**
   - Scan and dedup are read-only. Export's move mode and delete's recycle-from-source are opt-in per checkbox.

3. **Two-tier caching avoids re-hashing on every scan.**
   - Cheap probe: `(path, file_size, mtime)` — if unchanged, reuse entire cached row (hash + all signals).
   - This makes re-scanning a 40k-photo library near-instant.

### Pipeline sketch

```
Folder pick
  → Enumerate files, probe cache
    → cache hit: reuse row
    → cache miss: content hash (SHA-256) + parallel analysis
                  ├ NSFW score (ONNX)
                  ├ Blur score (Laplacian variance)
                  ├ EXIF (date, camera)
                  └ Thumbnail (SkiaSharp)
  → czkawka_cli subprocess for similar-image groups
  → SQLite (faceted query)
  → SPA grid (select, filter)
  → Export (copy/move/hardlink → hash-verify → manifest)
  → Delete (optional, separate mode, recycle + undo panel)
```

### Dependency notes

**Never use ImageSharp.** Its Six Labors Split License has a revenue threshold ($1M) above which you must pay. SkiaSharp covers every need (decode, thumbnail, geometry) and is unambiguously MIT.

All other deps are MIT or Apache-2.0. No paid, no subscription-gated.

### ONNX & model assumptions

- **ONNX Runtime inference** runs in-process (no server, no cloud).
- **Model is the Falconsai ViT** trained on ~10k labeled images (Apache-2.0).
- **Output is a single float `[0, 1]`** (probability of NSFW). The SPA provides a threshold slider.
- **Preprocessing** (resize, normalize) is in `NsfwPreprocess.cs`; the config is in `preprocessor_config.json` (downloaded alongside the model).
- **Model validation** (test category `NsfwModelValidation`) requires labeled fixtures to confirm the model's scores are sensible. Tests skip unless you provide them.

### Czkawka integration

- **Used only for similar-image detection**, not exact duplicates.
- **Exact duplicates** come from our own SHA-256 hashes in SQLite (faster, works offline, no sidecar required).
- **Parser** (`CzkawkaFinder.cs`) recursively finds arrays of `{path,...}` objects in the JSON output. Schema is not publicly documented; the parser is defensive and unit-tested, but **must be validated against real `czkawka_cli -C` output** before trusting similar-detection results in production.

### Gotchas

1. **`dotnet add package ... -v q` misparses `q` as a version.** Use `dotnet add package ... --prerelease` or omit `-v` entirely.

2. **Hashing is SHA-256, not BLAKE3**, for portability (hardware-accelerated in .NET, zero native build dependencies). Swappable in `Scanning/Hasher.cs` if needed.

3. **Exact duplicates from our hashes, not Czkawka.** Czkawka is only for similar detection. This means exact dedup works even without the `czkawka_cli.exe` sidecar.

4. **Windows-specific code is stubbed on macOS.** The four `IPlatformServices` methods for Windows (Recycle Bin, hardlinks, DirectML, Photino window) are macOS-compatible stubs or no-ops. Full end-to-end testing happens on macOS, but **those four paths must be verified on Windows hardware** before shipping.

5. **SkiaSharp's native assets** are in the Core csproj (macOS + Win32 .nupkg packages). The publish step bundles them into the self-contained .exe.

6. **App data (caches, database, temp thumbnails)** lives in `.photoclean/` (OS-appropriate location, e.g., `%APPDATA%\photo-cleaner` on Windows). Listed in `.gitignore` so never committed.

---

## Development Notes

- **Test naming:** Tests are organized by feature (ScannerTests, DedupTests, NsfwTests, ExportTests, DeleteTests, AnalysisTests, PlatformTests).
- **Nullable reference types** are enabled; use `#nullable disable` only where cross-cutting OSS interop forces it.
- **Implicit usings** are enabled; global `using` statements in each project.
- **JSON serialization** uses camelCase (property names in SPA are camelCase, DTOs auto-serialize via `JsonCamel.Options`).
- **Platform-specific tests** use `[SkippableFact]` to skip gracefully on the wrong OS (e.g., Recycle Bin tests skip on macOS).

---

## Windows Release Checklist

See [WINDOWS-VERIFY.md](WINDOWS-VERIFY.md) for the four Windows-only paths that must be validated on Windows hardware:
1. Recycle Bin (delete → restore)
2. Shell restore (from Recycle Bin context menu)
3. Hardlinks (export hardlink mode)
4. WebView2 (native window host, optional; currently browser-launch only)

All four are behind `IPlatformServices` so the impact is isolated. Prototype implementations exist (macOS versions or stubs); production use requires Windows validation.
