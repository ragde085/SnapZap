# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

SnapZap is a Windows desktop app (.NET 10, C#) that finds duplicates, NSFW images, and blurry photos in a folder, then exports the clean set to a destination (e.g., for Plex to watch). The app is:
- Developed on macOS, shipped as a self-contained `win-x64` executable
- ~90% cross-platform; four Windows-only paths behind `IPlatformServices` interfaces (Recycle Bin, hardlinks, DirectML GPU, native window host)
- No cloud, no subscriptions, no paid dependencies
- Safety-critical: nothing is hard-deleted until hash-verified; source folder untouched unless explicitly requested

See [DESIGN.md](docs/DESIGN.md) for full architecture and rationale, [WINDOWS-VERIFY.md](docs/WINDOWS-VERIFY.md) for the Windows validation checklist, and [UI-FEATURES.md](docs/UI-FEATURES.md) for a quick per-feature reference to recent UI work (busy/progress, dark-mode buttons, the directory picker, folder rescan, preview zoom, thumbnail size) — read it before touching any of those instead of re-deriving the "why" from the diff.

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

### Build (Windows)
```bash
# From macOS or Windows. Publishes a folder, not a file — see below.
dotnet publish src/SnapZap.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o artifacts/win-x64

# Windows only: the same publish, then packaged into an installer (needs Inno Setup 6).
scripts\build-installer.bat
```

Output: `artifacts/win-x64/` — `SnapZap.App.exe` + `wwwroot/` + `appsettings.json`, ~157 MB, no .NET
install needed. `scripts\build-installer.bat` turns that into
`artifacts/installer/SnapZap-<version>-setup.exe` (~47 MB).

**The executable does not work on its own.** `PublishSingleFile` bundles the runtime and the app
assemblies; it cannot bundle `wwwroot`, which holds `app.css`, `interop.js` and Blazor's
`_framework/blazor.web.js`. Separated from it, the app still starts and still serves 200 for
every page — it just renders unstyled, prerendered markup that ignores every click. That is
what shipping a bare `.exe` produced, and it is why distribution is an installer.

A `VerifyPublishOutput` target asserts those files exist after every publish. Do not remove it.

ReadyToRun is enabled automatically for this RID (scoped by `RuntimeIdentifier` in
`SnapZap.App.csproj`, no extra flag needed) — pre-JITted native startup code, trading a larger
binary for faster cold start. Published-size and cold-start deltas need measuring on the actual
Windows target; not yet done (tracked in `docs/PERFORMANCE.md`).

### Build for macOS (local use on the dev machine)

```bash
# RID-specific, framework-dependent (~57 MB). NOT self-contained, NOT single-file — see below.
dotnet publish src/SnapZap.App -c Release -r osx-arm64 --self-contained false -o artifacts/mac

# Wraps that publish in a double-clickable SnapZap.app and packages it as a .pkg installer.
scripts/build-installer-mac.sh
```

⚠️ **Do not use `--self-contained` / `PublishSingleFile` on macOS.** The resulting apphost
Mach-O binary is SIGKILLed on launch (exit 137, no output, no crash report) by endpoint
security on managed Macs — it is ad-hoc signed and unnotarized. Verified: self-contained and
framework-dependent apphosts both die; `dotnet SnapZap.App.dll` runs fine. So the macOS
`.app` bundle uses a **shell-script launcher** (`installer-mac/launcher.sh`) that invokes
`dotnet <dll>`, resolving the runtime by absolute path via `installer-mac/find-dotnet.sh`
(Finder gives GUI apps a minimal `PATH` that excludes `/usr/local/share/dotnet`).

`scripts/build-installer-mac.sh` produces `artifacts/installer-mac/SnapZap-<version>.pkg` via
`pkgbuild`/`productbuild`, with the NSFW model as the same kind of optional, checksummed
component the Windows installer offers. Unlike Windows it can only warn about a missing .NET
10 runtime (nothing to silently bootstrap, since the app can't be self-contained here), and a
`.pkg` has no built-in uninstaller — removal is dragging `SnapZap.app` to the Trash. It is also
unsigned/unnotarized, so Gatekeeper flags it on any Mac other than the one that built it.

### Sidecar assets (optional, ship beside the binary)
```bash
# Install the optional sidecar: NSFW ONNX model (328 MB).
# Pinned URLs, SHA-256 verified, idempotent. Writes <repo>/models and <repo>/tools, which
# SnapZap.App.csproj copies into the build output (but NOT into publish output).
scripts/install-deps.sh            # macOS / Linux
scripts\install-deps.bat           # Windows

scripts/install-deps.sh --model-only
scripts/install-deps.sh --force
scripts/install-deps.sh --dest artifacts/win-x64   # for a published binary

# Build the .onnx from PyTorch weights instead of downloading a conversion (needs Python)
scripts/export-nsfw-model.sh

# Validate model's scores against labeled fixtures
PC_NSFW_MODEL="$PWD/models/nsfw.onnx" \
PC_NSFW_FIXTURES=/path/to/fixtures \
dotnet test --filter "Category=NsfwModelValidation"
```

Windows sidecar layout:
```
SnapZap.App.exe
wwwroot/                    (published automatically — required, not optional)
appsettings.json
models/nsfw.onnx            (optional — enables NSFW scoring)
models/preprocessor_config.json
```

Graceful degradation: a missing NSFW model disables NSFW scoring and nothing else. Duplicate detection — exact, variant and burst — is entirely in-process and needs no sidecar.

The sidecars are excluded from publish output (`CopyToPublishDirectory="Never"`), so the published folder is the same size whether or not they are installed locally. Populate a publish folder with `scripts/install-deps.sh --dest artifacts/win-x64`, or tick the model component in the installer.

---

## Project Structure

| Path | Purpose |
|---|---|
| `src/SnapZap.Core/` | Portable core logic: scanning, hashing, dedup, NSFW, blur/EXIF, export, delete, platform interfaces |
| `src/SnapZap.App/` | ASP.NET Core host + Blazor Server UI (`Components/`, `Services/`, `wwwroot/`) |
| `tests/SnapZap.Tests/` | xUnit test suite + fixtures |
| `CHANGELOG.md` | User-facing release notes, one section per version — update it alongside every version bump |
| `docs/DESIGN.md` | Architecture, decisions, data model, pipeline, safety invariants |
| `docs/ROADMAP.md` | Current status + prioritized next steps |
| `docs/BLAZOR-MIGRATION.md` | The SPA → Blazor Server migration (completed) |
| `docs/UI-FEATURES.md` | Quick per-feature reference for recent UI work — what each does, where the code lives, the one invariant not to regress |
| `docs/WINDOWS-VERIFY.md` | Checklist for four Windows-only code paths |
| `docs/ANDROID-PORT-PLAN.md` | Implementation plan for an Android port (not started) — architecture bet, `IPlatformServices` Android impl, storage model, ordered tasks |
| `installer/SnapZap.iss` | Inno Setup definition for the Windows installer (per-user, optional model component, WebView2 bootstrap) |
| `scripts/build-installer.bat` | Publish + package in one step; the supported way to build the Windows installer |
| `installer-mac/` | `.app` bundle template (`Info.plist.template`, `launcher.sh`, `find-dotnet.sh`) + `.pkg` postinstall scripts (`postinstall-app.sh`, `postinstall-model.sh`, `notify.sh`) |
| `scripts/build-installer-mac.sh` | Publish + assemble `SnapZap.app` + package as a `.pkg` in one step; the supported way to build the macOS installer |
| `scripts/install-deps.{sh,bat}` | One-command install of both optional sidecars (pinned + checksummed) |
| `scripts/export-nsfw-model.sh` | Build the ONNX model from PyTorch weights instead of downloading it |
| `scripts/make-icons.py` | Rebuild the icon set from the source art (needs Pillow; outputs are committed) |
| `assets/icon/` | Source art for the app icon + the generated 1024/256 PNGs |
| `artifacts/` | Publish output (built, not committed) |
| `models/` | NSFW ONNX model + preprocessor config (installed, not committed) |

### Key subdirectories in `App/`

- **Components/** — Razor components. `Pages/Home.razor` composes the whole app; `Toolbar`
  (scan bar), `FilterBar` (filters + selection menus, library actions), `SelectionBar`
  (contextual Export/Delete/Hide, only while something is selected), `FolderTreeView` (the
  entire left pane), `PhotoGrid`, `Card`, `Toast`, and the `ExportDialog` / `HideDialog` /
  `ExtractDialog` / `UndoDialog` / `PreviewModal` / `DependencyDialog` / `SetupDialog` /
  `ShortcutsDialog` overlays.
- **Services/** — `AppState` (scoped per circuit: view state + operations, replaces the old
  `app.js` state object), `ImageView` (record wrapping `ImageRecord` for display),
  `DependencyChecker` (validates the optional sidecars, singleton).
- **wwwroot/** — `app.css` (the "Darkroom" design system), `interop.js` (grid geometry
  measurement, scroll windowing, arrow-key focus movement), and two icon files. `favicon.ico`
  is also the `.exe` icon (`<ApplicationIcon>` in the csproj points here so the tab, the
  taskbar and the window cannot disagree); `snapzap.png` is the same artwork at 128px for the
  mark beside the wordmark in `Home.razor`. Both are generated from
  `assets/icon/snapzap-source.png` by `scripts/make-icons.py` — edit the source art, not the
  outputs, and `VerifyPublishOutput` will catch it if either goes missing from a publish.

### Key subdirectories in `Core/`

- **Scanning/** — file enumeration, SHA-256 hashing (`Hasher.cs`), cache-probe optimization
- **Dedup/** — `ExactDuplicateFinder` (SQL on our hashes), `PerceptualHash` (272-bit dHash, 4 rotations), `VariantFinder` / `BurstFinder`, `SimilarityGrouper` (complete-linkage), `DedupSettings` (in `meta`)
- **Nsfw/** — ONNX Runtime inference (`OnnxNsfwClassifier`), image preprocessing, score thresholds
- **Analysis/** — EXIF extraction (`ExifExtractor`), blur detection via Laplacian variance (`BlurDetector`)
- **Export/** — `ExportEngine` (copy/move/hardlink modes), manifest writer, hash verification, collision-safe naming
- **Delete/** — recycle-bin operations, undo/restore, platform-specific `ITrashService`
- **Stego/** — `StegoEngine` (hides photos by appending a payload to a carrier image's own bytes, plus a small footer — the carrier still opens normally as an image, any image format), `PayloadCrypto` (optional AES-GCM keyed from a passphrase via PBKDF2, versioned blob with a stored iteration count), `PayloadZipper` (in-memory `ZipArchive` wrapper with local collision-safe naming)
- **Data/** — SQLite schema, `ImageRepository`, `DupeRepository`, cache layer
- **Platform/** — `IPlatformServices` interface + macOS impl (`MacOsServices`), Windows impl stub (`WindowsServices`)
- **Imaging/** — `SkiaImageService` for decode, EXIF geometry, thumbnail generation (never use ImageSharp — Six Labors split license trap)

---

## Architecture Highlights

### Four-interface platform abstraction

All Windows-specific logic is behind `IPlatformServices` (in `Platform/IPlatformServices.cs`):

| Method | Purpose | Status |
|---|---|---|
| `ITrashService` | Recycle bin operations (delete → recycle → restore) | macOS impl working; Windows impl written, never executed |
| `ILinkService` | Hardlink creation + stat (for export hardlink mode) | macOS impl (libc `link()`) working; Windows impl written, never executed |
| `IInferenceProvider` | NSFW ONNX inference (CPU on macOS, optionally DirectML on Windows) | CPU impl working; DirectML not started |
| `IAppHost` | Window host + browser launch (Photino/WebView2 on Windows) | Implemented in `App/Services/AppHost.cs` and verified on Windows 11: `PhotinoAppHost` (embedded WebView2, STA thread) with `BrowserAppHost` as fallback and as the macOS host |

There is no issue tracker on this repo; open work lives in [docs/ROADMAP.md](docs/ROADMAP.md)
and [docs/WINDOWS-VERIFY.md](docs/WINDOWS-VERIFY.md).

Development on macOS with Windows-impl stubs means:
- **Cross-platform code paths execute fully** (scan, dedup, export, delete all work on Mac)
- **Windows-only paths (4 above) must be verified on Windows hardware** (see [docs/WINDOWS-VERIFY.md](docs/WINDOWS-VERIFY.md))

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
  → in-process perceptual matching (variant + burst groups)
  → SQLite (faceted query)
  → AppState (per-circuit view state) → Blazor grid (select, filter)
  → Export (copy/move/hardlink → hash-verify → manifest)
  → Delete (optional, separate mode, recycle + undo toast/panel)
```

### UI layer (Blazor Server)

Components call Core services directly — there is no HTTP/JSON boundary for app logic. Only
three endpoints remain: `/api/health`, `/api/thumb/{hash}`, and `/api/full/{id}` (guarded to
paths present in the catalog).

Two things to know before touching the grid:

1. **Shared state needs explicit subscription.** `AppState` is mutated by sibling components
   whose parameters into each other never change, so Blazor's diffing skips re-rendering them.
   Every component reading `AppState` subscribes to `AppState.Changed` in `OnInitialized` and
   unsubscribes in `Dispose`. Same pattern for `DependencyChecker.Changed`.
2. **The grid does not use `<Virtualize>`.** That component derives its viewport from an
   IntersectionObserver on its own spacers, which never resolves inside this flex/scroll
   layout — it reports zero capacity and renders no rows at all. `PhotoGrid` instead windows
   rows itself using geometry measured in `interop.js` (`SetViewport` / `SetScroll`), which is
   deterministic. Verified at 4,000 photos with ~120 cards in the DOM.
3. **`.grid` must keep `overflow-anchor: none`.** Scroll anchoring — the browser adjusting
   `scrollTop` to keep content visually still when things above it resize — feeds back into
   the windowing, which resizes spacers on every scroll. The loop runs the grid to one end or
   the other on a single wheel gesture. It only reproduces with real wheel/trackpad input;
   programmatic scrolls suppress anchoring, so setting `scrollTop` from the console looks fine.

### Optional sidecar validation

`DependencyChecker` (singleton) resolves the two optional sidecars at launch and exposes them
to the UI. Both are genuinely optional — the core workflow (scan, exact duplicates, export,
delete) works with neither installed, so a missing one is always presented as reduced
capability, never an error.

| Sidecar | Unlocks | Resolution order |
|---|---|---|
| `models/nsfw.onnx` | NSFW scoring | `PC_NSFW_MODEL` → `models/` beside the binary |

Detection reuses the same lookup the feature itself performs, so "Ready" in the UI can never
disagree with what happens at run time. When something is
missing the app shows a dialog once at startup (dismissible, and suppressible via
`settings.json` in app-data), flags it with a pip on the **Setup** rail icon, and annotates
the affected toolbar buttons. **Add new sidecars in `DependencyChecker.Detect()`** — the
dialog, the Setup panel and the pip all render from that one list.

### Dependency notes

**Never use ImageSharp.** Its Six Labors Split License has a revenue threshold ($1M) above which you must pay. SkiaSharp covers every need (decode, thumbnail, geometry) and is unambiguously MIT.

All other deps are MIT or Apache-2.0. No paid, no subscription-gated.

### ONNX & model assumptions

- **ONNX Runtime inference** runs in-process (no server, no cloud).
- **Model is the Falconsai ViT** trained on ~10k labeled images (Apache-2.0).
- **Output is a single float `[0, 1]`** (probability of NSFW), per input image.
- **Preprocessing** (resize, normalize) is in `NsfwPreprocess.cs`; the config is in `preprocessor_config.json` (downloaded alongside the model).
- **Model validation** (test category `NsfwModelValidation`) requires labeled fixtures to confirm the model's scores are sensible. Tests skip unless you provide them.

### The NSFW flag rule (`NsfwSettings`, in `Core/Nsfw/NsfwDecision.cs`)

The rule reads **two** numbers, and both halves are load-bearing. Rationale and the measurements
are in the type's own docs; the parts you must not undo:

- **Every photo is scored twice: whole frame, and as nine overlapping tiles.** The model only
  ever sees a 224×224 thumbnail of its input, so a person occupying part of a wide photo is
  destroyed by the resize before inference. Measured: one image scored **0.0014** whole-frame and
  **0.9983** on the tile containing the subject. No threshold recovers that.
- **Tiles are combined with `mean`, never `max`.** ⚠ This is the safety-critical one. A clothed
  head-and-shoulders portrait contains one tile that is nearly all skin, which the model scores
  ~0.99 — max-over-tiles flagged **8 of 17** photos in a real family album. The mean flagged
  none, across 263 control images. `NsfwBandTests.An_ordinary_photo_of_a_person_is_not_flagged`
  pins it.
- **`TileMeanFlag` below `SafeTileMeanFloor` (0.50) is where false positives start**, and they
  arrive fast: 0 of 17 at 0.50, 6 of 17 at 0.40. The setting allows it; the UI warns at the edge.
- **`nsfw_tile_mean` is nullable and that is the mechanism, not an oversight.** Null means
  "scored whole-frame only", which is how a tiled run finds rows a previous quick run left
  behind — exactly the job `dupe_checked_kinds` does for dedup. Without it, turning the deeper
  setting on would appear to do nothing.
- **Thresholds and depth are user settings** (`settings.json`, via `DependencyChecker.Nsfw`),
  presented as Cautious / Balanced / Eager with per-threshold overrides. Read them through
  `AppState.NsfwRule` — never re-derive a comparison inline, or the badge, the filter and the
  counts start disagreeing, which is precisely what moving the rule into Core fixed.
- **Changing a threshold re-judges, it does not re-score.** `AppState.Reband()` exists because
  the band counts are cached per load.

### Duplicate detection (v2 — in-process)

Full rationale in [docs/DEDUP-V2.md](docs/DEDUP-V2.md). The short version:

- **Three kinds, and the split is safety-critical.** `Exact` (SHA-256), `Variant` (same shot,
  resized/re-encoded/rotated) and `Burst` (same scene seconds apart). `AppState.InScope` (for
  `SelectionScope.DuplicateExtras`) and `ReclaimableBytes` filter to
  `DupeKindExtensions.IsBulkSelectable()` — i.e. `Exact | Variant`. A burst is five *different
  photographs*; sweeping them into a delete would make this a shredder.
  **Never re-derive that rule inline; a new kind must not become bulk-selectable by default.**
  The gate lives on the `InScope` predicate, not on the command, so the button's count, its
  reclaimable-bytes label and what it selects cannot disagree. `DuplicateKeepers` is intentionally
  unfiltered — a burst's keeper is a survivor like any other.
- **The hash** is a 272-bit gradient hash on a 17×17 square grid, stored for **all four rotations**
  (160 bytes in `images.phash`). Do *not* "optimise" it to store only the smallest of the four:
  noise flips which rotation wins, so near-identical photos canonicalise to different orbit members
  and stop matching entirely.
- **It rides on the scan's existing decode.** `Scanner.Analyze` calls `DecodeGray` once and feeds
  both `BlurDetector.ScoreFrom` and `PerceptualHash.FromGray`. Adding a second decode here would
  give back the main saving of the rewrite.
- **Grouping is complete-linkage, not union-find** (`SimilarityGrouper`). Perceptual similarity is
  not transitive — A~B and B~C does not give A~C — so union-find collapses a real library into one
  group of thousands with all but one flagged for deletion. A group is a clique; pairs are
  processed closest-first; ids break ties so runs are reproducible. `GrouperTests` locks this in.
- **Thresholds are in bits out of 272** and are *not* comparable to czkawka's old
  `--max-difference 10`. Defaults live in `DedupSettings`.
- **Exact and Burst detection have no on/off switch.** Exact because a duplicate finder that
  cannot find identical files is not one; Burst because a safety rule a checkbox can disable is not
  one. Burst used to default to off, which did not leave bursts ungrouped — it left them grouped as
  `Variant`, which *is* bulk-selectable, so the guard was inert as shipped. Only `VariantEnabled`
  and the thresholds are settings.
- **The kinds overlap, so `GroupReconciler` enforces one group per relationship** after the
  detectors run, dropping any group whose members are all covered by a stronger one. Precedence is
  **Exact → Burst → Variant**. Burst beating Variant is the safety-critical, non-obvious direction:
  pixel distance cannot separate them (two different burst frames measured 9 bits apart; one photo
  and its 50% resize 16 apart), so the detector that consulted the capture clock wins. Counts in
  the status line are read back off the catalogue after this pass, never summed from the detectors.
- **Settings live in the `meta` table**, not `settings.json`, because `images.dupe_checked_kinds`
  only means anything against the settings that produced it and both must reset with `catalog.db`.
  (`settings.json` still exists for app-level prefs — `DependencyChecker.StoredSettings`.)
- **Matching uses a pigeonhole band prefilter** (`VariantFinder.BandPrefilterPairs`), not brute
  force. The original brute-force claim undersold its own cost — with rotations on, the five-word
  `DistanceTo` loop runs once per rotation and rarely reaches 0 to trigger the early break, ~8×
  worse than "one XOR and one PopCount" implied. Splitting the 272 bits into `VariantMaxBits + 1`
  bands and indexing rotation 0 of every signature is **exact, not approximate**: two hashes within
  threshold can differ in at most that many bands, so by pigeonhole at least one band must match
  identically. Measured (M1, synthetic/uniform-random hashes — real libraries cluster and will skew
  differently, see `docs/PERFORMANCE.md`): 6.5×/12×/20× at 20k/50k/100k. The brute-force sweep is
  retained as a fallback for high thresholds (`VariantMaxBits` up to 60 collapses band width below
  `VariantFinder.BandWidthFloor`) and as the reference path parity tests compare against.
- **Not detected: crops and reframes.** No grid hash can find them. Documented and accepted in
  DEDUP-V2 §9, not an oversight.

### Gotchas

1. **`dotnet add package ... -v q` misparses `q` as a version.** Use `dotnet add package ... --prerelease` or omit `-v` entirely.

2. **Hashing is SHA-256, not BLAKE3**, for portability (hardware-accelerated in .NET, zero native build dependencies). Swappable in `Scanning/Hasher.cs` if needed.

3. **Duplicate detection has no external dependency.** All three detectors run in-process; the `czkawka_cli` sidecar was removed in v2 (docs/DEDUP-V2.md). Exact detection has no setting to switch it off.

4. **Windows-specific code is stubbed on macOS.** The `IPlatformServices` methods for Windows (Recycle Bin, hardlinks, DirectML) are macOS-compatible stubs or no-ops. Full end-to-end testing happens on macOS, but **those paths must be verified on Windows hardware** before shipping. The window host is no longer among them — it is implemented and verified. `dotnet test` on Windows currently fails four tests that pass on macOS; they are listed in [WINDOWS-VERIFY.md](docs/WINDOWS-VERIFY.md) §6 and two of them are the Recycle Bin and hardlink implementations failing for real.

9. **Never set `<OutputType>WinExe</OutputType>` on `SnapZap.App`.** It builds and publishes cleanly and silently drops `wwwroot/_framework` — the SDK target that contributes Blazor's scripts is gated on `'$(OutputType)' == 'Exe'` exactly. The result is an app that runs, serves 200s and renders unstyled and dead. The console window is hidden at run time instead (`ConsoleWindow.HideIfOwned` in `App/Services/AppHost.cs`), only when SnapZap owns the console, so `dotnet run` from a terminal keeps its log. The `VerifyPublishOutput` target in the csproj fails the build if this ever regresses.

5. **SkiaSharp's native assets** are in the Core csproj (macOS + Win32 .nupkg packages). The publish step bundles them into the self-contained .exe.

6. **App data** (catalog DB, thumbnails, manifests, `settings.json`) lives under
   `Environment.SpecialFolder.LocalApplicationData` + `SnapZap` — i.e.
   `~/Library/Application Support/SnapZap` on macOS, `%LOCALAPPDATA%\SnapZap` on Windows. It is
   deliberately outside the scanned folder so the app never writes into the user's library
   (DESIGN safety invariant §7.5). Delete `catalog.db` to reset to a clean state.

7. **macOS: never publish self-contained or single-file.** The apphost binary is SIGKILLed on
   launch by endpoint security (see the macOS build section). Ship `dotnet <dll>` behind a
   script launcher instead.

8. **macOS `.app` launcher must resolve `dotnet` by absolute path.** Finder gives GUI apps a
   minimal `PATH` without `/usr/local/share/dotnet`, so `command -v dotnet` alone fails when
   launched by double-click even though it works from a terminal.

10. **The app version has exactly one source: `<Version>` in `src/SnapZap.App/SnapZap.App.csproj`.**
    Bump it there and nothing else — the Windows installer (`installer/SnapZap.iss`) reads it back
    out of the built `.exe` via `GetVersionNumbersString`, and `scripts/build-installer-mac.sh`
    greps it straight out of the csproj, so both installer filenames follow automatically. The
    example paths in `README.md`'s installer walkthroughs are illustrative only (not read by any
    script) — update them for freshness when you bump the version, but they're cosmetic, not a
    second source of truth. Add a matching entry to [`CHANGELOG.md`](CHANGELOG.md) in the same
    change — it has no automation behind it and goes stale the moment a version bump skips it.

---

## Development Notes

- **Test naming:** Tests are organized by feature (ScannerTests, DedupTests, NsfwTests, ExportTests, DeleteTests, AnalysisTests, PlatformTests).
- **Nullable reference types** are enabled; use `#nullable disable` only where cross-cutting OSS interop forces it.
- **Implicit usings** are enabled; global `using` statements in each project.
- **No DTO/JSON layer for app logic.** Razor components call Core services directly, so there is no serialization boundary and no camelCase mapping to keep in sync. (`JsonCamel` and `ExportRequestDto` were deleted in the Blazor migration.) The only JSON left is the manifest writer's output.
- **Platform-specific tests** use `[SkippableFact]` to skip gracefully on the wrong OS (e.g., Recycle Bin tests skip on macOS).

---

## Windows Release Checklist

See [WINDOWS-VERIFY.md](docs/WINDOWS-VERIFY.md) for the four Windows-only paths that must be validated on Windows hardware:
1. Recycle Bin (delete → restore)
2. Shell restore (from Recycle Bin context menu)
3. Hardlinks (export hardlink mode)
4. WebView2 (native window host, optional; currently browser-launch only)

All four are behind `IPlatformServices` so the impact is isolated. Prototype implementations exist (macOS versions or stubs); production use requires Windows validation.
