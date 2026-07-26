# SnapZap

Your personal photo assistant. Tired of duplicates and blurry shots cluttering your library?
SnapZap finds them all, flags what you don't want, then exports the clean results for Plex or
backup. Offline, free, and always in your control.

No cloud, no subscriptions, no paid dependencies. Two promises hold throughout:

- **Your original folder is never touched** unless you explicitly ask — an export in *Move*
  mode, or the opt-in "also recycle what I didn't pick" box.
- **Nothing is ever hard-deleted.** Deleting goes to the Recycle Bin, and every batch is
  reversible from **History**.

---

## Contents

**Using SnapZap**
[Install and run](#install-and-run) ·
[The screen](#the-screen) ·
[Scanning](#scanning-a-folder) ·
[Duplicates](#finding-duplicates) ·
[Reviewing duplicates](#reviewing-duplicates) ·
[Explicit content](#scoring-explicit-content) ·
[Filtering](#browsing-filtering-and-sorting) ·
[Selecting](#selecting-photos) ·
[Exporting](#exporting) ·
[Deleting](#deleting-and-undo) ·
[Setup](#setup) ·
[Shortcuts](#keyboard-shortcuts) ·
[Data locations](#where-snapzap-keeps-its-data) ·
[Troubleshooting](#troubleshooting)

**Building it**
[From source](#building-from-source) ·
[Tests](#tests) ·
[Project layout](#project-layout) ·
[Development note](#development-note)

---

## What it does

- **Duplicates** — three kinds, all detected in-process with no external tool: byte-identical
  files (by checksum), the same shot resized/re-encoded/rotated, and bursts of the same scene
  seconds apart. Bursts are grouped for review but deliberately excluded from bulk selection —
  they're different photographs, not copies.
- **Explicit content** — a single 0–1 score per image (Falconsai ViT via ONNX), sorted into
  four bands. Needs one optional model download; everything else works without it.
- **Blur** — variance-of-Laplacian sharpness score.
- **Dates** — capture date and camera from EXIF; browse and export by year/month.
- **Review** — windowed thumbnail grid (smooth at tens of thousands of photos), faceted
  filters, single / range / smart selection, and full keyboard triage.
- **Export** — copy · move · hardlink, into `date` / `mirror` / `flat` structure, with
  pre-flight, hash-verification, collision-safe naming, resume, and a written manifest.
- **Delete** — recycles to the OS bin with a one-click **undo** and a full history panel.

---

# Using SnapZap

## Install and run

SnapZap is a single file. Download `SnapZap.App.exe`, put it anywhere, and double-click it.
There is nothing to install — no .NET runtime, no setup wizard, no account.

Double-clicking starts a small local server on your own machine and opens SnapZap in your
browser. Everything stays on your computer; nothing is uploaded anywhere. Close the browser tab
and the console window when you're done.

*(Prefer to build it yourself? See [Building from source](#building-from-source).)*

### The one optional add-on

Explicit-content scoring needs a machine-learning model (~328 MB) that isn't bundled with the
app. **Everything else works without it** — scanning, all three kinds of duplicate detection,
blur, dates, export, and delete are built in and need no download.

If the model isn't installed, SnapZap says so once at startup, marks the **Setup** gear with a
badge, and greys out **Score NSFW**. To add it, run the installer that ships with the source:

```
scripts\install-deps.bat                        Windows
scripts/install-deps.sh                         macOS / Linux
```

It downloads from a pinned URL, verifies the SHA-256 before putting anything in place, and skips
work that's already done. To install beside an already-published app rather than into the repo:

```
scripts\install-deps.bat --dest artifacts\win-x64
```

Then open **Setup** (the gear, top right) and press **Check again** — no restart needed.

| Add-on | Unlocks | Size | Source |
|---|---|---|---|
| `models/nsfw.onnx` + `preprocessor_config.json` | Explicit-content scoring | 328 MB | [Falconsai ViT](https://huggingface.co/Falconsai/nsfw_image_detection), Apache-2.0, via a [pinned ONNX conversion](https://huggingface.co/onnx-community/nsfw_image_detection-ONNX) |

Sidecar layout beside the executable:

```
SnapZap.App.exe
wwwroot/                          (published automatically)
models/nsfw.onnx                  (optional — enables explicit-content scoring)
models/preprocessor_config.json
```

## The screen

| Area | What's there |
|---|---|
| **Top bar** | The folder box and three actions: **Scan**, **Find duplicates**, **Score NSFW**. Keyboard-help and **Setup** icons at the far right. |
| **Filter bar** | **Filters**, the selection commands (**All shown**, **Flagged**, **Extras**, **Invert**, **Clear**, **More**), **Review duplicates**, and **History**. |
| **Left pane** | The folder tree of whatever you scanned. |
| **Middle** | The summary line, the sort control, and the photo grid. |
| **Bottom** | The selection bar — appears once you've picked at least one photo, and carries **Export…** and **Delete…**. |

## Scanning a folder

Type or paste a folder path into the box at the top and press **Scan** (or just hit
<kbd>Enter</kbd>). A leading `~` expands to your home folder.

SnapZap walks the folder and everything beneath it, recording for each photo a checksum, the
dimensions, the capture date and camera from EXIF, a sharpness score, a visual fingerprint, and
a thumbnail. A progress bar shows a running count, and **Stop** is always available — everything
analysed so far is kept.

**Readable:** `.jpg` `.jpeg` `.png` `.webp` `.gif` `.bmp` `.tif` `.tiff`

**Not readable yet:** HEIC/HEIF and AVIF (modern phone formats) and camera RAW (`.cr2`, `.cr3`,
`.nef`, `.arw`, `.dng`, `.orf`, `.rw2`, `.raf`, `.srw`, `.pef`). These are **counted and
reported, never touched** — expand the note above the grid for the breakdown by format. This
matters because a folder of iPhone HEICs would otherwise just look like an empty scan.

**Re-scanning is fast.** A file whose path, size and modification time are unchanged is reused
from the catalogue rather than re-read, so scanning the same 40,000-photo library again is close
to instant. Files that have left the folder are dropped from the catalogue, and the status line
says how many.

## Finding duplicates

Press **Find duplicates**. Entirely in-process — no internet connection, no extra download.

SnapZap looks for three kinds, and the difference matters:

| Kind | What it means | Safe to bulk-delete? |
|---|---|---|
| **Identical** | Byte-for-byte the same file, in two places. Found by checksum. | Yes |
| **Same shot** | One photograph, resized, re-saved, converted or rotated. | Yes |
| **Burst** | Different photographs of the same scene, seconds apart. | **No — review by hand** |

That last row is the important one. A burst of five frames is **five different photographs**,
not five copies of one. SnapZap groups them so you can look at them, but deliberately leaves
them out of the **Extras** bulk-select — sweeping them into a delete would throw away real
photos. For a burst, you pick the keeper yourself.

When it finishes the status line reports each kind — *"12 identical, 40 same shot, 6 burst
groups"*. Anything that limited the search (photos with no fingerprint yet, a detector switched
off) is appended rather than hidden, so *"0 groups"* and *"0 groups, but half the library has no
signature"* never look the same.

Cards in the grid pick up a badge: **✓** for the copy being kept, **▣** for an extra copy. Hover
either for the group number and kind.

### Tuning it

**Setup → What "Find duplicates" checks**:

- **Identical files** — always on. A duplicate finder that can't find identical files isn't one.
- **The same shot at another size** — on by default.
  - **Also match rotated copies** — on by default, slightly slower.
  - **How different they may look** — default **20 of 272**. Lower is stricter; raise it to
    catch more, but past a point it starts calling two different photos the same one.
- **Bursts** — always on, no switch. The window is tunable: **frames within** *N* seconds,
  default **3 s**.

Burst detection needs an EXIF capture time. Photos without one are skipped by it, so a burst
with no timestamps isn't protected from bulk selection.

Changes save immediately. Run **Find duplicates** again to apply them.

> **Not detected: crops and reframes.** A cropped photo is a different image as far as the
> fingerprint is concerned. That's a known limit, not a bug.

## Reviewing duplicates

Press **Review duplicates** in the filter bar. This is the good way to work through them: one
group at a time, copies side by side, with the facts that decide which survives.

Whatever is identical across the group is greyed out — it can't help you choose. Whatever
differs stays bright, and a strict winner (largest resolution, biggest file) is highlighted. The
subtitle names what to compare on: *"different resolutions"*, *"different file sizes"*,
*"identical copies — choose by location"*.

**Click a copy to make it the keeper.** The others become extras.

| Control | Does |
|---|---|
| **‹ ›** or <kbd>←</kbd> <kbd>→</kbd> | Move between groups |
| <kbd>1</kbd>–<kbd>9</kbd> | Pick that copy as the keeper |
| **Compare full size** | Full resolution, panes panning together, zoom Fit / 100% / 200% / 400% |
| **Select all N extras** | Select every extra copy across the library, and close |
| <kbd>Esc</kbd> | Close |

**Nothing is deleted here.** It only decides which copy survives an export or delete you run
afterwards.

## Scoring explicit content

Press **Score NSFW** (needs the optional model — see [above](#the-one-optional-add-on)).

Every photo gets one score from 0.00 to 1.00 and lands in a band:

| Band | Score | Meaning |
|---|---|---|
| **Likely explicit** | 0.85 and up | The model is fairly confident |
| **Not sure** | 0.20 – 0.85 | The pile actually worth looking at |
| **Looks clean** | below 0.20 | |
| **Not checked yet** | — | Never scored |

**This is a guess, not a verdict.** The model is right often enough to be useful and wrong often
enough that you should look before acting — which is what "Not sure" is for. It's deliberately
small, so it stays a review queue rather than a category.

Flagged photos get a shield badge; hover it for the score and the threshold.

## Browsing, filtering and sorting

### The folder tree

The left pane mirrors the folder you scanned. Click a folder to show only its photos, or
**All folders** to go back. Selecting a folder includes everything beneath it by default.

Each row shows its photo count, plus two markers: **⧉** means photos here are in duplicate
groups (greyed **⧉** means checked, none found), and a **small dot** means some analysis hasn't
reached this folder yet — hover the row to see which step.

Keyboard: <kbd>↑</kbd> <kbd>↓</kbd> move, <kbd>→</kbd> expands, <kbd>←</kbd> collapses,
<kbd>Home</kbd> / <kbd>End</kbd> jump.

### Filters

Press **Filters**; the button shows a count when any are active.

| Filter | Options |
|---|---|
| **Explicit content** | Any · Likely explicit · Likely or not sure · Looks clean · Not checked yet — each with a live count |
| **Blurrier than** | Slider, 0–300. At 0 it's off. Photos at or below the value read as soft. |
| **Duplicates only** | Only photos in a duplicate group |
| **Folder** | Set by clicking the tree; the ✕ clears it |
| **Year** | Any year found in your photos' EXIF |
| **Advanced → Include subfolders** | On by default. Off, the folder matches *exactly* — so **All shown** picks up that directory and nothing under it. |

**Clear filters** resets everything.

### Sorting

Above the grid: Scan order · Date taken · Name · File size · Sharpness · Explicit content, with
an arrow to flip direction. Picking a sort also picks the direction most people want from it —
choosing "Explicit content" puts the highest scores first, not the tamest.

### The summary line

Live: how many photos are shown of how many total, how many duplicate extras exist and how much
space they'd reclaim, how many haven't been checked for explicit content, and how many are
selected.

## Selecting photos

Selection is how you tell SnapZap what to export or delete. Nothing acts on your photos until
something is selected.

**By hand** — click to select or deselect, **shift-click** to select the range from the last
photo you clicked, **double-click** to open the preview.

**By command** — the **Select** commands in the filter bar. Each shows how many photos it would
pick, and hovering explains it:

| Command | Selects |
|---|---|
| **All shown** | Everything the current filters show (<kbd>Ctrl</kbd>+<kbd>A</kbd>) |
| **Flagged** | Photos the model called likely explicit |
| **Extras** | Every extra copy — **identical and same-shot only, never burst frames**. The tooltip says how much space they'd reclaim. |
| **Invert** | Everything shown that isn't selected now |
| **Clear** | Deselect everything (<kbd>Ctrl</kbd>+<kbd>D</kbd>) |
| **More → Not sure** | Photos the model was unsure about |
| **More → Keepers** | The one copy being kept from each duplicate group |

Commands that can't do anything stay visible but greyed, and say why — *"Press Find duplicates
to look for copies"*, *"Nothing scored yet — press Score NSFW"*, *"Only burst frames left —
those are separate shots, so review them by hand"*.

> **Two SnapZap windows on the same library share one selection.** A command like **Extras**
> *replaces* the selection rather than adding to it, so a press in one window changes what the
> other is about to act on.

### The preview

Press <kbd>Enter</kbd> or double-click a photo.

- <kbd>←</kbd> <kbd>→</kbd> step through photos in the order shown on screen
- <kbd>X</kbd> selects or deselects
- <kbd>I</kbd> toggles the details panel — filename, folder, dimensions, size, capture date,
  camera, explicit-content band and score, focus score, duplicate-group membership
- <kbd>Esc</kbd> or a click on the background closes it

## Exporting

The main event: write the photos you picked into a clean destination folder. Select some photos,
then press **Export…** in the bottom bar.

**Destination** — the full path where the clean library goes, starting from the root of the
drive. Relative paths are rejected, because one would quietly resolve somewhere inside the
folder you're cleaning.

**Transfer mode**

| Mode | What happens |
|---|---|
| **Copy (safest)** | Originals stay exactly where they are. The default. |
| **Move (verify, then remove source)** | Written, hash-verified against the original, and *only then* is the source removed. |
| **Hardlink (zero-copy, same drive)** | A second name for the same data — instant and free, but destination and source must share a drive. |

**Structure**

| Structure | Layout |
|---|---|
| **By date** | `YYYY/YYYY-MM/` from the capture date. The default. |
| **Mirror source tree** | Keeps your existing folder layout. Needs a folder scanned this session. |
| **Flat** | Everything in one folder, with collision-safe renaming. |

**Also recycle what you didn't pick** — an opt-in tick box that sends every photo you *didn't*
select to the Recycle Bin, but only **after** the export is written and verified. Two things to
be clear about: **filters don't limit it** (it covers the whole catalogue, not just what's on
screen), and **it's reversible** from History. Ticking it opens a confirmation naming the exact
count and total size.

### Check destination

Press **Check destination** before exporting. You get how many photos and bytes will be written,
free space at the destination and whether that's enough, whether hardlinks are available on that
pair of drives, and an example of the folder structure.

**Export stays disabled until this check passes.** Change anything — destination, mode,
structure — and the check is invalidated, so the button can never be armed for a plan nobody
verified. If free space can't be read the export isn't blocked, but SnapZap says plainly that
nothing has confirmed it will fit.

### While it runs, and after

Progress shows the phase, the count and the current file. **Stop** is always available:
everything already copied is verified and complete, and re-running resumes where it left off
rather than starting over.

When it's done you get a one-line summary — exported, already there, failed, recycled — and the
path to a **manifest** (a CSV recording exactly what went where). In Move mode it also says how
many originals were removed from the source, and that it's undoable from History.

## Deleting and undo

Select photos and press **Delete…** in the bottom bar. A confirmation names the exact count and
total size. Confirming moves them to the **Recycle Bin** — not a hard delete. A toast appears:

> Recycled 128 photos to the Recycle Bin.  **[Undo]**

**Undo** puts them straight back. If the toast has gone, everything is still in **History**.

### History

The **History** button lists every batch SnapZap has recycled or moved, newest first, with its
timestamp and photo count. Each row has **Restore**, which puts the files back where they came
from. Three kinds of entry appear:

- **Deleted** — a normal delete
- **Recycled during export** — the "also recycle what I didn't pick" option
- **Moved out by export** — an export in Move mode. These were *relocated*, not binned, so
  looking for them in the Recycle Bin would be looking in the wrong place.

You can also restore from the Recycle Bin's own right-click menu in Explorer.

## Setup

The gear icon, top right. A badge on it means something optional isn't installed.

**Add-on status** — whether the explicit-content model was found, and which copy is in use. Drop
a file in and press **Check again**; no restart needed. Detection uses the same lookup the
feature itself performs, so "Ready" here can never disagree with what actually happens at run
time.

**What "Find duplicates" checks** — see [Tuning it](#tuning-it).

**Catalogue** — how many photos have been analysed, and how much space the database and
thumbnails take. The catalogue spans *every folder you've ever scanned*, because that's what
makes re-scanning fast, even though only the current folder is ever shown.

**Forget everything…** discards all of it: hashes, scores, dates, keeper decisions, thumbnails.
**No photo is deleted or moved** — a scan rebuilds all of it, and anything already in the
Recycle Bin stays restorable from History.

## Keyboard shortcuts

Press the **?** icon in the top right for this list in the app.

**In the grid**

| Key | Action |
|---|---|
| <kbd>↑</kbd> <kbd>↓</kbd> <kbd>←</kbd> <kbd>→</kbd> | Move between photos |
| <kbd>X</kbd> or <kbd>Space</kbd> | Select / deselect |
| <kbd>Shift</kbd>+<kbd>X</kbd> | Extend the selection to here |
| <kbd>Enter</kbd> | Open the preview |
| <kbd>Home</kbd> / <kbd>End</kbd> | First / last photo |
| <kbd>Ctrl</kbd>+<kbd>A</kbd> | Select everything shown |
| <kbd>Ctrl</kbd>+<kbd>D</kbd> | Clear the selection |

<kbd>Tab</kbd> from the toolbar reaches the grid, which is a single tab stop — the arrows move
within it.

**In the preview** — <kbd>←</kbd> <kbd>→</kbd> previous / next · <kbd>X</kbd> select ·
<kbd>I</kbd> details · <kbd>Esc</kbd> close

**In Review duplicates** — <kbd>←</kbd> <kbd>→</kbd> previous / next group ·
<kbd>1</kbd>–<kbd>9</kbd> pick a keeper · <kbd>Esc</kbd> close

**Everywhere** — <kbd>Enter</kbd> scans when focus is in the folder box · <kbd>Esc</kbd> closes
the open dialog, preview or menu

<kbd>Ctrl</kbd>+<kbd>A</kbd> and <kbd>Ctrl</kbd>+<kbd>D</kbd> are deliberately ignored while a
dialog is open, so a stray keypress can't re-aim a delete confirmation already on screen. *(On
macOS these are <kbd>⌘</kbd>+<kbd>A</kbd> / <kbd>⌘</kbd>+<kbd>D</kbd>, and the app shows them
that way.)*

## Where SnapZap keeps its data

Everything SnapZap writes lives in `%LOCALAPPDATA%\SnapZap` — deliberately **outside** the
folder you scan, so the app never writes into your photo library. (On macOS,
`~/Library/Application Support/SnapZap`.)

| File | What |
|---|---|
| `catalog.db` | The catalogue: hashes, scores, dates, fingerprints, keeper decisions |
| `thumbs/` | Generated thumbnails |
| `manifests/` | The CSV written by each export |
| `settings.json` | App preferences, such as whether you've dismissed the add-on prompt |

Deleting `catalog.db` resets SnapZap to a clean state; **Setup → Forget everything** does the
same from inside the app.

## Troubleshooting

**"No photos in \<folder\>"** — either the folder holds no readable images, or it's a folder of
folders and you meant to point at one of them. If there's a note above the grid about unreadable
formats, that's your answer.

**My iPhone photos don't show up.** They're probably HEIC, which SnapZap can't decode yet (nor
AVIF or camera RAW). They're counted and reported above the grid, and left completely untouched.
Converting to JPEG makes them visible.

**The filters show nothing.** The empty-state message names the reason. The common one: an
explicit-content filter over a library nothing has scored matches nothing, because every photo
is still "Not checked". Run **Score NSFW** or clear the filter.

**"Score NSFW" is greyed out.** The model isn't installed — see
[the optional add-on](#the-one-optional-add-on).

**"Find duplicates" is greyed out.** Scan a folder first. Duplicate detection itself is built in
and needs no download.

**Export won't let me press the button.** Press **Check destination** first. If you've changed
the destination, mode or structure since the last check, check again — the button deliberately
disarms so it can never run an unverified plan.

**"Enter a full path, starting from the root of the drive."** Relative destinations are
rejected, because they'd resolve somewhere unexpected — possibly inside the folder you're
cleaning. Use something like `D:\Photos\Clean`.

**Mirror structure won't export.** Mirror keeps your existing folder layout, so it needs a
source folder scanned in this session. Scan the source again, or choose **By date** or **Flat**.

**I deleted the wrong photos.** Open **History** and press **Restore** on the batch, or use the
Recycle Bin's own restore in Explorer. Nothing SnapZap deletes is ever hard-deleted.

**A burst got grouped as duplicates.** Intended — they're grouped for review, and **not** picked
up by **Extras**, so a bulk delete won't touch them. Use **Review duplicates** to choose frames,
or widen the burst window in **Setup** if one burst is being split across groups.

**Scanning is slow the first time.** The first scan analyses every photo; later ones reuse the
catalogue and are close to instant. **Stop** is safe at any point — the work done so far is kept.

---

# Building it

## Building from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

### Run in development

```bash
dotnet run --project src/SnapZap.App
```

Then open the printed `http://localhost:<port>` URL. Environment knobs:

- `PC_NSFW_MODEL=/path/to/nsfw.onnx` — location of the model (default: `models/nsfw.onnx`
  beside the binary)
- `PC_NO_BROWSER=1` — don't auto-open a browser (used by tests/headless)

### The Windows executable

From Windows or macOS:

```bash
dotnet publish src/SnapZap.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -o artifacts/win-x64
```

Produces a single self-contained `SnapZap.App.exe` (~130 MB, no .NET install needed).
Double-clicking it starts the local server and opens the app in your browser.

The optional model is deliberately **not** copied into the publish output, so the `.exe` stays
~130 MB whether or not you installed it locally. To ship it, point the installer at the publish
folder: `scripts\install-deps.bat --dest artifacts\win-x64`.

### macOS

macOS is the development platform, but the app runs there too. Publish framework-dependent and
RID-specific:

```bash
dotnet publish src/SnapZap.App -c Release -r osx-arm64 --self-contained false -o artifacts/mac
dotnet artifacts/mac/SnapZap.App.dll
```

For a double-clickable `SnapZap.app`, wrap that output in a bundle whose
`Contents/MacOS/<name>` is a shell script that `cd`s to the payload and runs
`dotnet SnapZap.App.dll`. Two things make this necessary:

- **Don't use `--self-contained` or `PublishSingleFile` on macOS.** The published apphost is
  ad-hoc signed and unnotarized, and endpoint security on managed Macs SIGKILLs it at launch
  (exit 137, no output, no crash report). Running the DLL through `dotnet` avoids this.
- **Resolve `dotnet` by absolute path in the launcher.** Finder hands GUI apps a minimal `PATH`
  that excludes `/usr/local/share/dotnet`, so a bare `dotnet` works from a terminal but not from
  a double-click.

### Building the model yourself

If you'd rather build the `.onnx` from the original PyTorch weights than trust a prebuilt
conversion, `scripts/export-nsfw-model.sh` does that. It needs Python and downloads ~2 GB of
torch/transformers into a throwaway venv; the result is equivalent.

Validate the model's scores against your own labeled images before trusting them:

```bash
PC_NSFW_MODEL="$PWD/models/nsfw.onnx" \
PC_NSFW_FIXTURES=/path/to/fixtures \   # fixtures/nsfw/*.jpg and fixtures/sfw/*.jpg
dotnet test --filter Category=NsfwModelValidation
```

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
| `scripts/install-deps.{sh,bat}` | One-command install of the optional model |
| `scripts/export-nsfw-model.sh` | Build the ONNX model from PyTorch weights yourself (rarely needed) |
| `docs/DESIGN.md` | Architecture, decisions, safety invariants |
| `docs/DEDUP-V2.md` | How the three duplicate detectors work, and why |
| `docs/ROADMAP.md` | Current status + prioritized next steps |
| `docs/BLAZOR-MIGRATION.md` | The SPA → Blazor Server migration (completed) |
| `docs/WINDOWS-VERIFY.md` | Checklist for the four Windows-only code paths |

## Development note

Developed on macOS, shipped for Windows. ~90% of the code is portable and tested locally; the
four Windows-only paths (Recycle Bin, hardlinks, DirectML GPU, native window host) are behind
interfaces with macOS dev implementations and **must be verified on Windows hardware** — see
[WINDOWS-VERIFY.md](docs/WINDOWS-VERIFY.md).
