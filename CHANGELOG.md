# Changelog

All notable changes to SnapZap are recorded here, newest first. The desktop version lives in one
place — `<Version>` in [`src/SnapZap.App/SnapZap.App.csproj`](src/SnapZap.App/SnapZap.App.csproj) —
see [CLAUDE.md](CLAUDE.md)'s Gotchas for why that's the only place to bump it.

Since 1.3.0 the Android head carries its own pair, because the platform requires both a
`versionName` and a monotonic integer `versionCode`: `ApplicationDisplayVersion` (keep equal to
`<Version>`) and `ApplicationVersion` in
[`src/SnapZap.Android/SnapZap.Android.csproj`](src/SnapZap.Android/SnapZap.Android.csproj).

`versionCode` is a build counter rather than a second version number, so it advances independently
of the sections below — any APK that has to install over an earlier one needs a higher code, and it
can never go backwards. 1.3.0 exists as code 2 and, after the UX-review work, as code 3.

## 1.3.0 — 2026-07-29

The Android port, and a QA round that found real defects in the desktop app too — followed by a
senior UX review of the Android app ([docs/ANDROID-UX-REVIEW.md](docs/ANDROID-UX-REVIEW.md)) whose
nine findings are closed here in the same release.

### Added — Android

SnapZap now runs on Android as a **native app over the same `SnapZap.Core`** — no WebView, no
embedded server. Scan a folder, find duplicates, review them one-handed, delete reversibly, and
restore from history, all on the phone.

- **Eleven of the twelve screens** from the mobile design handoff: first run, scanning, library,
  plan, the duplicate-review queue, burst review, filters, folders, selection mode, photo preview
  and history. Export (screen 11) is deliberately out of this release.
- **Duplicate review as a one-thumb queue** with the keeper pre-picked and its reason stated —
  the thing a phone genuinely does better than the desktop's three-up comparison. Burst groups
  arrive last and never pre-pick anything.
- **Progress and background safety.** Scanning a real library is minutes of decoding and hashing;
  Android freezes or kills a backgrounded process doing that. Scans, duplicate detection, deletes
  and restores now run under a foreground service with a progress notification, so switching apps
  or letting the screen sleep no longer stops the work.
- **Trash and history** with a size read-out and an empty action. Android has no OS-level trash for
  an app-private folder, so unlike Windows and macOS the app has to provide what Explorer and
  Finder provide elsewhere.
- **Launcher icon**, generated from the same source art as the desktop favicon.
- **Swipe gestures are animated.** The review card follows your finger, rotates as it goes, and a
  stamp names the action before you let go — keep, previous, or move to trash. Past the threshold it
  throws; short of it, it springs back. The three directions are deliberately *not* animated alike:
  keep behaves like a card being dealt away, going back is tethered and unrotated because it decides
  nothing, and the trash gesture sits behind a much longer pull than the other two.
- **An undo bar after a swipe-delete**, with Restore one tap away instead of two screens away.
- **Stop, on the scanning screen.** The screen has always said stopping keeps what it has analysed;
  now it can actually be stopped, and the same button ends the duplicate pass that follows.
- **A sort control** on the library — scan order, newest, oldest, name, largest.
- **Hold-and-drag selects a range**, as the design always said it would.
- **A settings screen** with the same controls as the desktop's Setup panel — variant detection,
  rotation matching, how different two photos may look, and the burst window — plus what SnapZap is
  using in storage. Changing a threshold flags that detection has not run with it yet and offers to
  re-run it on the spot, because these settings decide what detection *finds* rather than how
  existing results are judged.
- **Screen-reader labels throughout.** Every icon control, photo tile, list row and plan step now
  announces itself; the app previously had none at all.
- **Delete from the preview.** The full-size view could show you a photo you had clearly finished
  with and offer nothing to do about it. Deleting steps to the next photo rather than closing, and
  puts an undo bar within reach of the tap that caused it.
- **Delete a group's extras from the comparison itself**, and move to the next one. Every button on
  that screen used to only *mark* a keeper — actually reclaiming the space meant leaving for the
  library, entering selection mode and finding the same photos again. The button names the count and
  the bytes it will free, and excludes burst-adjacent frames through the same shared rule the
  library's Select-all uses.
- **Forget everything**, matching the desktop's Setup panel. Android had no way to clear the
  catalogue at all — app-private storage cannot be reached to delete `catalog.db` by hand, so the
  only reset was uninstalling. Photos are never touched; the confirmation says so, and warns that
  History entries lose their previews because those *are* the thumbnail cache.

