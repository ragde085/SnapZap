# Changelog

All notable changes to SnapZap are recorded here, newest first. The version number lives in one
place — `<Version>` in [`src/SnapZap.App/SnapZap.App.csproj`](src/SnapZap.App/SnapZap.App.csproj) —
see [CLAUDE.md](CLAUDE.md)'s Gotchas for why that's the only place to bump it.

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
