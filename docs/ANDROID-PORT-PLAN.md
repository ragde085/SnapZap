# Android port — implementation plan

**Status:** Ready to execute in a local session with the Android SDK/workload installed.
**Supersedes:** the earlier research proposal (Option A / MAUI Blazor Hybrid), refined against
three decisions: the app needs to reach folders outside the system Photos library, export mode
is not needed for v1, and the two test devices are a Samsung Galaxy S23 Ultra and a mid-range
Motorola.

This plan was written in a sandbox with **no .NET SDK, no Android SDK, and no `adb`** — every
file below was authored by hand against the actual `SnapZap.Core`/`SnapZap.App` source, but none
of it has been compiled. Treat step 0 as mandatory and non-skippable: it's the one thing this
whole plan bets on, and it needs a real build to confirm before anything else is built on top of
it.

---

## 0. The one architectural bet — validate this first

The obvious way to port a Blazor app to Android is **Blazor Hybrid**: a `BlazorWebView` control
that runs the Razor component tree directly inside a WebView, no HTTP server involved. That's
real work — it means moving off Blazor Server's render mode, and re-hosting the app's two minimal
API endpoints (`/api/thumb/{hash}`, `/api/full/{id}` — `Program.cs:93-136`) some other way, since
`BlazorWebView` doesn't run inside a normal ASP.NET Core pipeline.

There's a cheaper option worth trying first: **`SnapZap.App` already runs its own embedded
Kestrel server** (`Program.cs:26-43`) and hands the bound URL to whatever `IAppHost` shows it —
Photino on Windows, a plain browser tab on macOS (`AppHostFactory`, `App/Services/AppHost.cs`).
Android can be a fifth `IAppHost`: **run the exact same `WebApplication` in-process inside the
Android app, bound to loopback, and point a plain system `WebView` at it** instead of Photino.

If this works, the payoff is large: `Program.cs`, both API endpoints, Blazor Server's render
mode, `AppState`, and every Razor component are reused completely unmodified — the only new code
is a small Android host shell and the platform-services implementation in §2. If it doesn't work
(ASP.NET Core's hosting stack turns out not to run cleanly under the `net10.0-android` TFM, or
Android's WebView refuses loopback traffic for some unresolvable reason), the fallback is classic
`BlazorWebView`/Blazor Hybrid — more work, described briefly at the end of §0, not worth designing
in detail until the cheap option is actually ruled out.

**Day-1 spike — do this before writing anything else in this plan:**

1. `dotnet workload install android` (or install via Visual Studio's MAUI workload, which
   includes it).
2. Create a throwaway `net10.0-android` project. Reference `Microsoft.AspNetCore.App`'s
   constituent packages directly (`Microsoft.AspNetCore.Hosting`, `Microsoft.AspNetCore.Server.Kestrel`,
   or just try `WebApplication.CreateBuilder` the normal way and see what resolves) and confirm a
   minimal `WebApplication` binds `http://127.0.0.1:0` and serves a "hello" route inside an Android
   `Activity`.
3. Confirm a plain `Android.Webkit.WebView` (or MAUI's `WebView` control, if using MAUI project
   scaffolding rather than a bare Android head) can `LoadUrl` that loopback address and render the
   response. Watch for Android's cleartext-traffic block (§3) — this is the most likely reason
   step 3 shows a blank white screen even when step 2 works.
4. Confirm SignalR's WebSocket transport survives the trip (load a real page from `SnapZap.App`,
   not just a hello route, and check the Blazor circuit actually connects — an interactive button
   click is the simplest proof).

If all four pass: proceed with the rest of this plan as written. If step 2 or 4 fails outright
(not just needs a config tweak): stop, and re-scope around `BlazorWebView` instead — extract
`Components/`, `Services/`, and `wwwroot/` into a Razor Class Library referenced by both
`SnapZap.App` and a new MAUI head, keep a small in-process Kestrel *only* for the two file-serving
endpoints (BlazorWebView still needs *some* way to serve `<img src="/api/thumb/...">`), and let
BlazorWebView own the component rendering instead of Blazor Server. That's meaningfully more
surgery than §1-§5 below, which all assume the spike passed.

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

### `IAppHost`

Only relevant if the §0 spike passes. Add `AndroidWebViewAppHost : IAppHost` in the new host
project:

```csharp
public sealed class AndroidWebViewAppHost(Android.Webkit.WebView webView) : IAppHost
{
    public void Run(string url) => webView.LoadUrl(url);
}
```