The port required no change to the scanning, hashing, duplicate-detection or NSFW logic. Verified
on-device: perceptual hashes are **bit-identical** to the desktop's, and NSFW scores match to
4e-11 — so a library deduplicated on one behaves the same on the other.

### Changed — Android

- **A first scan no longer defaults to the entire phone.** It starts at `DCIM/Camera`, with one-tap
  chips for the other places photos live and "Everything on this phone" still there as a choice.
- **The scan folder survives a relaunch**, read back from the catalogue like the desktop has always
  done — the Plan tab used to print a default folder above a count of photos from somewhere else.
- **The duplicates callout can be dismissed**, and a filter that matches nothing now says so and
  offers a way back instead of showing an empty grid.
- **The plan reads "of 3", not "of 5".** Content review and export are marked as desktop-only rather
  than as steps that have not started — neither can ever be completed on Android, and the model
  content review needs cannot be fetched by an app with no network permission.
- **Selecting photos no longer scrolls the grid back to the top** on every tap.
- **The library now shows the folder you scanned, and refreshes when it changes.** Two defects
  together: every screen read the catalogue unscoped, so photos from folders scanned earlier stayed
  in the grid — and inside the reach of "Select all" → Delete — while nothing reloaded when returning
  from another screen, so changing the folder, clearing the catalogue or deleting a photo left the
  previous answer on display.
- **Restoring from History puts photos back in the library.** Deleting removes the catalogue row as
  well as moving the file, so restoring returned the file and left it invisible in the app until the
  next scan. All four restore paths now re-read the folder, as the desktop has always done.
- The review screen's footer no longer says "nothing is deleted here" on a screen where swiping down
  deletes.

### Fixed — safety

- **Burst frames could be selected for bulk deletion.** Two independent defects. A Variant group
  that is a strict superset of a Burst group survives reconciliation, and the per-photo group
  assignment let whichever group came last win — so a burst frame could report `Variant`, pass the
  bulk-selectable gate, and be swept into a delete. Both closed: assignment now uses
  `GroupReconciler`'s own precedence, and `images.burst_adjacent` protects frames that complete
  linkage legitimately excluded from a burst clique.
- **Burst protection was cleared across the whole catalogue** on every detection run, so scanning a
  second folder silently stripped the first folder's protection. Now scoped to the folder being
  detected, like every other write of its kind.
- **The duplicate-review "select extras" button overstated what it would select** — 17 where the
  panel said 11. The selection was always correct; only the label was wrong, on the control that
  arms a bulk delete.

### Fixed — Android

- Back is routed through `OnBackInvokedDispatcher`. Overriding `OnBackPressed` silently stops
  working at API 36, which had made choosing a folder do nothing and made Back in selection mode
  close the app.
- Adaptive launcher icon, so it fills its circle rather than being shrunk inside one.
- Restoring from history left the progress notification running if the restore failed.

### Changed — both heads

- **"Export" is now "Move to a folder".** Same action, clearer name — nothing here was ever an
  export in the sense of producing a different format. The Copy/Move/Hardlink mode picker is gone
  with it: there is one action, and the only real choice left — whether the originals survive it —
  is now a confirmation asked next to the button that performs it rather than a radio three fields
  above. Both answers are explicit buttons, because "keep" and "remove" are two intentions, not a
  safe default and a variant of it. Hardlink stays in the engine (it has its own Windows
  verification) but is no longer something to pick from a list.
- **Burst grouping can now be switched off**, in Setup on the desktop and Settings on Android. It
  stays **on by default**, and both screens say what turning it off means, because it is not the
  harmless-sounding toggle it looks like: burst frames do not become ungrouped, they get picked up as
  "the same shot" instead, which *is* bulk-selectable. So all but one frame of every burst becomes
  available to a bulk delete. Reversible without re-scanning — switch it back on and find duplicates
  again. The Help FAQ's "will my bursts be deleted?" answer no longer says an unconditional no.

### Fixed — desktop

- **Every photo in a delete batch can now be restored on its own.** Per-photo Restore already
  existed in History, but only the first eight photos of a batch were ever drawn — and the button
  lives on a thumbnail, so anything past the eighth could only be restored by bringing the whole
  batch back. The "+N" counter is now a control that reveals more of the batch a page at a time.

### Changed — desktop

- The folder picker offers Pictures and Downloads as quick-jumps beside the drive/home roots.
- `PC_APPDATA` points the app at a different catalogue directory, so the running app can be driven
  against a throwaway library instead of the real one.

### Fixed — desktop

- The full-size compare view shows each copy's folder — the one fact it was asking you to decide on
  when two copies are byte-identical.
