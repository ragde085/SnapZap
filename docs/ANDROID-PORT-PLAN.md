# Android port — implementation plan

> ## ⚠ Status: partly superseded — read this box first
>
> This plan was written in a sandbox with no .NET SDK, no Android SDK and no `adb`. It has since
> been executed far enough to settle its central question, and **its architecture was wrong**.
>
> | | |
> |---|---|
> | **§0's bet — Kestrel in-process + a WebView** | ⛔ **Impossible.** ASP.NET Core publishes no runtime pack for any mobile RID. Proven, not guessed — see [ANDROID-PORT-ACS.md](ANDROID-PORT-ACS.md) §1.5. |
> | **§0's fallback — BlazorWebView + a small Kestrel for `/api/thumb`** | ⛔ Also impossible: *no* Kestrel means not even a small one. |
> | **What replaced them** | A **native C# Android UI over `SnapZap.Core`** — no WebView, no HTTP, no Blazor on Android. Rationale in §0 below. |
> | **§1 scope, §3 storage model, §5 risks** | ✅ Still valid and still the right calls. |
> | **§2 platform services** | Partly valid — `IAppHost` is void, trash/link survive in better form. |
> | **§4 task list, §7 estimate** | Rewritten below. |
>
> Acceptance criteria, per-item status and all measured evidence live in
> [ANDROID-PORT-ACS.md](ANDROID-PORT-ACS.md); that document is now the source of truth for
> *whether* something works. This one says what to build next.

**Test devices:** Samsung Galaxy S23 Ultra and a mid-range Motorola. Neither has been used yet —
everything so far is an `arm64-v8a` emulator, which shares the handsets' ABI and therefore loads
the same native libraries.

---

## 0. The architecture — settled, the hard way

The original bet was that Android could be a fifth `IAppHost`: run the same `WebApplication`
in-process bound to loopback, point a system `WebView` at it, and reuse `Program.cs`, both API
endpoints, Blazor Server's render mode, `AppState` and every Razor component unmodified.

**It cannot work.** A real `net10.0-android36.0` head hits two errors:

1. `NETSDK1150` — an Android app is a self-contained executable and cannot reference
   `SnapZap.App`, a non-self-contained `Exe`. (`SnapZap.App` must stay `OutputType=Exe` *exactly*
   or Blazor's static web assets are silently dropped — CLAUDE.md gotcha 9.) There is a documented
   escape hatch, and it leads to:
2. `NETSDK1082` — *no runtime pack for `Microsoft.AspNetCore.App` for RID `android-arm64`.*

The second is not configuration. Verified against nuget.org: `microsoft.aspnetcore.app.runtime`
resolves for `win-x64`, `osx-arm64` and `linux-arm64`, and **404s for `android-arm64` and
`ios-arm64`**. There is no ASP.NET Core on mobile, by design — it is why MAUI ships
`BlazorWebView` rather than hosting Kestrel. That also invalidates this plan's own fallback, which
assumed "a small in-process Kestrel *only* for the two file-serving endpoints".

### What replaced it: a native head over the shared Core

`SnapZap.Core` multi-targets `net10.0-android36.0` (behind an opt-in flag, AC-2.7) and a native
C# Android UI drives it directly. No WebView, no HTTP, no Blazor, no SignalR.

The reasoning, in one line: **the expensive, dangerous-to-rebuild part of SnapZap is Core, not the
UI.** The perceptual hash with all four rotations, complete-linkage grouping, the NSFW mean-not-max
tiling rule and its measured thresholds, the burst safety gate — these are tuned behaviours backed
by 245 tests whose failure mode is *silently deleting photos that should have been kept*. The UI is
the cheap part, and it is also the part that does not transfer to touch anyway. A full native
rewrite in Kotlin would have inverted that trade; sharing an RCL with the desktop would have
refactored the shipping Windows/macOS product to serve a port that has not shipped.

Three things this buys that the WebView architecture could not:

- **No `INTERNET` permission at all.** It was only ever needed so a WebView could reach loopback.
  An app that reads the user's entire photo library and cannot talk to the network is a strictly
  better thing to ship. Defend this.
- **No scroll-windowing risk.** `PhotoGrid`/`interop.js` window rows by hand because
  `<Virtualize>` cannot resolve a viewport in that flex layout. Android's view recycling does it
  natively — so AC-6.5, the highest-risk item in this plan's original risk table, does not exist.
- **The desktop app is untouched.** No RCL extraction, no render-mode change, no new risk to
  Windows/macOS.

The cost, stated plainly: **two UIs to maintain.** Accepted, because a shared UI would have
diverged through touch conditionals immediately anyway.

### Proven on-device (arm64 emulator, API 37)

Every native dependency works, and results are identical to the dev Mac:

| | |
|---|---|
| SkiaSharp | loads; PNG encode byte-identical; blur Δ0.00E+000 |
| Perceptual hash | **bit-exact** vs desktop — dedup agrees across platforms |
| SQLite | opens, schema created, no `Batteries_V2.Init()` needed |
| ONNX / NSFW | Δ3.8e-11 vs desktop; onnxruntime#29270 does not reproduce |

Re-runnable at any time from the app's own **Run Core self-test** button (`CoreSelfTest.cs`).

---

## 1. Scope for v1

Confirmed:
- **Reach beyond the system Photos library** — arbitrary folders, same as desktop. This rules out
  building on `MediaStore`/`ContentResolver` (which only sees the indexed Photos collection) and
  means the Android app needs the same broad, unscoped filesystem access the desktop app already
  assumes. See §3.
- **No export.** `ExportDialog`, `ExtractDialog`, `HideDialog` and their toolbar entry points are
  out of scope for v1 — don't wire them into the Android UI at all, which also means `ILinkService`
  is never actually exercised (a stub is still required to satisfy the interface, but its
  correctness is untested and doesn't matter for v1 — see §2).
- **Test devices:** Samsung Galaxy S23 Ultra (One UI's WebView/Chrome build) and a mid-range
  Motorola (closer to stock Android/WebView). Test both for every milestone below, not just one —
  the whole reason to have two is OEM WebView variance, and a plan that only gets verified on the
  flagship doesn't actually cover that.

In scope: scan a folder tree, exact/variant/burst duplicate detection, NSFW scoring (if the ONNX
spike in §4 pans out — otherwise ships with NSFW scoring gracefully absent, exactly like a
missing model does on desktop today), blur detection, filters, delete-with-undo (including the
per-item restore from the trash-recovery work).

Out of scope for v1, explicitly: export (any mode), Hide/Extract (steganography), DirectML/GPU
inference, anything Windows-only that doesn't apply here anyway.

---

## 2. `IPlatformServices` — Android implementation

> **Status:** the *instinct* here was right and the *placement* advice was wrong in one direction
> and right in the other. Keeping Android SDK types out of `SnapZap.Core` held — `FolderTrashService`
> is portable and unit-tested on macOS. But "keep Core free of any Android workload dependency"
> did **not** hold: `SkiaSharp.NativeAssets.Android` is TFM-gated, so Core must multi-target
> regardless (AC-2.7). `IAppHost` is void — see below.

Add `src/SnapZap.Core/Platform/AndroidServices.cs` (new file, same pattern as
`MacOsServices.cs`/`WindowsServices.cs`) or the new Android host project if `SnapZap.Core` should
stay free of any Android SDK reference — **prefer keeping it out of `SnapZap.Core`** so that
project stays a plain portable-.NET library with no Android workload dependency; put the Android
implementations in the new host project instead, referencing `SnapZap.Core.Platform`'s interfaces
from there. (`MacOsServices.cs`/`WindowsServices.cs` currently live inside `Core` because they have
no SDK dependency of their own — `osascript`/Win32 P/Invoke don't need a workload. Android's
`Context`/`Android.Webkit` types do, so this is a real difference worth breaking the existing
pattern for.)

### `AndroidTrashService : ITrashService`

> ✅ **Built, better than drafted.** The draft below takes an Android `Context` purely to resolve a
> directory path, which would have made the risky half untestable off-device for no reason. What
> shipped instead is `SnapZap.Core.Delete.FolderTrashService` — portable, takes its trash root as a
> constructor parameter, no SDK types, **10 unit tests running on macOS today**. The Android head
> supplies the path.
>
> It also fixes a real bug in the draft: `RestoreAsync` used `File.Move(..., overwrite: false)`,
> which *throws* when the original path is occupied, while `ITrashService`'s own contract says
> return `false`. Cross-volume moves fall back to copy+delete, which matters when the trash root
> and an SD-card source differ.

No `MediaStore` dependency — the app needs to recycle files outside the indexed Photos
collection, and `createTrashRequest` only operates on already-indexed `content://` URIs. Simplest
robust option, and it mirrors a pattern the codebase already trusts (`DeleteService.RestoreMoved`
does exactly this move-then-move-back for export's undo-a-move case):

```csharp
public sealed class AndroidTrashService(Context context) : ITrashService
{
    string TrashRoot => Path.Combine(context.GetExternalFilesDir(null)!.AbsolutePath, "trash");

    public Task<string?> SendToTrashAsync(string path, CancellationToken ct = default)
    {
        Directory.CreateDirectory(TrashRoot);
        // Collision-safe: two files named IMG_0001.jpg from different folders would otherwise
        // clobber each other in one flat trash directory.
        var dest = Path.Combine(TrashRoot, $"{Guid.NewGuid():N}_{Path.GetFileName(path)}");
        File.Move(path, dest);
        return Task.FromResult<string?>(dest);
    }

    public Task<bool> RestoreAsync(string originalPath, string? trashedLocation, CancellationToken ct = default)
    {
        if (trashedLocation is null || !File.Exists(trashedLocation)) return Task.FromResult(false);
        Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
        File.Move(trashedLocation, originalPath, overwrite: false);
        return Task.FromResult(File.Exists(originalPath));
    }
}
```

`GetExternalFilesDir(null)` is the app's own sandboxed external directory — always writable
without any extra permission (even under scoped storage), survives app restarts, and is cleared
on uninstall, which is the expected behavior for a trash. This is *not* the system Recycle
Bin/Gallery trash — it's app-private, same conceptual role as `~/.Trash` on the Mac impl, just
without OS-level integration. Acceptable for v1; revisit only if "show up in the system Gallery's
own trash" becomes a real ask later.

### `AndroidLinkService : ILinkService`

Never exercised in v1 (§1 — no export), but `IPlatformServices` needs something registered:

```csharp
public sealed class AndroidLinkService : ILinkService
{
    public bool SameVolume(string a, string b) => false;   // hardlinks unsupported on Android's
                                                             // scoped-storage model; matches the
                                                             // existing cross-volume fallback in
                                                             // ExportEngine, so IF export ever
                                                             // gets exposed later it silently
                                                             // degrades to Copy instead of throwing.
    public void CreateHardLink(string linkPath, string targetPath) =>
        throw new NotSupportedException("Hardlinks are not supported on Android.");
}
```

### `IInferenceProvider` / NSFW

**No new code needed.** `OnnxNsfwClassifier`'s constructor already accepts a nullable
`SessionOptions` (`Core/Nsfw/OnnxNsfwClassifier.cs:23`) and nothing in the codebase currently
constructs or registers an `IInferenceProvider` implementation at all — DirectML is listed in
CLAUDE.md as "not started," and today's Windows/macOS paths both just run CPU inference with
default options. Android needs exactly the same: pass `null`/default options. The actual open
risk here is **not** "how do I write a CPU provider," it's **whether
`Microsoft.ML.OnnxRuntime`'s Android native binaries resolve and load at all** under this
project's `net10.0-android` target — this has a documented history of load-time failures on
mobile .NET targets ([microsoft/onnxruntime#11618](https://github.com/microsoft/onnxruntime/issues/11618)).
Test this explicitly and early (§4) — construct an `OnnxNsfwClassifier` against the real model
file on-device and score one image, before assuming NSFW scoring works at all on Android.

### `IAppHost` — ⛔ void

Presumed §0's architecture. `IAppHost` exists to hand a bound URL to something that displays it;
with no server there is no URL, and with a native UI there is nothing to display it in. No
`AndroidWebViewAppHost` was written and none should be. `AppHostFactory` keeps its Windows/macOS
branches untouched.

The one piece of this that did land is in `SnapZap.App`: `PhotinoAppHost` was split into its own
file so it and its package can be excluded from a non-desktop TFM. That is scaffolding for a future
multi-targeted `SnapZap.App`, not something Android uses.

---

## 3. Storage: full filesystem access, not `MediaStore`

> ✅ **Validated on-device.** A folder created by `adb push` and never opened in the Gallery — so
> not `MediaStore`-indexed — scanned end-to-end: enumerated, hashed, thumbnailed, catalogued
> (AC-4.5). This was the single assumption the whole storage model rested on.
>
> ⚠ **Items 3 and 4 below are obsolete.** `INTERNET` and the loopback cleartext config existed
> only so a WebView could reach in-process Kestrel. The native head makes **no network requests of
> any kind**, so neither is declared, and the manifest is better for it. Do not add them back
> without a specific reason.
>
> ✅ Item 5 (`DirectoryPickerDialog`) shipped as `SnapZap.Core.Platform.DirectoryRoots`, a testable
> seam with an Android branch returning `/storage/emulated/0`. Item 6 (`CatalogService`'s location)
> was measured, not assumed: `LocalApplicationData` → `/data/user/0/com.snapzap.android/files`.
> Note `UserProfile` resolves to the *same* app-private path, so the old default would have opened
> a directory the user has no reason to recognise — which is exactly what the Android branch fixes.

Because v1 needs to reach folders the system Photos library doesn't index, this **does not**
build on `ContentResolver`/`MediaStore` at all — that would only satisfy the Play Store's
narrower, better-behaved access model, which doesn't matter for a sideloaded personal-use app.
Instead:

1. Request `MANAGE_EXTERNAL_STORAGE` ("All files access") at runtime, via
   `Settings.ActionManageAppAllFilesAccessPermission` (Android 11+/API 30+). This is the one
   permission class Google Play gates hard for store distribution — irrelevant here since this
   app isn't going through Play Store review; sideloading has no such gate.
2. Once granted, **`SnapZap.Core`'s `Scanner`, `File.Exists`, `Directory.GetDirectories`, and
   everything else that already operates on raw paths needs zero changes.** This was the single
   largest cost item in the original proposal (a `MediaStoreSource`/`IPhotoSource` rewrite) and it
   goes away entirely under this permission model — full credit to reusing the desktop scanning
   code completely unmodified.
3. Add a manifest entry (`AndroidManifest.xml`):
   ```xml
   <uses-permission android:name="android.permission.MANAGE_EXTERNAL_STORAGE"
                     tools:ignore="ScopedStorage" />
   <uses-permission android:name="android.permission.INTERNET" />
   ```
   `INTERNET` is required for *any* WebView network request, including to `127.0.0.1` — easy to
   forget since nothing here actually reaches outside the device, and its absence produces a
   silent blank WebView with no obvious error.
4. **Cleartext traffic:** Android blocks plain HTTP by default for apps targeting API 28+.
   Loopback-to-Kestrel is plain HTTP. Add a scoped network security config rather than a blanket
   `usesCleartextTraffic="true"` (unnecessary risk surface for zero benefit — nothing else in this
   app talks HTTP):
   ```xml
   <!-- res/xml/network_security_config.xml -->
   <network-security-config>
     <domain-config cleartextTrafficPermitted="true">
       <domain includeSubdomains="false">127.0.0.1</domain>
     </domain-config>
   </network-security-config>
   ```
   referenced from the manifest's `<application android:networkSecurityConfig="@xml/network_security_config">`.
5. **`DirectoryPickerDialog.razor`** (`App/Components/DirectoryPickerDialog.razor:110-116`) needs
   an Android-specific default start path and root list. It currently falls back to
   `Environment.SpecialFolder.UserProfile` and, cross-platform, `DriveInfo.GetDrives()` — neither
   maps sensibly to Android. Give it `/storage/emulated/0` as the default start and root on this
   platform (the conventional primary shared-storage mount point); a `DriveInfo`-shaped concept of
   "other volumes" (an inserted SD card, if the test devices have one) is a nice-to-have, not v1 —
   note it in the doc's follow-ups rather than building it blind.
6. **`CatalogService`**'s catalog-DB location (`App/CatalogService.cs:46`,
   `Environment.SpecialFolder.LocalApplicationData`) needs verifying on-device, not assumed. .NET's
   Android BCL implementation generally does resolve this special folder to the app's private
   internal storage, but confirm during the spike rather than discovering it's wrong after the
   scanning/dedup pipeline is already wired up and silently writing somewhere unexpected.

---

## 4. Ordered task list

Everything through the storage model is done. What remains is UI.

### Done

| | | |
|---|---|---|
| ✅ | §0 spike | Run, **failed**, architecture replaced (§0) |
| ✅ | Core multi-targets Android | Opt-in `-p:IncludeAndroid=true` (AC-2.7) |
| ✅ | Native dependencies | Skia, SQLite, ONNX all verified on-device (§0) |
| ✅ | Platform services | Portable `FolderTrashService` + link stub, 10 tests |
| ✅ | Directory roots seam | `DirectoryRoots` with Android branch, 8 tests |
| ✅ | Android head scaffold | `src/SnapZap.Android`, installable APK |
| ✅ | Permission gate | `MANAGE_EXTERNAL_STORAGE`, blocks rather than degrades |
| ✅ | Scan + grid | Non-indexed folder → 6 photos → grid; cache survives restart |

### Next

1. **Dedup pass after scan.** Run `VariantFinder`/`BurstFinder`/`ExactDuplicateFinder` +
   `GroupReconciler` on-device and surface group counts. Pure Core, no new algorithms — the point
   is confirming grouping behaves identically to desktop on a real library.
2. **Swipe review UI** (ROADMAP item 11). Group-at-a-time, swipe right to keep / left to remove —
   the touch expression of `DupeReview.razor`. ⚠ Read the keep/remove decision through the existing
   `InScope` / `IsBulkSelectable` predicate; do **not** re-derive it (ROADMAP item 12). A burst is
   five different photographs and must never be bulk-selectable.
3. **Delete with undo**, on `FolderTrashService`. Plus AC-3.6: the app-private trash needs a size
   read-out and an empty action, since it is invisible to the user's file manager.
4. **Real-device pass** on both handsets — the grant flow (AC-4.2) and decode throughput on a real
   library, neither of which an emulator answers.
5. **Performance on a real library.** Everything measured so far is six photos.

### Explicitly not doing

Export, Hide/Extract, DirectML — out of v1 scope per §1, and now also structurally absent from the
Android UI rather than merely unwired.

---

## 5. Risks (specific, not generic)

| Risk | Why it matters here | What to do about it |
|---|---|---|
| §0's Kestrel-in-Android-process bet doesn't pan out | It's the foundation every other step in this plan sits on | The spike is step 1 for a reason — don't write step 2 onward until it's confirmed |
| ONNX Runtime's Android native load has a rough history | NSFW scoring is a real, used feature, not incidental | Spike it in parallel (task 7), independent go/no-go — the rest of the app doesn't depend on it |
| `MANAGE_EXTERNAL_STORAGE`'s runtime-grant UX varies by OEM | Directly affects both test devices differently — Samsung's grant flow and stock-Android-adjacent Motorola's aren't pixel-identical | Test the grant flow itself on both phones, not just what happens after it's granted |
| WebView build/version differs across the two test devices | This plan's entire UI is one WebView — rendering/JS bugs here are the whole app, not a corner of it | `interop.js`'s scroll-windowing math (CLAUDE.md's `.grid`/`overflow-anchor` note) is the most likely thing to behave differently between the two — test scrolling a large grid on both, specifically |
| `LocalApplicationData`/`UserProfile` special-folder resolution on Android is assumed, not confirmed | A wrong catalog-DB path fails quietly (creates a DB somewhere unexpected rather than erroring) | Explicitly log/verify the resolved path during the spike, don't just trust it silently works |

---

## 6. Definition of done for v1

- Runs on both the S23 Ultra and the Motorola.
- Grants `MANAGE_EXTERNAL_STORAGE`, scans a folder outside the system Photos library.
- Dedup (exact/variant/burst), blur detection, and filters all work end-to-end.
- NSFW scoring works, or is confirmed absent-and-explained (matching the existing "optional
  sidecar, gracefully degrades" model) — not silently broken.
- Delete-to-app-trash and restore (both whole-batch and single-item) round-trip correctly.
- Catalog DB persists across an app restart.
- No export, Hide/Extract, or DirectML code paths are reachable from the UI.

---

## 7. Effort estimate

The original 2-3 week figure assumed the §0 spike passed and the entire Blazor UI came along for
free. It didn't, so that number is void.

What actually happened: the infrastructure half — spike, multi-targeting, native dependency
validation, platform services, storage model, scaffold, permission gate, scan-to-grid — landed in
**one working session**, largely because the preparatory refactoring was done *before* the spike
and none of it depended on the hosting architecture. That was luck turned into design, and it is
the reason the architecture being wrong cost nothing.

What remains is a UI project: swipe review, delete/undo, trash management, and a real-device pass.
Estimating it in weeks would be guessing — the honest statement is that **every unknown that could
have killed v1 is now closed**, and what is left is ordinary UI work whose scope the team controls
directly by choosing how much of the desktop's feature surface to reproduce.

The narrower that answer, the sooner it ships. "Triage duplicates on my phone" is a much smaller
target than desktop parity, and it is the one worth aiming at first.
