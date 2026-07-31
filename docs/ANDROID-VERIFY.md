# Android verification checklist

Mirrors [WINDOWS-VERIFY.md](WINDOWS-VERIFY.md)'s role: this app is developed on macOS, and while
the Android toolchain is now partly in place (SDK at `~/Library/Android/sdk`, working `adb`, .NET
Android packs — see [ANDROID-PORT-ACS.md](ANDROID-PORT-ACS.md) §1.1), there is still **no physical
Android hardware attached** and no Android build of SnapZap has ever run. Every criterion below is gated 🔴 **DEVICE** in
the acceptance-criteria document — it cannot be faked by an emulator, code inspection, or
`dotnet test`, because the entire reason two physical phones are in scope is OEM WebView and
permission-flow variance that an emulator doesn't reproduce. Run through this once both devices
(Samsung Galaxy S23 Ultra, mid-range Motorola) and a working `net10.0-android` build exist.

**19 acceptance criteria are gated 🔴 DEVICE** as of this writing; all 19 are covered below,
grouped by the epic they belong to in `ANDROID-PORT-ACS.md`. Nothing here can be checked off
today — every row starts unverified. Fill in the two device columns as each is actually run on
that hardware; don't mark a row done from reasoning about the code, the same rule this document's
Windows counterpart already follows for its own unchecked boxes.

**Convention:** each row's device columns hold one of `☐ Not yet run`, `✅ Pass`, or `❌ Fail —
see notes`. Leave `☐ Not yet run` untouched rather than guessing at an outcome.

---

## 0. How to capture evidence

Don't rely on memory for a 🔴 DEVICE result — every fail (and every pass on a criterion with any
subtlety, e.g. AC-2.3's hash comparison) needs something recorded, not just a checked box.

- **Screenshots:** on-device power+volume-down capture, or `adb exec-out screencap -p >
  evidence/<device>-<ac-id>.png`. Name files `s23-AC-6.5.png` / `moto-AC-4.2.png` so they sort
  next to the AC they support.
- **Logcat, scoped to this app only** (the full system log is unusable noise otherwise):
  ```
  adb logcat --pid=$(adb shell pidof -s com.snapzap.android)
  ```
  or, before the process exists yet (e.g. capturing a crash on launch), filter by tag instead:
  ```
  adb logcat -s SnapZap:V AndroidRuntime:E ActivityManager:I
  ```
- **Save a session's log to a file** rather than eyeballing a scrolling terminal, especially for
  AC-6.5 and AC-4.4 where the interesting moment might be brief:
  ```
  adb logcat --pid=$(adb shell pidof -s com.snapzap.android) > evidence/<device>-<ac-id>.log
  ```
- **SignalR/WebSocket transport** (AC-0.4): don't infer from "the click worked" — the browser
  DevTools protocol isn't available on a bare `Android.Webkit.WebView`, so confirm from the
  server-side log line ASP.NET Core's SignalR emits on transport negotiation
  (`Microsoft.AspNetCore.SignalR` category at `Information` level reports the selected transport;
  make sure `Program.cs`'s logging isn't filtered below that before relying on it), captured via
  the same `adb logcat --pid=...` above since the Kestrel process is in-process with the app.
- **Perceptual hash / blur score comparison** (AC-2.3): log the actual computed values on-device
  (a temporary `Log.LogInformation` around `PerceptualHash.FromGray`/`BlurDetector.ScoreFrom` in
  the spike project is fine — this doesn't need to survive into the shipped app) and paste them
  into this document's notes column, not just "matched" — a future regression needs the actual
  numbers to diff against.
- **File everything under a per-run folder** (e.g. `evidence/2026-08-12/`) so a re-run after a fix
  doesn't overwrite the record of what failed the first time.

---

## 1. E0 — architectural spike

The go/no-go gate everything else sits on (`ANDROID-PORT-PLAN.md` §0). If AC-0.3 or AC-0.4 fails
on either device, stop and follow AC-0.6 rather than continuing down this checklist.

| AC | What to do | Pass looks like | S23 Ultra | Motorola | Notes |
|---|---|---|---|---|---|
| **AC-0.3** | Load a plain `Android.Webkit.WebView`'s `LoadUrl` against the loopback address the AC-0.2 spike bound (`http://127.0.0.1:<port>`), with `INTERNET` permission and the `127.0.0.1`-scoped cleartext network-security config (§E4) already in place. | The response body renders in the WebView — not a blank white screen. | ☐ Not yet run | ☐ Not yet run | A blank screen here without the cleartext config in place is a config problem, not an architecture failure — don't attempt this AC until AC-4.1's manifest/network-security-config work has landed, per the AC's own wording. |
| **AC-0.4** | Point the WebView at a real `SnapZap.App` page (not the AC-0.3 hello route) and confirm the Blazor circuit connects, using the **WebSocket** transport specifically, then click one interactive button and confirm it round-trips. | Transport is confirmed **from the server log** (see §0 above), not inferred from the click working — a working click over long-polling would still "look" fine but silently cost the app SignalR's low-latency assumptions everywhere (e.g. live scan progress). | ☐ Not yet run | ☐ Not yet run | If the transport is long-polling rather than WebSocket, that's a fail even if the click works — record which transport was actually negotiated. |
| **AC-0.6** | Go/no-go decision, recorded in writing regardless of outcome. | If AC-0.2 or AC-0.4 failed outright (not "needs a config tweak" — an actual dead end), the fallback re-scope to `BlazorWebView` (`ANDROID-PORT-PLAN.md` §0, closing paragraphs) is written up here before any further Android-specific code lands. If both passed, record that explicitly too — "passed silently" is exactly what this AC exists to prevent. | ☐ Not yet run | ☐ Not yet run | This AC's "device" component is really "were AC-0.3/0.4 exercised on both devices before this decision was made" — don't let one device's pass stand in for both. |

