# UI features — quick reference

**Status:** shipped
**Related:** [DESIGN.md](DESIGN.md) (architecture), [BLAZOR-MIGRATION.md](BLAZOR-MIGRATION.md) (the
`AppState`/`RunAsync` pattern these build on), [CLAUDE.md](../CLAUDE.md)

This is not a design doc — it's a map for whoever (human or AI) next touches one of these features,
so the "why does it work this way" doesn't have to be re-derived from the diff. Each section: what
it does, where the code lives, and the one thing you'd break by "simplifying" it.

---

## 1. Busy-operation progress: recycling, scanning, scoring

**What:** Every long-running operation (Scan, Find duplicates, Score NSFW, Move to Recycle Bin)
drives the same live counter + progress bar + Stop button in `Home.razor`'s `mhead`/`scan-stats`
block, not a bespoke spinner per feature.

**Where:**
- `AppState.RunAsync(label, body)` (`Services/AppState.cs`) is the one place that sets
  `Busy`/`BusyLabel`/`BusyDone`/`BusyTotal`/`BusyDetail`/`BusyEta`, owns the `CancellationTokenSource`
  behind `Cancel()`/`CanCancel`, and turns a thrown `OperationCanceledException` into a
  "stopped after N of M" status instead of an unhandled fault.
- `body` is `Func<Action<int,int,string?> report, CancellationToken, Task<string>>` — call
  `report(done, total, detail)` from an `IProgress<T>` adapter as work completes, and return the
  final status string.
- `DeleteService.RecycleAsync` (`Core/Delete/DeleteService.cs`) takes an
  `IProgress<DeleteProgress>?` and reports after every file, same shape as `ScanProgress`/
  `DedupProgress`/`NsfwProgress`. `AppState.DeleteAsync` wraps it in `RunAsync("Moving to the
  Recycle Bin", ...)` instead of hand-rolling its own busy/notify bookkeeping.
- `Home.razor`'s `BusyProgressNoun` switch is what turns a `BusyLabel` into the rich progress block
  (counter + bar + hint text); a label with no case there falls back to the plain
  mhead label + Stop button (that's still the right choice for one-shot batches like Forget/Restore
  that don't report incremental progress).

**Don't regress:** if you add a new long operation, route it through `RunAsync` rather than setting
`Busy`/`BusyLabel` by hand — that's what gives it cancellation, the "Failed: …" catch-all, and (once
you add a `BusyProgressNoun` case) the live counter, for free. A hand-rolled `Busy = true; ... Busy =
false;` block reintroduces exactly the bug this replaced: no progress during the run, and a Stop
button that's permanently disabled because no `CancellationTokenSource` ever existed.

The completion **toast** (`AppState.ShowToast`, rendered by `Toast.razor`) is separate from the busy
banner — it's what's still on screen after `Busy` goes back to `false`, e.g. Delete's "Recycled N
photos… Undo".

---

## 2. NSFW scoring opens the Filters tab automatically

**What:** When `AppState.NsfwAsync()` finishes (success, stopped, or failed alike), the Rail's
Filters tab comes forward on its own, so the flagged/not-sure results are the next thing on screen.

**Where:**
- `AppState.NsfwScoringFinished` (event, `Services/AppState.cs`) — invoked once, right after the
  `RunAsync` call inside `NsfwAsync()` returns. `RunAsync` never rethrows (it converts every
  exception to a status string), so this always fires once scoring stops, by whatever means.
- `Home.razor` subscribes in `OnInitializedAsync`, unsubscribes in `Dispose`, and the handler is one
  line: `_rail?.ShowFiltersTab()` — the same method the grid header's own "Filters" button calls.

