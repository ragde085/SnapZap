# Windows verification checklist

This app is developed on macOS and cross-compiled to `win-x64`. Cross-compiling proves the
Windows-only code *compiles*, not that it *runs correctly*. The four platform-specific paths
below use Win32 P/Invoke or Shell COM that could not be exercised on the dev machine. Run
through this on real Windows hardware before shipping a release.

## 1. Recycle Bin — `WindowsTrashService.SendToTrashAsync`
`shell32.SHFileOperation(FO_DELETE, FOF_ALLOWUNDO)`.
- [ ] Delete a few images in the app's Delete mode.
- [ ] Confirm they land in the Windows Recycle Bin (not permanently deleted).
- [ ] Confirm the source folder no longer shows them and the grid updates.

## 2. Restore — `WindowsTrashService.RestoreAsync`
Late-bound `Shell.Application` → Recycle Bin item → `Restore` verb.
- [ ] Open the Undo panel, click Restore on a delete batch.
- [ ] Confirm files return to their **exact original paths**.
- [ ] Confirm the app re-scans and the images reappear in the grid.
- [ ] Edge cases: a file whose original folder was since deleted; two deleted files with the
      same name from different folders.

## 3. Hardlinks — `WindowsLinkService`
`kernel32.CreateHardLinkW`; same-volume check via drive-letter comparison.
- [ ] Export with **Hardlink** mode to a destination on the **same** drive as the source.
- [ ] Confirm pre-flight reports "hardlink available (zero extra space)".
- [ ] Confirm destination files exist and share data with the source (`fsutil hardlink list`).
- [ ] Export with Hardlink mode to a **different** drive → confirm it silently falls back to
      Copy (pre-flight should not offer hardlink).

## 4. Native window host / GPU (not yet implemented)
Currently the `.exe` starts the local server and opens the default browser (the chosen
double-clickable UX). Two optional upgrades remain, both Windows-runtime-only:
- [ ] **WebView2 native window** (Photino) instead of a browser tab — `IAppHost`.
- [ ] **DirectML GPU** acceleration for NSFW inference — swap
      `Microsoft.ML.OnnxRuntime` → `Microsoft.ML.OnnxRuntime.DirectML` in the Windows publish
      and wire `IInferenceProvider`. CPU inference works today; this is a speed upgrade.

## General smoke test on Windows
- [ ] Double-click `SnapZap.App.exe` → server starts, browser opens the SPA.
- [ ] Scan a real folder of JPG/PNG → thumbnails, blur, EXIF populate.
- [ ] Place `models/nsfw.onnx` beside the exe → NSFW scoring works and scores look sane.
- [ ] Place `czkawka_cli.exe` beside the exe → similar-image groups appear.
- [ ] Full round-trip: scan → dedup → select duplicate extras → export (copy) → verify manifest.