## 2. E2 — native dependency viability

Higher severity than the plan's own risk table credited (`ANDROID-PORT-ACS.md` §1.2) — SkiaSharp
underlies scan, thumbnail, blur, and perceptual hash, so a failure here fails later and more
broadly than the ONNX risk the plan does call out.

| AC | What to do | Pass looks like | S23 Ultra | Motorola | Notes |
|---|---|---|---|---|---|
| **AC-2.3** | **SkiaSharp spike.** Decode a real JPEG from `/storage/emulated/0`, produce a thumbnail, compute a Laplacian blur score, and compute a 544-bit perceptual hash — see the dedicated procedure in §3 below. | Decode/thumbnail/blur all complete without a native-load exception, **and** the phash matches the value the same file produces on the dev Mac, per `GoldenValueTests`. A platform-dependent hash is a silent dedup break, not a visible crash — the numeric comparison is the actual pass/fail signal, not "it ran." | ☐ Not yet run | ☐ Not yet run | **v1 blocker if this fails** (`ANDROID-PORT-ACS.md` §2.6) — unlike ONNX, there is no graceful-degrade path for a broken decode/hash pipeline. |
| **AC-2.4** | **ONNX spike.** Construct `OnnxNsfwClassifier` against the real `nsfw.onnx` and score a known labeled fixture. | The score matches the desktop score for the same fixture within a stated tolerance (state the tolerance used in the notes column, not just "close"). | ☐ Not yet run | ☐ Not yet run | Not a v1 blocker if it fails — NSFW scoring degrades gracefully like a missing model does on desktop today (`DependencyChecker`'s existing "optional sidecar" surface). Record the failure mode anyway (native load exception vs. wrong score vs. timeout) so it's clear which. |
| **AC-2.5** | **SQLite spike.** Open `catalog.db`, create the schema, round-trip one row, then **fully restart the app** (not just background/resume) and confirm the row is still there. | Schema creation succeeds, the row reads back with the same values written, and it survives the full process restart. | ☐ Not yet run | ☐ Not yet run | **v1 blocker if this fails** (§2.6) — SQLite is as foundational as SkiaSharp here; `SQLitePCLRaw.bundle_e_sqlite3` shipping Android runtimes is not the same claim as those runtimes loading under this TFM, which is exactly what this AC confirms. |

## 3. AC-2.3 in detail — the perceptual-hash parity procedure

This is singled out because "the hash must match the dev-Mac value" is easy to under-specify into
"looks about right." `GoldenValueTests` (in `tests/SnapZap.Tests`) is the reference — it pins
known hash values for known fixture images specifically so a platform-dependent hash change would
be caught. Reuse the same fixtures on-device rather than a fresh photo, so the comparison is
apples-to-apples:

1. On the dev Mac, identify the exact fixture file(s) `GoldenValueTests` asserts against, and the
   pinned hash constant(s) it expects — read the test source rather than re-deriving from a run,
   since the point is comparing against the *pinned* value, not a value freshly computed on the
   Mac today (which begs the question if the Mac's own output ever drifted).
2. Copy the identical fixture file (byte-for-byte — verify with a checksum after transfer, not
   just "same filename") onto each device's `/storage/emulated/0`.
3. In the AC-2.3 spike project, decode the file with `SkiaImageService`/`DecodeGray`, run
   `PerceptualHash.FromGray` and `BlurDetector.ScoreFrom` exactly as `Scanner.Analyze` does (same
   call sequence — per CLAUDE.md, phash and blur ride the same decode on purpose; don't decode
   twice in the spike either, or a device-specific double-decode bug wouldn't be caught by the
   desktop comparison), and log the resulting 544-bit hash (all four rotations, per
   `images.phash`'s stored format) and the blur score.
4. Compare the logged on-device hash against the pinned `GoldenValueTests` value bit-for-bit, not
   "close enough" — a phash is meaningful only as an exact match or a small Hamming distance
   against a *known-good* comparison point; drift here silently breaks dedup rather than crashing.
5. Record the actual hash bytes (or their hex encoding) in this document's evidence, not just
   "matched"/"didn't match" — a future regression needs the real values to diff against.
6. Repeat independently on both devices — a match on one device and a silent mismatch on the other
   is exactly the OEM-variance scenario the two-device requirement exists to catch, and nothing
   about SkiaSharp's Android native bindings guarantees both OEMs' CPUs/builds behave identically.

## 4. E4 — storage and permissions

`MANAGE_EXTERNAL_STORAGE`'s grant UX is explicitly called out (`ANDROID-PORT-PLAN.md` §5 risk
table) as differing between Samsung's One UI and the Motorola's closer-to-stock build — this is
the epic where the two-device requirement is least optional.

| AC | What to do | Pass looks like | S23 Ultra | Motorola | Notes |
|---|---|---|---|---|---|
| **AC-4.2** | Exercise the `MANAGE_EXTERNAL_STORAGE` ("All files access") grant flow end-to-end from the gating screen (AC-4.3), via `Settings.ActionManageAppAllFilesAccessPermission`, on **both** devices — see the OEM-specific notes below. | Both devices reach a granted state, and the app confirms it (e.g. re-checks the permission and dismisses the gating screen) without a restart being required. | ☐ Not yet run | ☐ Not yet run | **Record the actual screen sequence for each device** — number of taps, exact wording of each screen, whether it's a system Settings page or an in-line toggle — not just pass/fail, since the ACS's whole point in calling this out is that the sequences differ and future maintainers need to know how. |
| **AC-4.4** | With the permission already granted and a scan in progress or the grid populated, background the app, revoke "All files access" from system Settings (Settings → Apps → SnapZap → Permissions), then resume the app. | The app returns to the gating screen (AC-4.3) on resume rather than throwing `UnauthorizedAccessException` mid-scan or showing a silently-empty grid. | ☐ Not yet run | ☐ Not yet run | Try revoking at two different moments — idle (nothing running) and mid-scan (a `RunAsync` operation in flight) — since the failure mode for revocation mid-I/O (an actual thrown exception from a `File.*` call) is different from revocation while idle (nothing fails until the next access attempt). Note which moment was tested. |
| **AC-4.5** | Scan a folder that is **not** indexed by the system Photos library / `MediaStore` — e.g. a folder created via a file manager and populated by `adb push`, never opened in the Gallery app. | The folder scans end-to-end (enumerate, hash, thumbnail, dedup) exactly as a Photos-indexed folder would. | ☐ Not yet run | ☐ Not yet run | This is the entire reason `MediaStore` was ruled out in the plan (§3) — it must be proven, not assumed from the permission being granted. A pass on a Photos-indexed folder does **not** substitute for this AC; use a folder deliberately kept outside any indexed path. |

## 5. E5 — Android-specific paths

| AC | What to do | Pass looks like | S23 Ultra | Motorola | Notes |
|---|---|---|---|---|---|
| **AC-5.3** | Open the (ported) `DirectoryPickerDialog` on-device. Confirm it opens at `/storage/emulated/0`, lists real subfolders, navigates into a subfolder and back out, and — in a folder you don't have read access to, if one can be arranged — shows an inline error rather than an empty list. | Listing, navigation, and the permission-denied case (inline message, not a silently empty list) all behave as the macOS/Windows picker already does per `docs/UI-FEATURES.md` §4. | ☐ Not yet run | ☐ Not yet run | The permission-denied case may be hard to arrange under `MANAGE_EXTERNAL_STORAGE` (which is intentionally broad) — if no denial case is reachable on-device, note that explicitly rather than silently skipping the check. |
| **AC-5.4** | Log `CatalogService`'s resolved `AppDataDir` on first run, confirm (by path, and by checking it isn't visible to a file manager / doesn't require the all-files permission to reach) that it's app-private internal storage, then fully kill the app (not background) and relaunch. | The logged path is app-private internal storage (not shared/external), and `catalog.db` plus thumbnails are still present and load correctly after the kill+relaunch. | ☐ Not yet run | ☐ Not yet run | Pairs with AC-0.5 (`LocalApplicationData`/`UserProfile` resolution logged during the spike) — if that earlier log already answered this, cite it here rather than re-deriving; if the spike's answer and this AC's on-device answer disagree, that's itself a finding. |

## 6. E6 — touch pass, device-only portion

See [ANDROID-TOUCH-AUDIT.md](ANDROID-TOUCH-AUDIT.md) for the audit and code-level fix (AC-6.1-6.3).
This section covers only what that audit could not: real hardware, real touch input.

| AC | What to do | Pass looks like | S23 Ultra | Motorola | Notes |
|---|---|---|---|---|---|
| **AC-6.4** | Measure the on-screen tap target size (not the visual icon size — the actual hit area) for every primary action: card selection, the topbar icon buttons, Select/Export/Delete/Hide in `SelectionBar`, the rail tabs, folder tree rows, and (once fixed per the touch audit) `.undo-thumb-restore`. | Every primary action's tap target is **≥48dp** on both devices. dp, not px — the same physical target can differ in dp across devices at different densities, which is exactly why both devices are checked independently rather than assuming one confirms the other. | ☐ Not yet run | ☐ Not yet run | List any control that measures under 48dp by name and measured size, don't just fail the row — a partial pass (11 of 12 controls compliant) is actionable, a bare ❌ is not. |
| **AC-6.5** | **The scroll-windowing regression check.** Full reproducible procedure in §7 below — do not substitute "scroll around and see how it feels." | The grid scrolls smoothly under real touch, in both directions, at ≥4,000 photos, with no runaway to either end of the scroll range. | ☐ Not yet run | ☐ Not yet run | This is the single highest-value device check in this whole document — see §7 for why, and for the exact steps. |

## 7. AC-6.5 in detail — the scroll-windowing regression procedure

**Why this gets special treatment.** `PhotoGrid.razor` doesn't use Blazor's `<Virtualize>` — per
CLAUDE.md, that component's viewport detection (an `IntersectionObserver` on its own spacers)
never resolves inside this app's flex/scroll layout, reporting zero capacity and rendering nothing.
`PhotoGrid` instead windows rows itself: `interop.js` measures scroll geometry and pushes it into
`SetViewport`/`SetScroll` (`PhotoGrid.razor:159-177`), and `.grid { overflow-anchor: none }`
(`app.css:720-724`) exists because the browser's scroll-anchoring behavior — adjusting `scrollTop`
to keep on-screen content visually still when off-screen content resizes — fed back into that
windowing math and ran the grid to one end on a **single wheel gesture** (measured on desktop: ten
wheel notches from 20,000px landed at 119,091px instead of ~21,200px). That failure mode
reproduces **only with real wheel/trackpad input** — a programmatic `scrollTop` assignment
suppresses anchoring entirely, so the bug is invisible from the browser console or any automated
test that sets scroll position directly rather than dispatching real input events.

Touch momentum scrolling (the deceleration/fling behavior after a swipe) is a genuinely different
input mode from both the wheel input that originally triggered this and any programmatic scroll —
it delivers a rapid, OS-generated sequence of scroll position updates that could excite the same
feedback loop through a different path, on a WebView build (Chrome-derived, but OEM-patched) that
has never run this code before. Two devices are required specifically because "does this OEM's
WebView's momentum-scroll implementation interact badly with this app's windowing math" is not
answerable from one data point.

**Procedure (run identically on both devices):**

1. Build a test library of **at least 4,000 photos** — real or synthetic images are both fine
   (the windowing math cares about row count, not photo content), but they must be enough distinct
   files that thumbnails are visibly generated per-row rather than served from a tiny cached set
   that could mask missing rows.
2. Scan that folder on-device and let it fully complete (Plan step 1 done, grid populated).
3. **Fling scroll test, downward:** from the top of the grid, perform 10 consecutive fast swipe
   gestures (finger down near the bottom of the grid, flick upward, release — the standard
   "scroll down" gesture) with a natural pause between each (enough for momentum to mostly decay,
   not back-to-back interrupts). After each swipe, note the approximate scroll position (row number
   or `scrollTop`, loggable via a temporary `console.log` in `interop.js`'s scroll handler if not
   otherwise visible). Expected: position advances roughly in proportion to the swipes; not a
   runaway to the very bottom or a snap-back to the top.
4. **Fling scroll test, upward:** from wherever step 3 ended, repeat with 10 downward flicks
   (scroll toward the top). Same expectation, opposite direction.
5. **Interrupt-during-momentum test:** perform a fast fling, and while the page is still visibly
   decelerating from momentum, touch down again (interrupting the fling) and immediately swipe the
   opposite direction. This is the input pattern most likely to excite a feedback loop, since it
   forces rapid `SetScroll` calls while a resize/re-render from the previous windowing update may
   still be settling. Expected: the grid tracks the new gesture; it does not accelerate away to
   either end.
6. **Mid-library random test:** scroll to roughly the middle of the library (not the very top or
   bottom, where a runaway might coincidentally look like "reached the end normally"), then repeat
   steps 3-5 from there.
7. For any run that shows a runaway (rapid, gesture-disproportionate jump toward either end) or a
   snap-back, capture: a screen recording (not just a screenshot — the defect is about motion over
   time) via `adb shell screenrecord`, the `interop.js` console log of `SetViewport`/`SetScroll`
   calls around the event, and the approximate row count scrolled per real swipe distance versus
   what was observed.
8. Record pass/fail **per device independently** — a pass on the S23 Ultra says nothing about the
   Motorola's WebView build, which is the entire premise for requiring both.

## 8. E7 — definition of done

Full end-to-end confirmation that the port actually works, not just that its individual pieces do.

| AC | What to do | Pass looks like | S23 Ultra | Motorola | Notes |
|---|---|---|---|---|---|
| **AC-7.1** | Install and launch the app fresh. | Runs without crashing on both devices. | ☐ Not yet run | ☐ Not yet run | |
| **AC-7.2** | Grant all-files access (AC-4.2) and scan a folder outside the system Photos library (AC-4.5). | Completes end-to-end, same as those two ACs individually — this row is the combined smoke test, not a new procedure. | ☐ Not yet run | ☐ Not yet run | If AC-4.2 and AC-4.5 both already passed individually, this is largely a re-confirmation that they compose correctly, not a fresh investigation. |
| **AC-7.3** | Scan the **same folder** already scanned on the dev Mac. Compare exact/variant/burst duplicate group membership, blur scores, and every filter's result count against the Mac's output for that folder. | Group membership matches exactly — divergence in which photos land in which group is a fail, not a rounding difference (per the AC's own wording; a few-bits difference in a threshold-adjacent phash comparison is the kind of thing that *would* show up as membership divergence, which is why AC-2.3's exact-match requirement upstream matters). | ☐ Not yet run | ☐ Not yet run | Use a folder small enough to diff by hand (dozens, not thousands, of photos) so a mismatch is actually traceable to a specific pair/group rather than lost in aggregate counts. |
| **AC-7.4** | Attempt NSFW scoring (with `nsfw.onnx` present on-device). | Either scoring works and produces sane-looking results, **or** it's absent and explained through the existing `DependencyChecker` "optional sidecar, gracefully degrades" surface — silently broken (button does nothing, or crashes) is the only fail state. | ☐ Not yet run | ☐ Not yet run | Ties back to AC-2.4 — if that spike already answered "works" or "gracefully absent," this is confirming the same answer holds in the full app, not a fresh unknown. |
| **AC-7.5** | Delete a batch of photos to trash, restore the whole batch; separately, delete another batch and restore just one photo from `UndoDialog`'s thumbnail strip (once AC-6.3's touch fix is in place, restoring one via touch specifically — this is also where a botched AC-6.3 fix would first surface as a functional bug rather than just a UI nit). | Both round-trip correctly: files return to their original paths, the grid and undo log reflect the restore accurately. | ☐ Not yet run | ☐ Not yet run | |
| **AC-7.6** | Restart the app fully (kill process, relaunch) after a scan. | Catalog DB and thumbnails persist — same check as AC-5.4, this row is the full-app confirmation rather than the isolated spike. | ☐ Not yet run | ☐ Not yet run | |

`AC-7.7` (no export/Hide/Extract/DirectML reachable from the Android UI) and `AC-7.9` (safety
invariants hold, restated for Android) are 🟡 SDK / 🟢 NOW respectively — verified by inspecting
the rendered component tree and the trash implementation, not by anything that needs a physical
device. Not included in this table; see `ANDROID-PORT-ACS.md` directly for those two.

---

## 9. Summary

| Epic | 🔴 DEVICE ACs covered here |
|---|---|
| E0 — spike | AC-0.3, AC-0.4, AC-0.6 |
| E2 — native deps | AC-2.3, AC-2.4, AC-2.5 |
| E4 — storage/permissions | AC-4.2, AC-4.4, AC-4.5 |
| E5 — Android paths | AC-5.3, AC-5.4 |
| E6 — touch | AC-6.4, AC-6.5 |
| E7 — definition of done | AC-7.1, AC-7.2, AC-7.3, AC-7.4, AC-7.5, AC-7.6 |
| **Total** | **19** |

This is every AC gated 🔴 DEVICE in `ANDROID-PORT-ACS.md` as of this writing. If a future revision
of that document adds, removes, or re-gates an AC, this table's count is the thing to reconcile
first — it should never silently drift out of sync with the source document.
