# Blazor Server Migration Plan

Migrate SnapZap's UI from the vanilla-JS SPA (`wwwroot/index.html` + `app.js` + `style.css`,
served by minimal-API + SSE endpoints) to **Blazor Server** Razor components.

**This is a UI-layer rewrite only.** `SnapZap.Core`, all engines/services, the SQLite schema,
the platform interfaces, and the entire test project stay untouched. The HTTP/JSON boundary
between C# and JS is removed — components call Core services directly.

Last updated: 2026-07-24. This is instructions, not code — write the code as you execute each
step.

---

## 1. Why Blazor Server (recap)

- **No serialization boundary.** Components render `ImageRecord`, `DupeGroup`, `ExportResult`
  directly. The camelCase/PascalCase bug class (and `JsonCamel`, the DTOs) disappears.
- **Built-in `<Virtualize>`.** Windowed grid rendering for free — covers roadmap item P1.5 and
  makes the grid faster than the current full-`innerHTML`-rebuild approach.
- **C# end-to-end.** Progress becomes `IProgress<T>` → `StateHasChanged()`, not an EventSource
  stream parsed by hand. No `/api/*` for app logic.
- **Localhost single-user** neutralizes Blazor Server's only real weakness (per-event network
  round-trips) — sub-millisecond locally.

---

## 2. What stays vs. what changes

| Stays (do not touch) | Changes (rewrite) |
|---|---|
| `SnapZap.Core/**` (all logic) | `Program.cs` (host wiring) |
| `tests/SnapZap.Tests/**` | `wwwroot/index.html`, `app.js` → deleted |
| `CatalogService`, platform DI | `style.css` → `wwwroot/app.css` (reused) |
| `/api/thumb/{hash}` endpoint | SSE + JSON API endpoints → deleted |
| Browser auto-open, `PC_NO_BROWSER` | `ExportRequestDto.cs`, `JsonCamel.cs` → deleted |

**Endpoints to KEEP:** `/api/health` (test readiness), `/api/thumb/{hash}` (image `src`),
and ADD `/api/full/{id}` (full-res preview — folds in roadmap P1.3).

**Endpoints to REMOVE:** `/api/scan`, `/api/images`, `/api/dedup`, `/api/nsfw-scan`,
`/api/dupe-groups`, `/api/export/preflight`, `/api/export/run`, `/api/delete`,
`/api/undo/batches`, `/api/undo/{batchId}`. All replaced by direct service calls from
components.

Images are still fetched over HTTP (`<img src="/api/thumb/...">`) — that does not change with
Blazor; only app logic/state stops going over the wire.

---

## 3. Target structure

Create under `src/SnapZap.App/`:

```
Program.cs                         (rewritten)
Components/
  App.razor                        root document: <html>, <head>, <HeadOutlet/>, blazor.web.js
  Routes.razor                     <Router> over this assembly
  _Imports.razor                   global usings for components
  Layout/
    MainLayout.razor               minimal shell — just @Body (no nav chrome needed)
  Pages/
    Home.razor                     @page "/" — the whole app; owns nothing, delegates to AppState
  PhotoGrid.razor                  <Virtualize> grid of cards
  Card.razor                       one thumbnail cell (badges, selection, click handlers)
  Toolbar.razor                    folder input + Scan/Dedup/NSFW buttons + status text
  Sidebar.razor                    filters + selection buttons + actions
  ExportDialog.razor               destination/mode/structure/reject, pre-flight, run
  UndoDialog.razor                 batch list + restore
  PreviewModal.razor               full-res image + metadata
Services/
  AppState.cs                      scoped per-circuit view state + operations
  ImageView.cs                     record: ImageRecord + dupe info + thumb/full URLs
wwwroot/
  app.css                          (moved from style.css, + Blazor error-UI rules)
```

Delete after migration: `wwwroot/index.html`, `wwwroot/app.js`, `wwwroot/style.css`,
`ExportRequestDto.cs`, `JsonCamel.cs`.

