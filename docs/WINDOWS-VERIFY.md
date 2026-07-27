# Windows verification checklist

This app is developed on macOS and cross-compiled to `win-x64`. Cross-compiling proves the
Windows-only code *compiles*, not that it *runs correctly*. The four platform-specific paths
below use Win32 P/Invoke or Shell COM that could not be exercised on the dev machine. Run
through this on real Windows hardware before shipping a release.

## 1. Recycle Bin — `WindowsTrashService.SendToTrashAsync`
`shell32.SHFileOperation(FO_DELETE, FOF_ALLOWUNDO)`.
- [x] Delete a few images in the app's Delete mode.
- [x] Confirm they land in the Windows Recycle Bin (not permanently deleted).
- [x] Confirm the source folder no longer shows them and the grid updates.

## 2. Restore — `WindowsTrashService.RestoreAsync`
Late-bound `Shell.Application` → Recycle Bin item → `Restore` verb.
- [x] Open the Undo panel, click Restore on a delete batch.
- [x] Confirm files return to their **exact original paths**.
- [x] Confirm the app re-scans and the images reappear in the grid.
- [ ] Edge cases: a file whose original folder was since deleted; two deleted files with the
      same name from different folders.

## 3. Hardlinks — `WindowsLinkService`
`kernel32.CreateHardLinkW`; same-volume check via drive-letter comparison.
- [ ] Export with **Hardlink** mode to a destination on the **same** drive as the source.
- [ ] Confirm pre-flight reports "hardlink available (zero extra space)".
- [ ] Confirm destination files exist and share data with the source (`fsutil hardlink list`).
- [ ] Export with Hardlink mode to a **different** drive → confirm it silently falls back to
      Copy (pre-flight should not offer hardlink).

## 4. Native window host / GPU
- [x] **WebView2 native window** (Photino) instead of a browser tab — `IAppHost`. Verified on
      Windows 11: window opens, styled UI renders, closing it stops the server and exits.
- [ ] **DirectML GPU** acceleration for NSFW inference — swap
      `Microsoft.ML.OnnxRuntime` → `Microsoft.ML.OnnxRuntime.DirectML` in the Windows publish
      and wire `IInferenceProvider`. CPU inference works today; this is a speed upgrade.

## General smoke test on Windows
- [x] Double-click `SnapZap.App.exe` → the app opens in its own window, no console window visible.
- [x] Assets serve (`app.css`, `interop.js`, `_framework/blazor.web.js`) and the circuit connects.
- [x] Launch with a working directory *other* than the app's own folder → still styled and live.
- [x] Two copies at once → the second gets its own port instead of an address-in-use crash.
- [x] Scan a real folder of JPG/PNG → thumbnails, blur, EXIF populate.
- [ ] Place `models/nsfw.onnx` beside the exe → NSFW scoring works and scores look sane.
- [ ] Full round-trip: scan → dedup → select duplicate extras → export (copy) → verify manifest.

## 5. Installer
- [x] Silent install → per-user, no UAC, Start Menu entry, Apps & features registration.
- [x] Installed copy launches and renders.
- [x] Uninstall removes the program folder and shortcuts.
- [ ] Interactive install with the **Explicit-content scoring model** component ticked →
      328 MB download, checksum verified, `models/` populated, **Score NSFW** enabled.
- [ ] Interactive uninstall → the "also delete the catalogue?" prompt appears and defaults to
      keeping it. (A *silent* uninstall must never delete it — see the note in `SnapZap.iss`.)
- [ ] Install over an existing version → upgrades in place, one entry in Apps & features.

## 6. Known-failing on Windows (pre-existing, unrelated to packaging)
`dotnet test` on Windows fails four tests that pass on macOS. Three are the Windows platform
implementations this document exists to verify, so they are evidence, not noise:
- `ExportTests.Hardlink_export_creates_zero_copy_links_on_same_volume` — `ILinkService`
- `ExportTests.Reject_recycle_happens_only_after_keepers_verified` — `ITrashService`
- `ScannerTests.Exact_duplicates_do_not_fail_on_concurrent_thumbnail_write` — 7 of 21 rows end
  up with an empty `ThumbPath`; fails 5/5 in isolation
- `SelectionCommandTests.Burst_only_extras_are_distinguishable_from_having_no_extras_at_all`