- The filename in that view no longer collapses to a few characters.
- The remove-from-history confirmation no longer overflows its dialog, which had pushed Cancel and
  the warning text out of view on an irreversible action.
- Two hardcoded English strings no longer leak into the Spanish UI.
- Delete a photo directly from the duplicate comparison, and remove a batch from history.

## 1.2.0 — 2026-07-28

### New
- **Browse for folders instead of typing paths.** Every place SnapZap asks for a folder — the
  folder to scan, an export destination, where to write extracted photos — now has a "Browse…"
  button that opens an in-app folder picker, instead of a blank text field.
- **Rescan a single folder.** Each folder in the tree has its own rescan action, so re-checking one
  folder after adding a few photos to it no longer means re-scanning the whole library.
- **Zoom in the photo preview.** A Fit / 100% / 200% / 400% toolbar (plus `+`/`-`/`0` keyboard
  shortcuts) — the same zoom the duplicate-compare view already had, now in the regular preview too.
- **Adjustable thumbnail size.** A Small / Medium / Large control next to Sort changes how many
  photos fit per row in the grid.
- **Thumbnails in the History (undo) dialog.** Each batch now shows a preview strip of the photos
  it contains, so you can see what you're restoring before clicking Restore. Hovering a thumbnail
  reveals a restore button for that photo alone, for when you only want one back, not the whole batch.

### Improved
- **Recycling shows real progress.** Moving photos to the Recycle Bin now shows a live counter and
  progress bar instead of a frozen-looking status line, and Stop actually works mid-recycle.
- **Scoring explicit content jumps straight to the results.** When a scoring pass finishes, the
  Filters panel opens on its own so the flagged/not-sure photos are the next thing you see.
- **"Include subfolders" moved next to the folder picker itself** (the Folders tab), instead of a
  separate trip to the Filters tab to find it.

### Fixed
- **Dark mode:** the photo preview's navigation, close, details and select buttons were nearly
  invisible — dark icons on the dark theme's near-black background.

## 1.1.0 — 2026-07-27

### New
- **Hide Photos in Image.** Conceal selected photos inside a carrier image of your choice —
  optionally passphrase-encrypted — and a matching Extract flow to get them back out. The carrier
  still opens normally as an ordinary image.
- **A real Windows installer**, with the app icon and version info a downloaded .exe is expected to
  have, plus a **macOS installer (.pkg)** offering the same optional NSFW-model download.
- **"Modernist" redesign**, rebuilt around the app's own wolf-mark artwork, with a light/dark theme
  toggle.
- **Configurable explicit-content sensitivity** (Cautious / Balanced / Eager, with per-threshold
  overrides), and **tiled scoring**: photos are also checked in nine overlapping regions so a
  subject occupying only part of a wide photo isn't missed by a single whole-frame resize — without
  flagging ordinary portraits in the process.
- **Duplicate detection now runs automatically right after every scan** — no second click needed.

### Improved
- **Scan can actually be stopped mid-run**, and every long-running step (scan, dedup, scoring,
  export) shows a live "N of M · ~time left" estimate.
- **Explicit-content scoring respects what you're looking at** — your current selection, or the
  folder you have open — instead of always re-scoring the entire library.
- **A duplicate group can keep more than one copy**, instead of forcing exactly one keeper.

### Fixed
- **The published app could render a blank, unstyled page** if launched from anywhere other than
  its own folder, or if the `.exe` was moved away from the files beside it — it now always finds
  its own assets, and ships as an installer rather than a single copy-anywhere `.exe`.

## 1.0.0 — 2026-07-26

The first release: SnapZap as a working duplicate/explicit-content/blurry-photo cleaner.

- **Scan a folder** and re-scan it near-instantly afterward (unchanged photos are cached, not
  re-analyzed), browsed as a real **folder tree** scoped to what you actually scanned.
- **Duplicate detection in three kinds** — Exact, Variant (resized or re-encoded), and Burst (the
  same scene, seconds apart) — with Burst always protected from bulk deletion, and a **side-by-side
  review flow** to pick which copy of each group to keep.
- **Explicit-content scoring** with a plain-language verdict (Likely explicit / Not sure / Looks
  clean / Not checked), not a bare decimal.
- **Export** to a clean folder — copy, move, or hardlink — with hash verification and a manifest.
- **Delete always goes to the Recycle Bin**, never a hard delete, with one-click Undo and a History
  panel of past batches.
- Your **selection persists** across page reloads and app restarts; **space savings** (how much a
  cleanup would actually reclaim) are shown up front, not just a count.
- **Full-size preview** with keyboard navigation, and a full-size **compare view** for judging
  near-duplicates thumbnails can't settle.
- In-app **Help/FAQ**.