`SnapZap.App.csproj` already uses `Microsoft.NET.Sdk.Web`; Blazor Server needs **no extra
NuGet package** in .NET 10 (it's in the shared framework).

---

## 4. `AppState` — the heart of the port

A **scoped** service (`builder.Services.AddScoped<AppState>()` — one per circuit). It holds the
view model and exposes operations; components subscribe to a `Changed` event and call
`StateHasChanged()`. This replaces the `state` object in `app.js`.

Responsibilities (name the members after the current `app.js` functions so the mapping is
obvious):

- **Data:** `IReadOnlyList<ImageView> Images`, `Dictionary<long, DupeInfo> DupeOf`,
  `HashSet<long> Selected`, current filter fields (`NsfwMin`, `BlurMax`, `DupesOnly`,
  `Folder`, `Year`), `string? ScannedFolder`, `string Status`.
- **Load:** `LoadAsync()` — pull images + dupe groups from the repos via `CatalogService`,
  build `Images`/`DupeOf`, recompute facet lists. Replaces `loadAll()`.
- **Filtering:** `IEnumerable<ImageView> Filtered()` and the per-image `Matches()` predicate.
  Replaces `filteredImages()` / `matches()`.
- **Selection:** `Toggle(id)`, `SelectRange(fromId, toId, visibleOrder)`, `SelectAllVisible()`,
  `SelectVisibleFolder()`, `SelectDupeExtras()`, `ClearSelection()`. Replaces the selection
  functions. `SelectRange` needs the current visible ordering (pass the filtered id list).
- **Operations (call Core directly, report progress):**
  - `ScanAsync(folder, IProgress<ScanProgress>)` → `CatalogService.ScanAsync`.
  - `DedupAsync(folder)` → `new DuplicateService(Db).DetectAsync`.
  - `NsfwAsync(IProgress<NsfwProgress>)` → `new NsfwScorer(Db, modelPath).ScoreAllAsync`.
  - Export/delete/undo are invoked from their dialogs but may live here too for one home.
- **Facets:** `IReadOnlyList<string> Folders`, `IReadOnlyList<int> Years` (recomputed in Load).
- **Event:** `event Action? Changed;` + a `Notify()` helper that raises it. Components do
  `AppState.Changed += StateHasChanged;` in `OnInitialized` and unsubscribe in `Dispose`.

`ImageView` wraps `ImageRecord` plus `ThumbUrl => $"/api/thumb/{ContentHash}"`,
`FullUrl => $"/api/full/{Id}"`, and the resolved `DupeInfo?` (group id, kind, isKeeper). Folder
and year are computed from `Path` / `ExifTaken` like the JS `folderOf`/`yearOf`.

**Progress → UI:** create `new Progress<T>(p => { state.Status = ...; InvokeAsync(StateHasChanged); })`
in the component that starts the operation. `Progress<T>` marshals to the captured
synchronization context; in a Blazor circuit use `InvokeAsync(StateHasChanged)` to be safe.

---

## 5. Component responsibilities (no code — just contracts)

- **App.razor / Routes.razor / MainLayout.razor** — standard Blazor Server scaffolding. Root
  renders the HTML document and `<script src="_framework/blazor.web.js">`. Router maps `/` to
  `Home`. Layout is a bare `@Body`.

- **Home.razor** — `@page "/"`, `@rendermode InteractiveServer`. Injects `AppState`. Composes
  `<Toolbar/>`, `<Sidebar/>`, `<PhotoGrid/>`, and the three dialogs. Subscribes to
  `AppState.Changed`. Holds only trivial UI flags (which dialog is open, preview target).

- **Toolbar.razor** — folder text `@bind`, buttons wired to `AppState.ScanAsync/DedupAsync/
  NsfwAsync` with a `Progress<T>` that updates `Status`. Shows `AppState.Status`. Persist the
  last folder with a tiny localStorage JS-interop call OR a value in `AppState` (simpler: skip
  persistence in v1, add later).

- **Sidebar.razor** — filter inputs `@bind` to `AppState` fields (range sliders, checkbox,
  selects for folder/year); on change call `AppState.Notify()`. Selection buttons call the
  `AppState` selection ops. Actions: Export (opens dialog, enabled when `Selected.Count > 0`),
  Delete (confirm → `DeleteService.RecycleAsync` → `LoadAsync`), Undo history (opens dialog).

- **PhotoGrid.razor** — wraps `<Virtualize Items="AppState.Filtered().ToList()" Context="img">`
  and renders a `<Card>` per item. Keep `ItemSize` roughly the card height; `<Virtualize>`
  renders only visible rows regardless of library size. Maintain the visible id order (the
  filtered list) so `Card` can request a shift-range select against it.

- **Card.razor** — `[Parameter] ImageView Img`. Renders the `<img>`, NSFW/blur/dupe badges,
  selection border. Click → `Toggle`; Shift-click → `SelectRange` using the grid's visible
  order; double-click → open preview (callback up to `Home`). Read `Selected.Contains(Img.Id)`
  for the selected style. Handling shift detection needs the mouse event args
  (`@onclick` with `MouseEventArgs`, check `e.ShiftKey`).

- **ExportDialog.razor** — bound fields for destination, mode, structure, a "recycle unselected"
  checkbox. `Pre-flight` builds an `ExportRequest` (keepers = `Selected`, rejects = unselected
  if the box is checked, `SourceRoot = AppState.ScannedFolder`) and calls
  `new ExportEngine(Db, links, trash).Plan(req)`; render the `Preflight` fields directly (no
  JSON!). `Run` calls `RunAsync(req, progress)`, then `ManifestWriter.Write(...)`, shows the
  `ExportResult` counts + manifest path, and calls `AppState.LoadAsync`. Inject `ILinkService`,
  `ITrashService`, `CatalogService`.

- **UndoDialog.razor** — lists `DeleteService.Batches()`; each row's Restore button calls
  `RestoreAsync(batchId)`, then re-scans the folder (`AppState.ScanAsync` on
  `ScannedFolder`) so restored files reappear (same reconciliation the JS version does), then
  refreshes the list.

- **PreviewModal.razor** — `[Parameter] ImageView? Img`. Shows `<img src="@Img.FullUrl">` and a
  metadata line (dims, size, NSFW, blur, EXIF date/camera, dupe group). Esc / backdrop click
  closes (callback to `Home`).

---

## 6. `Program.cs` changes (instructions)

1. Add `builder.Services.AddRazorComponents().AddInteractiveServerComponents();`
2. Add `builder.Services.AddScoped<AppState>();` (keep `CatalogService` singleton + platform DI).
3. After `var app = builder.Build();`: `app.UseStaticFiles();` then `app.UseAntiforgery();`
   (Blazor Server requires the antiforgery middleware).
4. Keep the `/api/health` and `/api/thumb/{hash}` endpoints. Add `/api/full/{id}`: look up the
   image path by id via `ImageRepository`, verify it is a cataloged path that exists, infer
   content-type from extension, return `Results.File(path, contentType)`; else `NotFound`.
   (Guard: only serve paths present in the catalog — never an arbitrary path from the client.)
5. Replace the SPA fallback with
   `app.MapRazorComponents<SnapZap.App.Components.App>().AddInteractiveServerRenderMode();`
   (fully qualify `App` to avoid clashing with the `SnapZap.App` namespace).
6. Keep the browser auto-open block and `public partial class Program;` unchanged.
7. Remove all the deleted endpoints and the `JsonCamel` reference.

---

## 7. Migration steps (ordered, each independently verifiable)

Do these in order; build after each and keep the app runnable.

1. **Host swap (skeleton).** Rewrite `Program.cs` for Blazor; add `App/Routes/_Imports/
   MainLayout/Home` with Home showing a static "SnapZap" placeholder. Keep thumb + health.
   *Verify:* `dotnet run`, page loads, `blazor.web.js` connects (no console errors), health OK.
2. **AppState + load.** Add `AppState` and `ImageView`; `Home` calls `LoadAsync` on init and
   renders a plain count of images. *Verify:* after a manual scan (temporarily keep the old
   `/api/scan`, or seed the DB), the count shows. Then remove the temporary endpoint.
3. **Grid.** `PhotoGrid` + `Card` with `<Virtualize>`, badges, selection styling.
   *Verify:* thumbnails render; selecting toggles the border; scroll a large set stays smooth.
4. **Toolbar + operations.** Wire Scan/Dedup/NSFW to `AppState` with progress.
   *Verify:* scan a real folder end-to-end; status updates; grid populates; badges appear
   after dedup + NSFW.
5. **Sidebar filters + selection.** Bind filters; wire selection buttons.
   *Verify:* NSFW slider narrows the grid; "Duplicate extras" selects the non-keepers.
6. **Dialogs.** Export, Undo, Preview. *Verify:* full round-trip export (copy + hardlink),
   delete → undo → restore, preview shows full-res.
7. **CSS + cleanup.** Port `style.css` → `app.css` (+ Blazor's `#blazor-error-ui` rules);
   delete `index.html`, `app.js`, `style.css`, `ExportRequestDto.cs`, `JsonCamel.cs`.
   *Verify:* full browser regression + `dotnet test` (backend must still pass unchanged).

---

## 8. Mapping cheat-sheet (`app.js` → Blazor)

| `app.js` | Blazor |
|---|---|
| `state` object | `AppState` scoped service |
| `loadAll()` | `AppState.LoadAsync()` |
| `filteredImages()` / `matches()` | `AppState.Filtered()` / `Matches()` |
| `render()` (full innerHTML rebuild) | `<Virtualize>` + `StateHasChanged()` (diffed) |
| `onCardClick` / shift-range | `Card` `@onclick` with `MouseEventArgs.ShiftKey` → `SelectRange` |
| `selectDupeExtras()` etc. | same-named `AppState` methods |
| SSE `runSse` / `fetchSse` | `IProgress<T>` → `InvokeAsync(StateHasChanged)` |
| `/api/*` JSON fetches | direct Core service calls (no HTTP) |
| `/api/thumb/{hash}` | unchanged (still an `<img src>`) |
| modal `openModal` | `PreviewModal` component + `FullUrl` |
| `localStorage` folder/dest | optional JS-interop, or defer |

---

## 9. Risks & gotchas

- **Antiforgery middleware is required** for interactive Server components — forgetting
  `app.UseAntiforgery()` yields a runtime error on the first interaction.
- **`Progress<T>` marshalling:** raise UI updates through `InvokeAsync(StateHasChanged)` — the
  progress callback may run off the circuit's synchronization context.
- **Long operations block the circuit's render loop if run synchronously.** Keep
  scan/score/export `await`-ed (they already are async) so the UI stays responsive; the
  existing services offload CPU work with `Task.Run` internally.
- **`<Virtualize>` needs a stable item height** (`ItemSize`) to size the scrollbar; tune it to
  the card's rendered height or the scroll feel will be off.
- **Dispose subscriptions:** every component that does `AppState.Changed += StateHasChanged`
  must unsubscribe in `Dispose` or circuits leak handlers.
- **`AppState` is scoped, `CatalogService` is singleton.** That's correct: the DB/catalog is
  process-wide; the view state is per-circuit. Don't make `AppState` a singleton.
- **Full-res endpoint is an arbitrary-file-read risk** if it serves any path. Only serve paths
  that exist in the `images` table (look up by id), never a path supplied by the client.
- **Publish size / static assets:** Blazor Server ships `blazor.web.js` from the framework; no
  WASM payload. The self-contained publish command in `README.md` is unchanged.

---

## 10. Verification checklist (definition of done)

- [ ] `dotnet build` clean; `dotnet test` — all backend tests still pass (unchanged).
- [ ] Scan a real folder → thumbnails, blur, EXIF populate; rescan is fast (cache intact).
- [ ] Dedup → keep/dup badges; NSFW → score badges; both via direct service calls.
- [ ] Filters (NSFW/blur/date/folder/dupes-only) narrow the grid; selection persists across
      filtering.
- [ ] Selection: click, shift-range, all-visible, folder, duplicate-extras, clear.
- [ ] Export: copy + move + hardlink; pre-flight shows counts/size/free/hardlink; manifest
      written to app-data; safety invariants intact.
- [ ] Delete → Recycle Bin; Undo panel → restore returns files + grid reconciles.
- [ ] Preview shows full-resolution image.
- [ ] `<Virtualize>`: a 10k+ image library scrolls smoothly (only visible rows in the DOM).
- [ ] Old SPA files and DTO/JsonCamel removed; no dead `/api/*` endpoints remain.

---

## 11. Rollback

The vanilla SPA is a known-good fallback. Do the port on a branch; if Blazor Server proves
unsuitable (it won't for a localhost single-user app, but still), `git checkout` the branch
away — Core and tests were never touched, so the backend is unaffected either way.