**Don't regress:** this is intentionally unconditional (fires even if the model isn't installed and
nothing was scored, or the run was stopped after one photo) — the point is "scoring just happened,
here's where the results live," not "scoring succeeded." If you need it to skip a case, gate it in
the event handler, not by suppressing the event — other future listeners (there's only one today)
shouldn't have to know about NSFW-specific edge cases to just react to "a scoring pass ended."

---

## 3. Dark mode: plain `<button>`s need an explicit `color`

**What:** The preview modal's ‹›, ✕, "Details" and "Select" buttons were unreadable in dark mode —
dark text on the dark palette's near-black background.

**Root cause, and the pattern to watch for:** a plain `<button>` in this codebase is styled two
ways. Buttons using the shared `.btn` class get `color: var(--color-text)` for free. Buttons with
their *own* one-off class (`.modal .nav`, `.close`, `.details-toggle`, `.frame-select`, and
elsewhere `.cmp-zoom button`, `.help-nav button`) do **not** automatically inherit page text color —
the browser's user-agent stylesheet gives `<button>` its own `color: buttontext`, which does not
track the app's light/dark palette at all. Two fixes are both in use in `app.css`, pick whichever
already applies to the block you're editing:
- Set `color: var(--color-text)` explicitly (what the preview-modal buttons now do), or
- Use `all: unset` on the button (which resets `color` to `inherit` for free) — that's the pattern
  `.cmp-zoom button`, `.frame-zoom button`, `.help-nav button`, `.dir-row` and `.trow-action` all
  already use, which is why none of those needed this fix.

**Don't regress:** any *new* one-off button class in this file needs one of the two treatments
above. Neither `.btn` nor a bare `background`-only rule is enough on its own — a background color
with no text-color rule is exactly the bug that shipped.

---

## 4. Directory picker (browse instead of typing a path)

**What:** `DirectoryPickerDialog.razor` — an in-app folder browser (server-side
`Directory.GetDirectories`, drive/root quick-jumps, a typed-path box) wired behind a "Browse…"
button next to every plain-text *folder* field: `ScanForm`'s "Folder to scan", `ExportDialog`'s
"Destination", `ExtractDialog`'s "Output folder".

**Where:** self-contained component, no Core/service changes — it walks the filesystem directly
(`Directory.GetDirectories`, `DriveInfo.GetDrives()` on Windows) because Blazor Server already runs
on the same machine as the files; there's no remote boundary to cross. Each host component owns a
`bool _showPicker` and an `OnPicked(string path)` handler that just assigns its own text field (and,
for `ExportDialog`, re-invalidates the preflight check).

**Don't regress:**
- It's a **folder** picker only (`Directory.GetDirectories`/`Directory.Exists`), not a file picker —
  `HideDialog`'s carrier-image field and `ExtractDialog`'s source-image field are deliberately still
  plain text, because there's no file-picker version of this component yet.
- Exceptions from a permission-denied subfolder are caught in `Navigate()` and shown as an inline
  error *without* mutating `_current`/`_subfolders` — the dialog stays on the last folder that
  actually opened. If you refactor `Navigate`, keep the field assignments after the throwing calls,
  not before.
- `StartPath` is re-read every time the dialog opens (`OnParametersSet`, gated on `Show && !_wasShown`),
  not just once — reopening after the user hand-edited the text field has to start from the new
  value, not wherever browsing left off last time.

---

## 5. Rescan one folder from the folder tree

**What:** Each row in the folder tree (`FolderTreeView.razor`) has a small rescan icon that
re-analyzes just that folder — without re-scanning (or re-scoping) the whole library.

**Where:**
- `CatalogService.RescanFolderAsync(folder, progress, ct)` (`App/CatalogService.cs`) —
  deliberately does **not** touch `ScanRoot` or `LastScannedFolder` the way `ScanAsync` does. Those
  two describe the whole library's scope (what the grid, the tree, and Export's Mirror structure all
  read as "everything in scope"); narrowing them to a subfolder would silently shrink the library
  down to whatever was last rescanned.
- Pruning is still correctly scoped to the subfolder without any extra code: `PathScope.Where(root)`
  is a **prefix match** (`path >= $prefix AND path < $prefixEnd`), so
  `ImageRepository.ProbeMap`/`DeleteMissing` called with a subfolder as `root` can only ever
  touch rows already under that subfolder.
- `AppState.ScanAsync` and `AppState.RescanFolderAsync` both call a shared
  `ScanBodyAsync(folder, isLibraryRoot, report, ct)` — the `isLibraryRoot` flag is the only branch:
  whether `ScannedFolder` gets updated, and which of `catalog.ScanAsync`/`catalog.RescanFolderAsync`
  runs. Dedup always re-runs afterward against the *whole* library root
  (`ScannedFolder ?? folder`), never just the rescanned subfolder — a photo that changed in one
  folder can newly match (or stop matching) a duplicate anywhere else in the catalogue.

**Don't regress:** if `ScanBodyAsync` ever needs a third mode, extend the flag/branch — don't let a
subfolder rescan start setting `ScannedFolder` under any condition. That's the one invariant this
whole feature exists to protect.

---

## 6. Preview zoom

**What:** The photo preview (`PreviewModal.razor`) has a Fit/100%/200%/400% toolbar — the same
ladder `DupeReview`'s compare view already used — plus `+`/`-`/`0` keyboard shortcuts (`0` = fit).

