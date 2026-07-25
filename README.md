# SnapZap

Offline, free, Windows desktop tool to declutter a photo folder: find duplicates and
near-duplicates, flag NSFW and blurry images, browse by date, then **export the clean set to
a destination folder** (which Plex, or anything else, can watch). Nothing is ever hard-deleted
and the source folder is never touched unless you ask.

No cloud, no subscriptions, no paid dependencies. See [DESIGN.md](docs/DESIGN.md) for the full
architecture and rationale.

## What it does

- **Duplicates** — exact duplicates from content hashes (always), visually-similar images via
  the optional [`czkawka_cli`](https://github.com/qarmin/czkawka) sidecar.
- **NSFW** — a single 0–1 score per image (Falconsai ViT via ONNX), with a threshold slider.
- **Blur** — variance-of-Laplacian sharpness score.
- **Dates** — capture date + camera from EXIF; browse and export by year/month.
- **Review** — windowed thumbnail grid (smooth at tens of thousands of photos), faceted
  filters, single / range / smart selection, and full keyboard triage.
- **Export** — copy · move · hardlink, into `date` / `mirror` / `flat` structure, with
  pre-flight, hash-verification, collision-safe naming, resume, and a written manifest.
- **Delete** — a separate mode; recycles to the OS bin with a one-click **undo** panel.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build. The Windows `.exe` is
  self-contained and needs nothing installed; the macOS build needs the .NET 10 runtime.
- (Optional) `czkawka_cli` for similar-image detection — placed beside the binary or on PATH.
- (Optional) the NSFW ONNX model for NSFW scoring — see below.

Both optional pieces are validated at launch: if either is missing the app says so once,
explains what it unlocks and exactly where to put the file, and keeps a live status list under
**Setup** in the sidebar with a *Check again* button (no restart needed). Everything else —
scanning, exact duplicates, blur, dates, export, delete — works without them.

## Run (development)

```bash
dotnet run --project src/SnapZap.App
```

Then open the printed `http://localhost:<port>` URL. Environment knobs:

- `PC_NSFW_MODEL=/path/to/nsfw.onnx` — location of the NSFW model (default: `models/nsfw.onnx`
  beside the binary).
- `PC_NO_BROWSER=1` — don't auto-open a browser (used by tests/headless).

## Get the NSFW model (one-time, optional)

The model (~350 MB, Apache-2.0) is **not** committed. Export it once:

```bash
scripts/get-nsfw-model.sh          # writes models/nsfw.onnx + preprocessor_config.json
```

Validate the model's scores against your own labeled images before trusting them:

```bash
PC_NSFW_MODEL="$PWD/models/nsfw.onnx" \
PC_NSFW_FIXTURES=/path/to/fixtures \   # fixtures/nsfw/*.jpg and fixtures/sfw/*.jpg
dotnet test --filter Category=NsfwModelValidation
```

## Build the Windows executable

From macOS or Windows:

```bash
dotnet publish src/SnapZap.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o artifacts/win-x64
```

Produces a single self-contained `SnapZap.App.exe` (~130 MB, no .NET install needed).
Double-clicking it starts the local server and opens the app in your browser.

## Build for macOS

macOS is the development platform, but the app runs there too. Publish framework-dependent and
RID-specific:

```bash
dotnet publish src/SnapZap.App -c Release -r osx-arm64 --self-contained false -o artifacts/mac
dotnet artifacts/mac/SnapZap.App.dll
```

To get a double-clickable `SnapZap.app`, wrap that output in a bundle whose
`Contents/MacOS/<name>` is a shell script that `cd`s to the payload and runs
`dotnet SnapZap.App.dll`. Two things make this necessary:

- **Don't use `--self-contained` or `PublishSingleFile` on macOS.** The published apphost is
  ad-hoc signed and unnotarized, and endpoint security on managed Macs SIGKILLs it at launch
  (exit 137, no output, no crash report). Running the DLL through `dotnet` avoids this.
- **Resolve `dotnet` by absolute path in the launcher.** Finder hands GUI apps a minimal
  `PATH` that excludes `/usr/local/share/dotnet`, so a bare `dotnet` works from a terminal but
  not from a double-click.

### Sidecar layout on Windows

```
SnapZap.App.exe
wwwroot/                     (published automatically)
models/nsfw.onnx             (optional — enables NSFW scoring)
models/preprocessor_config.json
czkawka_cli.exe              (optional — enables similar-image detection)
```

Both optional pieces degrade gracefully: without the model, NSFW scoring is disabled;
without `czkawka_cli.exe`, only exact-duplicate detection runs.

## Tests

```bash
dotnet test
```

The suite runs on macOS or Windows. Two tests are gated on assets that can't live in the repo
and skip unless you provide them: the ONNX plumbing test (`PC_TEST_ONNX`) and the real-model
score validation (`PC_NSFW_MODEL` + `PC_NSFW_FIXTURES`).

## Project layout

| Path | What |
|---|---|
| `src/SnapZap.Core` | Portable logic: scan, hash, dedup, NSFW, blur/EXIF, export, delete, platform interfaces |
| `src/SnapZap.App`  | ASP.NET Core host + Blazor Server UI (`Components`, `Services`, `wwwroot`) |
| `tests/SnapZap.Tests` | xUnit suite |
| `scripts/get-nsfw-model.sh` | One-time ONNX model export |
| `docs/DESIGN.md` | Architecture, decisions, safety invariants |
| `docs/ROADMAP.md` | Current status + prioritized next steps |
| `docs/BLAZOR-MIGRATION.md` | The SPA → Blazor Server migration (completed) |
| `docs/WINDOWS-VERIFY.md` | Checklist for the four Windows-only code paths |

## Development note

Developed on macOS, shipped for Windows. ~90% of the code is portable and tested locally; the
four Windows-only paths (Recycle Bin, hardlinks, DirectML GPU, native window host) are behind
interfaces with macOS dev implementations and **must be verified on Windows hardware** — see
[WINDOWS-VERIFY.md](docs/WINDOWS-VERIFY.md).
