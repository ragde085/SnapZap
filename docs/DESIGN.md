# SnapZap — Design

**Status:** design agreed, implementation not started
**Target platform:** Windows (x64), single self-contained `.exe`
**Development platform:** macOS (cross-compiled; see [Platform abstraction](#platform-abstraction))

---

## 1. Goal

Point the app at a folder of photos. It finds duplicates, near-duplicates, NSFW images,
and blurry/low-quality images. You review everything in a fast visual grid, select what to
keep, and **export the clean set to a destination folder** that Plex watches.

The source folder is never modified unless you explicitly ask for it.

### Non-goals

- No cloud, no network calls, no paid dependencies, no subscriptions.
- Not a photo editor, viewer, or library manager — it is a triage-and-export tool.
- No Plex API integration. Plex watches the destination folder; that *is* the integration.

---

## 2. Architecture

```
┌─────────────────────────────────────────────┐
│  Browser tab (Photino/WebView2 planned)     │
│  ┌───────────────────────────────────────┐  │
│  │  Razor components — grid, preview,    │  │
│  │  select, filters                      │  │
│  └───────────────────────────────────────┘  │
└──────────────────┬──────────────────────────┘
                   │ SignalR circuit (Blazor Server)
                   │ + HTTP for image bytes only
┌──────────────────┴──────────────────────────┐
│  ASP.NET Core host (in-process)             │
│    AppState → Scanner ─ Analyzer ─ Exporter │
└──┬─────────┬──────────┬──────────┬──────────┘
   │         │          │          │
 SQLite   ONNX RT   SkiaSharp   czkawka_cli
 (cache) (NSFW ViT) (decode/    (subprocess)
                     thumbs)
```

Everything runs in one process on localhost. The UI is served by the same binary that
hosts it. `czkawka_cli` is the only external executable.

Components call Core services directly over the circuit — there is no JSON/DTO layer for
application logic. Only image bytes still travel over HTTP: `/api/thumb/{hash}` and
`/api/full/{id}`, plus `/api/health` for test readiness. Being single-user and on localhost is
what makes Blazor Server's per-interaction round trip a non-issue.

### Why this shape

- **C#/.NET over Python:** self-contained single-`.exe` deployment (no runtime install, no
  venv), faster parallel file scanning, and ONNX Runtime has first-class C# bindings so
  there is no inference-speed penalty.
- **Web UI over native XAML:** a dense windowed photo grid with lazy thumbnails, shift-click
  ranges and live filters is substantially less work in CSS grid than in XAML. (Originally a
  vanilla-JS SPA; migrated to Blazor Server — see [BLAZOR-MIGRATION.md](BLAZOR-MIGRATION.md).)
- **Czkawka over a hand-rolled deduper:** it is mature, Rust-fast, and already does both
  exact and perceptual matching. Reinventing it has no upside.

### Implementation deviations (as built)

- **Hashing is SHA-256, not BLAKE3.** Built into .NET, hardware-accelerated, zero native
  dependency to complicate the Mac→Windows cross-compile. Isolated in `Scanning/Hasher.cs`,
  swappable later. See that file's comment.
- **Exact duplicates come from our own content hashes, not Czkawka.** Since every image is
  SHA-256 hashed during the scan, byte-identical files are found in pure SQL
  (`Dedup/ExactDuplicateFinder.cs`) with no external tool. **Czkawka is used *only* for
  *similar* (perceptual) images** (`Dedup/CzkawkaFinder.cs`). Consequence: exact dedup works
  even with no Czkawka installed; only near-duplicate detection needs the sidecar.
- **Czkawka JSON parser is validation-pending.** Czkawka's similar-images JSON schema is not
  publicly documented and could not be captured on the dev Mac (binary not installed). The
  parser is defensive (recursively finds arrays of `{path,...}` objects) and unit-tested
  against assumed shapes, but must be verified against real `czkawka_cli -C` output before
  similar-detection results are trusted.

---

## 3. Dependencies

All MIT or Apache-2.0. Nothing paid, nothing with a revenue-threshold license.

| Job | Package | License |
|---|---|---|
| Window host | Photino.NET (WebView2) | MIT |
| Web layer | ASP.NET Core Minimal APIs | MIT |
| NSFW inference | `Microsoft.ML.OnnxRuntime` (+ `.DirectML` on Windows) | MIT |
| Image decode / thumbnails | SkiaSharp | MIT |
| EXIF | MetadataExtractor | Apache-2.0 |
| Cache / state | `Microsoft.Data.Sqlite` | MIT |
| Duplicate detection | `czkawka_cli` (external process) | MIT |
| NSFW model | Falconsai/nsfw_image_detection (ViT → ONNX) | Apache-2.0 |
| Recycle Bin | `Microsoft.VisualBasic.FileIO` (Windows) | MIT |
| Blur detection | hand-rolled Laplacian variance over SkiaSharp pixels | — |

> **Do not use ImageSharp.** Its Six Labors Split License requires payment above a revenue
> threshold. SkiaSharp covers every need here and is unambiguously free.

### Sidecar assets (not embedded in the `.exe`)

The NSFW ONNX model (~350 MB) and `czkawka_cli.exe` ship *beside* the binary, not inside
it. Both are detected at startup; if missing, the UI degrades gracefully and points the
user at the download rather than failing hard. This keeps the executable lean and lets each
piece be updated independently.

---

## 4. Pipeline

```
Folder pick
   │
   ├─► Enumerate files ──► cache probe (path, size, mtime)
   │                          │hit → reuse row, skip all analysis
   │                          │miss ↓
   │                       content hash (BLAKE3)
   │                          ↓
   │                    parallel analysis
   │                      ├─ NSFW score   (ONNX)
   │                      ├─ Blur score   (Laplacian variance)
   │                      ├─ EXIF         (date, camera)
   │                      └─ Thumbnail    (SkiaSharp)
   │
   └─► czkawka_cli subprocess ──► exact + similar groups
                   │
                   ▼
              SQLite  ──►  Blazor faceted grid  ──►  Export
```

### Caching strategy

Re-hashing every file on every scan is itself expensive. Two-tier:

1. **Cheap probe:** `(path, file_size, mtime)`. Unchanged → reuse the cached row entirely,
   including its hash and all four signals. No I/O beyond the directory entry.
2. **Full analysis** only when that triple changes or the path is new.

This is what makes the second scan of a 40k-photo library near-instant. It is not an
optimization to defer — without it the app feels broken on re-open.

### Four independent signals

Every image gets `{dupe_group, nsfw_score, blur_score, exif_date}`. The UI is then a
faceted query over those columns, which is why the features compose: *"blurry duplicates
from 2019 scoring above 0.8 NSFW"* is a `WHERE` clause, not a feature.

---

## 5. Data model

```sql
CREATE TABLE images (
  id            INTEGER PRIMARY KEY,
  path          TEXT    NOT NULL UNIQUE,
  content_hash  TEXT    NOT NULL,
  file_size     INTEGER NOT NULL,
  mtime         INTEGER NOT NULL,
  width         INTEGER,
  height        INTEGER,
  format        TEXT,
  nsfw_score    REAL,
  blur_score    REAL,
  exif_taken    INTEGER,          -- unix utc, null when absent
  exif_camera   TEXT,
  thumb_path    TEXT,
  analyzed_at   INTEGER
);
CREATE INDEX ix_images_hash ON images(content_hash);
CREATE INDEX ix_images_nsfw ON images(nsfw_score);
CREATE INDEX ix_images_taken ON images(exif_taken);

CREATE TABLE dupe_groups (
  id          INTEGER PRIMARY KEY,
  kind        TEXT NOT NULL,      -- 'exact' | 'similar'
  similarity  TEXT                -- czkawka level, for 'similar'
);

CREATE TABLE dupe_members (
  group_id  INTEGER NOT NULL REFERENCES dupe_groups(id),
  image_id  INTEGER NOT NULL REFERENCES images(id),
  is_keeper INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (group_id, image_id)
);

CREATE TABLE export_runs (
  id            INTEGER PRIMARY KEY,
  started_utc   INTEGER NOT NULL,
  finished_utc  INTEGER,
  destination   TEXT NOT NULL,
  mode          TEXT NOT NULL,    -- 'copy' | 'move' | 'hardlink'
  structure     TEXT NOT NULL,    -- 'date' | 'mirror' | 'flat'
  reject_action TEXT NOT NULL,    -- 'leave' | 'recycle'
  status        TEXT NOT NULL     -- 'running' | 'done' | 'failed' | 'cancelled'
);

CREATE TABLE export_items (
  run_id      INTEGER NOT NULL REFERENCES export_runs(id),
  image_id    INTEGER NOT NULL REFERENCES images(id),
  dest_path   TEXT,
  status      TEXT NOT NULL,      -- pending|copied|verified|skipped|failed
  skip_reason TEXT,
  error       TEXT,
  PRIMARY KEY (run_id, image_id)
);

CREATE TABLE undo_log (
  id            INTEGER PRIMARY KEY,
  batch_id      TEXT    NOT NULL,
  op            TEXT    NOT NULL, -- 'recycle' | 'move'
  original_path TEXT    NOT NULL,
  new_location  TEXT,
  ts_utc        INTEGER NOT NULL,
  restored      INTEGER NOT NULL DEFAULT 0
);
```

`export_items` doubles as the resume journal and the source of the exported manifest.

---

## 6. Export engine

The final step, and the primary workflow. Select a destination, choose how files get there,
and the clean set is written out.

### Runtime options

| Setting | Values |
|---|---|
| **Transfer mode** | `copy` · `move` · `hardlink` |
| **Structure** | `date` (`YYYY/YYYY-MM/`) · `mirror` (source tree) · `flat` |
| **Rejects** | `leave` (untouched in source) · `recycle` (after verified export) |

**Hardlink mode** is offered automatically when source and destination share an NTFS
volume. It produces a fully curated destination folder at zero disk cost and near-zero
time, because both paths reference the same data. It silently falls back to `copy` across
volumes. For a large library this is dramatically better than copying.

Under `date` structure, images with no usable EXIF timestamp land in `undated/` rather than
being guessed at from filesystem mtime — a wrong date is worse than an obvious gap.

### Collision handling

Three distinct cases, never conflated:

| Case | Action |
|---|---|
| Destination exists, **same** content hash | Skip silently — dedup working as intended |
| Destination exists, **different** hash | Auto-suffix `name (2).ext`, record in manifest |
| Any other conflict | Fail that item, continue the run, report it |

**Never silently overwrite.** There is no configuration that enables overwriting.

### Execution order (the safety invariant)

```
for each keeper:
    write to destination (copy / link)
    re-hash at destination
    compare against source hash
      mismatch → mark failed, LEAVE SOURCE INTACT, continue
      match    → mark verified
                 └─ move mode only: release source
after all items verified:
    reject_action == 'recycle' → send rejects to Recycle Bin (logged to undo_log)
```

A destructive step never precedes its verification. A truncated copy that deleted its own
source would be unrecoverable, so verification gates everything.

### Pre-flight

Before any bytes move: item count, total size, destination free space, estimated structure
preview. A 180 GB operation should announce itself before it starts, not when the disk
fills at 80%.

### Resume

Progress lives in `export_items`. An interrupted run restarts by skipping rows already
marked `verified` — no recopying.

### Metadata preservation

Created and modified timestamps are carried to the destination. EXIF is untouched (files
are copied byte-for-byte, never re-encoded). Without this the exported library sorts wrong
and undermines the date-based structure.

### Manifest

Each run writes CSV + JSON of every item: source, destination, status, skip reason. Written to
app-data (`<LocalApplicationData>/SnapZap/manifests/`) — **never** the destination root, which
Plex is indexing.

---

## 7. Safety invariants

These hold across every mode and are not configurable.

1. **Nothing is hard-deleted.** Removal means Recycle Bin, always.
2. **No destructive action precedes verification.** Sources are released only after the
   destination copy is hash-confirmed.
3. **No silent overwrites.** Ever, in any mode.
4. **Every destructive operation is logged** to `undo_log` with a batch id, and every batch
   is reversible from the undo panel.
5. **Scanning is read-only.** Analysis never writes to the source folder.

---

## 8. NSFW detection

Falconsai/nsfw_image_detection (ViT) exported to ONNX, producing a single 0–1 score
surfaced as a sortable badge and a threshold slider. No fixed cutoff — the user sets the
line and sees counts update live.

> ### ⚠ Highest-risk component
> Preprocessing (resize → normalize → NCHW tensor) is implemented by hand in C# rather than
> being handled by `transformers`. **Incorrect normalization constants produce confident,
> plausible-looking, completely meaningless scores** — it fails silently.
>
> Mitigation, required before any batch result is trusted:
> 1. Read the exact constants from the model's `preprocessor_config.json`. Do not assume
>    ImageNet or 0.5/0.5 defaults.
> 2. Build a fixture set of ~10 hand-labeled images.
> 3. Assert expected scores in a unit test that runs in CI.
>
> This is cheap to do and expensive to skip.
>
> **✅ VALIDATED (2026-07-24).** The real Falconsai model was exported to ONNX
> (`scripts/get-nsfw-model.sh`) and the C# path was scored against HuggingFace's reference
> `transformers` pipeline on the same images: scores matched within **0.002** (max diff
> 0.0021). Confirmed: mean/std 0.5, size 224, bilinear resample (read from the model's
> `preprocessor_config.json`), RGB channel order, and nsfw = logit index 1 (id2label
> `{0: normal, 1: nsfw}`). The preprocessing is correct. Re-run this comparison if the model
> or its config ever changes.

**Blur detection** uses variance-of-Laplacian. The threshold is resolution-dependent and
empirical, so it is exposed as a slider rather than hardcoded.

---

## 9. Platform abstraction

Development happens on macOS; the artifact is a Windows `.exe`. Roughly 90% of the codebase
is genuinely cross-platform and testable locally.

| Concern | Interface | Windows | macOS (dev) |
|---|---|---|---|
| Trash | `ITrashService` | `Microsoft.VisualBasic.FileIO` | `NSFileManager` trash |
| Hardlinks | `ILinkService` | NTFS hard links | APFS hard links |
| GPU provider | `IInferenceProvider` | DirectML | CoreML / CPU |
| Window host | `IAppHost` | Photino + WebView2 | Photino + WKWebView |

Everything else — scanning, hashing, ONNX inference, Czkawka orchestration, the export
engine, the entire UI — is portable and developed against directly.

Ship with:

```
dotnet publish -c Release -r win-x64 --self-contained
```

The four Windows-only paths above must be verified on real Windows hardware before release.
Cross-compiling proves they compile, not that they work.

---

## 10. UI

Single-page faceted browser over the four signals.

- **Grid** — virtualized, lazy-loaded thumbnails; duplicates rendered side-by-side within
  their group for direct comparison.
- **Filters** — NSFW threshold slider, blur slider, EXIF date range, folder, dupe-group-only.
- **Selection** — checkbox, shift-click range, select-folder, select-all, and
  select-all-matching-current-filter.
- **Auto-select heuristics** — within each duplicate group, pre-check all but the best
  (highest resolution → largest file → lowest NSFW). Review a proposal; don't hand-pick 500
  times.
- **Keyboard** — `space` toggle, `x` reject, `←/→` navigate, `enter` full preview.
- **Export panel** — destination picker, mode/structure/reject toggles, pre-flight summary.
- **Undo panel** — session batches with one-click restore.

---

## 11. Build order

Each step is independently verifiable against a real folder.

| # | Step | Done when |
|---|---|---|
| 1 | Scanner + SQLite cache + thumbnails | Walks 20k files fast; rescan is near-instant |
| 2 | Czkawka subprocess + group parsing | Duplicate groups visible end-to-end |
| 3 | NSFW ONNX + **labeled-fixture validation** | Test suite passes on known images |
| 4 | Blur + EXIF extraction | All four signals populated |
| 5 | Photo grid — preview, selection | Can browse and select a real library |
| 6 | Filters + auto-select heuristics | Faceted queries drive the grid |
| 7 | Export engine | Copy/move/hardlink, verified, resumable |
| 8 | Export UI + pre-flight + manifest | Full export round-trip |
| 9 | In-place delete mode + undo panel | Recycle + restore working |
| 10 | Package as self-contained `.exe` | Runs on clean Windows, no prerequisites |

Steps 1–2 come first because they make the app immediately useful against a real folder
before any ML investment.

### Implementation status (as built)

All ten steps are implemented and tested on macOS; the app runs end-to-end there and
cross-compiles to a self-contained `win-x64` single-file `.exe`. 28 tests pass; 2 are gated on
external assets (the real NSFW model + labeled fixtures) and skip without them.

Packaging deviations from the original plan:

- **Launch is browser-based, not a Photino native window (for v1).** The `.exe` starts the
  local server and opens the default browser — the simplest reliable "double-click" UX. The
  `IAppHost` seam for a WebView2/Photino native window remains for a later pass.
- **NSFW inference is CPU (ONNX Runtime) by default.** DirectML GPU acceleration is a
  Windows-runtime swap (`Microsoft.ML.OnnxRuntime.DirectML` + `IInferenceProvider`) deferred
  until it can be built and measured on Windows. CPU inference is correct today.
- **The four Windows-only paths are implemented but runtime-unverified.** Recycle Bin
  (`SHFileOperation`), restore (`Shell.Application` COM), and hardlinks (`CreateHardLinkW`) are
  written and cross-compile, but were authored on macOS. See `WINDOWS-VERIFY.md` for the
  hardware checklist. The macOS dev implementations are the tested ones.

---

## 12. Deferred

Considered and explicitly out of scope for now:

- **Plex API integration** — unnecessary; Plex watches the destination. If auto-scan proves
  unreliable, a path-scoped `/library/sections/{id}/refresh?path=…` call is a ~15-line
  addition. Do not build it preemptively.
- **NudeNet / granular body-part detection** — a single score covers the use case.
- **Video support**, RAW formats, face recognition, screenshot detection.