**Where:** `_zoom` (`0` = fit) and `Zooms = [0, 100, 200, 400]` mirror `DupeReview.razor` exactly,
including the CSS trick: past "fit", the frame switches from `display:grid; place-items:center;
overflow:hidden` to `display:block; overflow:auto` (`.modal .frame.zoomed` in `app.css`). Centering
a grid/flex container that also clips overflow makes the top-left of the overflowing content
unreachable by scrolling — there's no scroll offset that reaches it, because the browser centers
the *overflow* symmetrically. Dropping centering once zoomed is the fix, not a scroll hack.

**Don't regress:** zoom state is **not** reset when navigating between photos (←/→) or reopening the
modal — same choice `DupeReview` already made for its own zoom, and matches `AppState.PreviewDetails`
(the details panel) already persisting across photos. If a future change wants per-photo zoom reset,
that's a deliberate behavior change to flag, not an oversight to "fix".

---

## 7. Thumbnail size control

**What:** A Small/Medium/Large segmented control next to Sort (`Home.razor`) changes how many
columns the photo grid fits per row.

**Where:** The grid doesn't use `<Virtualize>` (see CLAUDE.md/BLAZOR-MIGRATION.md — it derives
column count from measuring `gridEl.clientWidth` against a target `MIN_COL` in `interop.js`, not from
CSS). `AppState.ThumbSize`/`ThumbSizePixels(size)` just remember which preset is selected, for the
UI to show as checked; `snapzap.setCardSize(px)` (new in `interop.js`) is what actually changes
`MIN_COL` (now a `let`, was a `const`) and calls `report()` to re-measure and push a fresh
`SetViewport` immediately — the identical path a window resize already takes.

**Don't regress:** `ThumbSizePixels` is an **instance** method on `AppState`, not static — Home.razor
calls it as `AppState.ThumbSizePixels(size)` where `AppState` is the *injected instance*, not the
type (the field and the type share a name, `@inject AppState AppState`). A static method with that
same call syntax needs the C#, "identical simple names" disambiguation rule to resolve at all;
keeping it an instance method sidesteps that subtlety entirely. Don't make it `static` again without
double-checking every call site still compiles.

---

## 8. "Include subfolders" lives in the folder tree

**What:** Picking a folder in `FolderTreeView.razor` and deciding whether to bring in everything
beneath it now happen in the same tab. The Filters tab shows the same state read-only (a
`facet-note`, same treatment the folder name itself already got), instead of a second editable
checkbox for the same `AppState.IncludeSubfolders` property.

**Where:** `AppState.IncludeSubfolders` itself didn't change — it's the existing filter property
(`Services/AppState.cs`) that narrows `InFolderScope` from a prefix match to an exact match. Only
where it's *editable* moved, from `Rail.razor`'s Filters facet into `FolderTreeView.razor`, rendered
right below the search box whenever `AppState.Folder.Length > 0`.

**Don't regress:** don't re-add an editable checkbox in the Filters tab — that reintroduces two
places that can toggle the same state, which is exactly what this change removed. If Filters needs
more detail than the current read-only note, extend the note, not add a second control.

---

## 9. History (undo) dialog shows thumbnails per batch

**What:** `UndoDialog.razor`'s history table now renders a small thumbnail strip (up to 8, plus a
"+N" overflow label) under each batch's description, so you can see what's in a batch before
restoring it — instead of only a timestamp and a bare count.

**Where:**
- `undo_log` gained a nullable `content_hash` column (`Database.cs` `Schema` + `AddColumnIfMissing`,
  since existing catalogues need the guarded `ALTER TABLE`, same pattern as `phash`/`nsfw_tile_mean`).
  `DeleteService`/`ExportEngine`'s `LogUndo` now take the `ImageRecord.ContentHash` already in hand at
  the recycle/move call site and persist it — no new decode or lookup.
- `DeleteService.ItemsInBatch(batchId, limit = 8)` is the new per-item query (`UndoBatch` stays
  aggregate-only, for the existing count/restored columns); `AppState.ItemsInBatch` just forwards it.
- The strip reuses `/api/thumb/{hash}` unchanged — that endpoint reads straight from the on-disk
  thumbnail cache keyed by content hash and never touches the `images` table, so it still resolves
  after `RecycleAsync` deletes the row. A restored item's thumbnail is shown dimmed (`.undo-thumb.restored`)
  rather than removed, so a partially-restored batch still shows the whole set.

**Don't regress:**
- Don't try to wire this into `PreviewModal`/`/api/full/{id}` — that endpoint requires a live `images`
  row *and* the original file still at its original path, both of which a recycle/move breaks by
  design. The thumbnail cache is the only asset guaranteed to survive past the catalog row's deletion.
- Rows logged before this change have `content_hash = NULL` (migration doesn't backfill — the source
  `ImageRecord` is long gone by the time the column exists) and are skipped in the strip rather than
  rendering a broken `<img>`; keep that null-check when touching the loop.