`AppHostFactory` (`App/Services/AppHost.cs`) currently branches on `RuntimeInformation.IsOSPlatform`
to pick Photino vs. browser — it needs a third branch for `OSPlatform.Create("ANDROID")` (or
however `RuntimeInformation` reports on this TFM — confirm during the spike) resolving to this
type instead. Since `Program.cs`'s `AppHostFactory.Create(...).Run(url)` call already expects to
hand off to *something* that shows the URL, this is a small, contained change to an existing
switch, not new plumbing.

---

## 3. Storage: full filesystem access, not `MediaStore`

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

Each step should be a working, testable increment on both devices — don't stack three steps and
test once, since the whole point of having two test phones is catching OEM-specific breakage
early per-step.

1. **§0 spike.** Go/no-go gate. Don't proceed past this until it passes on *both* devices.
2. **Project scaffold.** New Android host project (`src/SnapZap.Android` or similar — bare
   `net10.0-android` head if the spike suggests MAUI's Controls/Shell machinery isn't needed for a
   single-WebView app; a minimal MAUI project if the bare-Android-SDK route hits packaging friction
   the MAUI templates already solve). References `SnapZap.Core` and `SnapZap.App` (for `Program.cs`'s
   composition — extract the `WebApplication` construction into a small shared method both
   entry points can call, since `Program.cs` today is a top-level-statements file, not a reusable
   method; keep the platform-specific `AppHostFactory`/foreground-run logic separate from that
   shared builder logic as part of this extraction). Add to `SnapZap.slnx`.
3. **Manifest + permissions** (§3): `MANAGE_EXTERNAL_STORAGE` request flow (a simple screen or
   inline banner gating the rest of the UI until granted, same spirit as the desktop
   `DependencyDialog` pattern already in `App/Components/DependencyDialog.razor` for optional
   sidecars — this one is a hard requirement, not optional, so block rather than degrade),
   `INTERNET`, cleartext config.
4. **`AndroidServices`** (§2): trash service, link service stub, `IAppHost` Android branch.
5. **`AppHostFactory` wiring** — new platform branch, DI registration for
   `ITrashService`/`ILinkService` mirroring the existing `if (Windows) ... else (macOS)` block in
   `Program.cs:59-68`.
6. **`DirectoryPickerDialog`/`CatalogService` Android paths** (§3 items 5-6).
7. **ONNX-on-Android spike** (can run in parallel with 2-6, since it's independent): construct
   `OnnxNsfwClassifier` against the real model file on both test devices, score a known image,
   confirm the result is sane. If it fails, decide then whether to debug the native-load issue or
   ship v1 with NSFW scoring off — don't let this block the rest of the port either way.
8. **Full run-through** on both devices: pick a folder outside the Photos library, scan it,
   confirm dedup/blur/NSFW (if step 7 passed) results render, delete a photo and restore it from
   history (including the single-item restore from the trash-recovery work), confirm the DB
   persists across an app restart.
9. **Touch pass** — deliberately *last*, and deliberately scoped down from the original proposal's
   full "redesign the interaction model" phase, because export/Hide/Extract are already out and a
   lot of desktop-only affordances (right-click, keyboard shortcuts) simply have no equivalent to
   design here rather than needing a redesign. Concretely: the per-thumbnail restore button in
   `UndoDialog.razor` is hover-revealed (`.undo-thumb-restore` in `app.css`) and needs a
   touch-visible always-shown or tap-to-reveal treatment, since there's no hover on a touchscreen;
   audit `Card.razor`/`FilterBar.razor`/`Toolbar.razor` for the same hover-only pattern before
   calling this done, rather than fixing only the one instance that's top of mind.

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

Meaningfully lower than the original proposal's 6-9 weeks, because two of that estimate's biggest
line items are gone: the `MediaStore` storage rewrite (§3 — not needed, full filesystem access is
in scope) and the export/hardlink UI work (§1 — explicitly out of scope). Rough shape, assuming
the §0 spike passes:

- Spike + scaffold + platform services (§0, tasks 1-5): **3-5 days**
- Storage/directory-picker Android paths (task 6): **1-2 days**
- ONNX spike (task 7, parallelizable): **1-3 days**
- Full run-through + fixes (task 8): **2-4 days**
- Touch pass (task 9): **3-5 days**

**Total: roughly 2-3 weeks, one person**, versus the original 6-9 week estimate — almost entirely
because the storage model and export scope both turned out to not need rebuilding, not because
any individual phase got easier. If the §0 spike fails and this falls back to classic
`BlazorWebView`, add back roughly the original proposal's UI-layer estimate for the parts that
bet on Kestrel-in-process (§0's fallback paragraph) — the platform-services and storage work in
§2-§3 stays valid either way.
