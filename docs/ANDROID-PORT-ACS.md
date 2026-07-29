# Android port — acceptance criteria

Companion to [ANDROID-PORT-PLAN.md](ANDROID-PORT-PLAN.md). The plan says *what* to build and in
what order; this document says *how we know each piece is done*, and separates the work that can
be executed today from the work that cannot begin until an Android toolchain and the two test
devices are present.

**Convention.** Every AC is independently checkable. Each carries a gate:

| Gate | Meaning |
|---|---|
| 🟢 **NOW** | Executable on the macOS dev machine today, with the tools already installed. Verified by `dotnet build` / `dotnet test` / code inspection. |
| 🟡 **SDK** | Needs the Android SDK installed (the .NET Android workload is already present — see §1.1). No physical device required (emulator sufficient). |
| 🔴 **DEVICE** | Needs both physical test devices (Galaxy S23 Ultra + mid-range Motorola). Cannot be faked by an emulator — the whole point is OEM WebView/permission-flow variance. |

---

## 1. Analysis: what the review turned up

The plan is sound in its central bet and in its scoping decisions (no export, no `MediaStore`,
full-filesystem access). Three findings change what has to happen and in what order.

### 1.1 The environment cannot run the §0 spike

Verified in this working tree:

```
dotnet --version        → 10.0.302        ✅
dotnet workload list    → (empty)         ⚠ MISLEADING — see below
which adb               → not found       ❌
ANDROID_HOME            → unset           ❌
```

**Correction, established later by actually attempting an Android build** rather than trusting
`dotnet workload list`: the .NET Android build support **is already installed on this machine.**
`/usr/local/share/dotnet/packs/` contains `Microsoft.Android.Sdk.Darwin/36.1.69`,
`Microsoft.Android.Ref.36`, and Mono/CoreCLR/NativeAOT runtime packs for `android-arm`,
`android-arm64`, `android-x64` and `android-x86`. `dotnet workload install android` is **not
needed**; the empty `workload list` output does not mean what it appears to mean.

A `-p:IncludeAndroid=true` build of `SnapZap.Core` gets all the way through NuGet restore and
fails only at `Xamarin.Android.Tooling.targets` with **XA5300: The Android SDK directory could not
be found**. So the single missing prerequisite is the **Android SDK itself** (command-line tools /
platform-tools / a platform image) — not the .NET workload.

#### Toolchain state as of this writing

Android Studio, the `android` workload and `android-commandlinetools` have since been installed.
Current measured state:

| Component | State |
|---|---|
| .NET Android packs | ✅ `Microsoft.Android.Sdk.Darwin/36.1.69` + arm/arm64/x64/x86 runtime packs |
| Android SDK | ✅ `~/Library/Android/sdk` (`build-tools/36.0.0`, `platform-tools`, `emulator`) |
| `adb` | ✅ works — `~/Library/Android/sdk/platform-tools/adb`, v1.0.41 |
| Devices attached | ❌ none yet (`adb devices` empty) |
| API 36 platform | ✅ `platforms/android-36` installed |
| Emulator image / AVD | ❌ none — no `system-images/`, `emulator -list-avds` empty |
| `ANDROID_HOME` / PATH | ❌ unset; every build so far passed `-p:AndroidSdkDirectory=` explicitly |

**✅ `SnapZap.Core` now builds for `net10.0-android36.0`.** This is the single most important
result so far — it clears the §1.2 SkiaSharp risk at the **build and restore** level:

```
src/SnapZap.Core/bin/Debug/net10.0-android36.0/SnapZap.Core.dll   ← produced, 230 KB
SkiaSharp.NativeAssets.Android/4.150.1
    runtimes/android-arm/native/libSkiaSharp.so
    runtimes/android-arm64/native/libSkiaSharp.so     ← the ABI both test devices use
    runtimes/android-x64/native/libSkiaSharp.so
    runtimes/android-x86/native/libSkiaSharp.so
```

⚠ **This does not clear AC-2.3.** Resolving and compiling is not loading and executing. Whether
`libSkiaSharp.so` actually initialises on-device, and whether a perceptual hash computed there is
bit-identical to the dev-Mac value that `GoldenValueTests` pins, is still unanswered and still
🔴 DEVICE. The same distinction applies to ONNX (AC-2.4): restore succeeding says nothing about
issue #29270, which is about which native library gets **bundled** on a macOS host.

#### Two CLI notes that cost time

- **`sdkmanager` is deprecated.** The replacement is the `android` binary in `cmdline-tools`:
  `android sdk install <package>`. Package names use `/` separators, not `;` — so
  `platforms/android-36`, not `platforms;android-36`. Global flags like `--no-metrics` go
  **before** the subcommand.
- **The new CLI has no `--sdk_root` flag**, so it installs into whatever `ANDROID_HOME` points at.
  With `ANDROID_HOME` unset it can silently populate a different SDK than the one .NET is building
  against. Set it explicitly (below) before installing anything.

**Resolved.** The trap worth remembering: the SDK had `platforms/android-36.1`, but .NET's
`net10.0-android36.0` target looks for `platforms/android-36/android.jar` **exactly** — the
directory names are not interchangeable, and the mismatch fails with **XA5207**. Fixed with:

```bash
export ANDROID_HOME="$HOME/Library/Android/sdk"
/opt/homebrew/share/android-commandlinetools/cmdline-tools/latest/bin/android \
  --no-metrics sdk install platforms/android-36
```

Still recommended, so no later command needs an explicit `-p:AndroidSdkDirectory=`:

```bash
export ANDROID_HOME="$HOME/Library/Android/sdk"
export PATH="$PATH:$ANDROID_HOME/platform-tools"
```

Two things that restore proved along the way, which were open questions before:

- `SkiaSharp.NativeAssets.Android/4.150.1` **resolves** for `net10.0-android36.0`, confirming both
  the package choice and the TFM.
- `Microsoft.ML.OnnxRuntime/1.27.1` **also resolves** for that TFM. This does **not** clear
  #29270 (AC-2.4) — that bug is about which *native* library gets bundled at package time on a
  macOS host, which restore has nothing to say about — but it removes "does it even restore" as a
  question.

The plan's step 1 is a hard go/no-go gate, and it is **not executable here**. Everything downstream
of the gate that assumes it passed (§2 `IAppHost`, §4 tasks 2-5, 8) is therefore blocked. This is
not a reason to stall: a substantial slice of the port is *preparatory refactoring that is correct
regardless of which way the spike goes*, and §7's own fallback paragraph says so explicitly — "the
platform-services and storage work in §2-§3 stays valid either way". That slice is what §5 below
parallelizes.

### 1.2 SkiaSharp has no Android native asset in this solution — the plan never mentions it

`src/SnapZap.Core/SnapZap.Core.csproj` references:

```xml
<PackageReference Include="SkiaSharp.NativeAssets.macOS" Version="4.150.1" />
<PackageReference Include="SkiaSharp.NativeAssets.Win32"  Version="4.150.1" />
```

There is no `SkiaSharp.NativeAssets.Android`. SkiaSharp is not a corner of this app — per CLAUDE.md
it is *every* decode, thumbnail, EXIF geometry, blur score and perceptual hash
(`Scanner.Analyze` → `DecodeGray` → `BlurDetector` + `PerceptualHash`). Without a resolving Android
native binary, scan/dedup/blur — i.e. the entire in-scope v1 feature set — fails at runtime, and it
fails *later* than the ONNX risk the plan does call out, because it fails during the first scan
rather than at classifier construction.

**This is a higher-severity risk than the ONNX one in §5, and it is absent from the plan's risk
table.** It gets its own AC epic (E2) and its own spike, run alongside the ONNX spike.

**And the fix is not "add a package".** Independently verified against the actual `.nupkg` from
`api.nuget.org`: `SkiaSharp.NativeAssets.Android` 4.150.1 ships `lib/` and dependency groups for
**only** `net9.0-android35.0` and `net10.0-android36.0`. A plain `net10.0` project cannot consume
it. So `SnapZap.Core` has to **multi-target** — `net10.0;net10.0-android36.0` — with the native
asset conditioned per TFM.

That directly contradicts the plan's §2, which says to "**prefer keeping it out of
`SnapZap.Core`** so that project stays a plain portable-.NET library with no Android workload
dependency". That preference is still right for the *`IPlatformServices` implementations* (they
need `Android.Content.Context`, and W2's portable `FolderTrashService` shows the seam can be drawn
so Core never sees an SDK type). It is **not achievable for SkiaSharp**, because the native asset
is bound to the TFM rather than to any Android API in our code.

**Resolved (AC-2.7): opt-in.** Listing the Android TFM unconditionally would make restore evaluate
it on every build, so anyone building only the shipping Windows/macOS product — and any CI runner —
would need Android tooling. Instead Core keeps a singular `<TargetFramework>` by default and goes
plural only under `-p:IncludeAndroid=true`:

```bash
dotnet build src/SnapZap.Core -p:IncludeAndroid=true
```

The desktop build is therefore unchanged and entirely non-cross-targeting. The price is AC-2.9: an
un-built TFM rots silently, so **CI must build with the flag** or the guard is decoration.

### 1.3 `SnapZap.App` cannot be referenced from an Android head as it stands

Plan task 2 says the Android head "References `SnapZap.Core` and `SnapZap.App`". Three obstacles,
none noted in the plan:

1. **`Photino.NET` is an unconditional `PackageReference` on `SnapZap.App`**, and
   `Services/AppHost.cs` has a file-level `using Photino.NET`. Referencing `SnapZap.App` from a
   `net10.0-android` head drags a desktop-only native package into the Android build.
2. **`SnapZap.App` is `Microsoft.NET.Sdk.Web`, single-targeted `net10.0`.** The Blazor
   static-web-assets pipeline that produces `wwwroot/_framework/blazor.web.js` is a Web-SDK target
   — how (or whether) it flows into an APK's asset packaging is exactly the kind of unknown that
   produced the two historical "serves 200, renders dead" failures the csproj's `VerifyPublishOutput`
   target now guards against. The Android head needs the equivalent guard.
3. **`Program.cs` is top-level statements** and its tail is desktop-only (`AppHostFactory`,
   `NativeDialog.Error`, `ConsoleWindow.HideIfOwned`). The plan already calls for extracting the
   `WebApplication` construction into a shared method; that extraction is the prerequisite for
   obstacles 1 and 2 being solvable at all, and it is 🟢 NOW work.

### 1.4 Smaller corrections to the plan

- **The §9 touch pass is smaller than the plan implies.** `app.css` has 27 `:hover` rules but the
  audit's actual target — hover-*reveal*, where an affordance is invisible until hover — is a much
  shorter list. `.undo-thumb-restore` (app.css:977-984) already pairs its hover trigger with
  `:focus-visible`, so it is keyboard-reachable; it is touch that has no path to it. Scope the AC to
  hover-reveal and hover-only-affordance, not to all 27 rules.
- **`AndroidTrashService` as drafted takes an Android `Context`**, which makes it untestable off-device
  and puts Android SDK types in the trash logic. The `Context` is used for exactly one thing: to
  resolve a directory path. Splitting a portable, path-parameterised trash service (testable on macOS
  today, against the existing `DeleteTests` patterns) from a three-line Android wrapper that supplies
  the path makes the risky half verifiable now. This is a strict improvement over the drafted code
  and is 🟢 NOW work.
- **The drafted `RestoreAsync` uses `File.Move(..., overwrite: false)`**, which throws rather than
  returning `false` when something already occupies the original path — a divergence from the
  contract in `ITrashService`'s doc comment ("Returns true if the file is back at its original
  path"). Covered by AC-3.4.
- **No trash retention policy.** Deleting from a 40k-photo library moves originals into app-private
  external storage, which counts against the app's storage footprint and is invisible to the user's
  file manager. Acceptable for v1, but it must be a stated decision with a size read-out, not an
  accident. AC-3.6.
- **`SQLitePCLRaw.bundle_e_sqlite3`** ships Android runtimes, but "ships them" and "they load under
  this TFM" are different claims — folded into the E2 native-load spike rather than assumed.
- **es-MX satellite resources** (`SatelliteResourceLanguages` in the App csproj) must survive APK
  packaging or the app silently ships English-only. AC-1.5.

---

## 2. Acceptance criteria

### E0 — Toolchain and the architectural spike

| ID | Gate | Criterion |
|---|---|---|
| **AC-0.1** | ✅ **DONE (host)** | Android SDK installed and API 36 present; `adb` v1.0.41 resolves; `dotnet build src/SnapZap.Core -p:IncludeAndroid=true` **succeeds**, producing `net10.0-android36.0/SnapZap.Core.dll` with `libSkiaSharp.so` for all four ABIs. Outstanding: `ANDROID_HOME`/PATH are still unset (every command passes `-p:AndroidSdkDirectory=`), no emulator image or AVD exists, and no device is attached. Do **not** run `dotnet workload install android`; it was never what was missing. |
| **AC-0.2** | 🟡 SDK | A throwaway `net10.0-android` project builds with a `WebApplication.CreateBuilder(...)` call that binds `http://127.0.0.1:0` and serves a route returning `200 hello`, launched from inside an `Activity`. The bound port is read back off `app.Urls` and logged. |
| **AC-0.3** | 🔴 DEVICE | On **both** devices, a plain `Android.Webkit.WebView` `LoadUrl`s that loopback address and renders the response body. A blank screen is a fail, not a "needs config" — the config (INTERNET + cleartext, E4) must be in place for this AC to be attempted. |
| **AC-0.4** | 🔴 DEVICE | On **both** devices, a real `SnapZap.App` page loads in that WebView, the Blazor circuit connects over the SignalR **WebSocket** transport (not long-polling fallback), and one interactive button click round-trips. Transport is asserted from the log, not inferred from the click working. |
| **AC-0.5** | 🟡 SDK | The resolved value of `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` and `SpecialFolder.UserProfile` is logged on-device during the spike and recorded in `docs/ANDROID-VERIFY.md`. Neither is assumed. |
| **AC-0.6** | 🔴 DEVICE | **Go/no-go recorded in writing.** If AC-0.2 or AC-0.4 fails outright, the fallback re-scope to `BlazorWebView` is written up before any further code lands. Passing silently is not the same as passing — the outcome is a documented decision either way. |

### E1 — Host composition, extractable today

| ID | Gate | Criterion |
|---|---|---|
| **AC-1.1** | 🟢 NOW | The `WebApplication` construction currently inline in `Program.cs:23-138` is extracted into a single reusable method (e.g. `SnapZapWebHost.Build(SnapZapHostOptions)`) callable from any entry point. The method covers: content-root anchoring, URL binding, DI registration, localization, static files, antiforgery, the three endpoints, and `MapRazorComponents`. |
| **AC-1.2** | 🟢 NOW | The extracted method contains **no** desktop-only code. `AppHostFactory`, `NativeDialog`, `ConsoleWindow` and the `PC_NO_BROWSER` branch stay in `Program.cs`'s tail, on the desktop side of the seam. |
| **AC-1.3** | 🟢 NOW | Platform-service DI registration (`Program.cs:59-68`) is parameterised, not hard-branched on `RuntimeInformation` inside the shared builder — a caller can supply its own `ITrashService`/`ILinkService`. The desktop entry point's behaviour on Windows and macOS is byte-for-byte unchanged. |
| **AC-1.4** | 🟢 NOW | `Photino.NET` no longer flows unconditionally to every consumer of `SnapZap.App`: either the reference is conditioned on a desktop TFM/RID, or the Photino host moves behind a seam that an Android head does not compile. `dotnet build` and `dotnet publish -r win-x64` both still succeed and `VerifyPublishOutput` still passes. |
| **AC-1.5** | 🟢 NOW | Regression proof for the whole extraction: **the existing `dotnet test` suite passes unchanged**, and a `win-x64` publish still produces `wwwroot/_framework/blazor.web.js`, `app.css`, `interop.js`, `favicon.ico`, `snapzap.png` and the `es-MX` satellite assembly. No test may be edited to accommodate the refactor; if one needs editing, that is a behaviour change and it needs justifying. |
| **AC-1.6** | 🟡 SDK | A `src/SnapZap.Android` head builds against the extracted method, is added to `SnapZap.slnx`, and produces an installable APK. |
| **AC-1.7** | 🟡 SDK | The Android head has its own equivalent of `VerifyPublishOutput`: the build **fails** if `blazor.web.js`, `app.css` or `interop.js` are missing from the packaged assets. Given this repo's two prior "serves 200, renders dead" incidents, shipping without this guard is not acceptable. |

### E2 — Native dependency viability (new epic; not in the plan)

| ID | Gate | Criterion |
|---|---|---|
| **AC-2.1** | 🟢 NOW | A written audit of every native-backed `PackageReference` in `SnapZap.Core` — SkiaSharp, `Microsoft.ML.OnnxRuntime`, `SQLitePCLRaw.bundle_e_sqlite3` — recording for each: whether an Android-supporting package exists at (or compatible with) the pinned version, the exact package id to add, the supported ABIs, and the license. Sources cited. Output: `docs/ANDROID-DEPS-AUDIT.md`. |
| **AC-2.2** | ✅ **DONE** | The audit states explicitly whether `SkiaSharp.NativeAssets.Android` at `4.150.1` exists and whether adding it to `SnapZap.Core` affects the existing macOS/Win32 publish size or asset set. |
| **AC-2.3** | 🔴 DEVICE | **SkiaSharp spike.** On both devices, decode a real JPEG off `/storage/emulated/0`, produce a thumbnail, and compute a Laplacian blur score and a 272-bit perceptual hash. The phash must match the value the same file produces on the dev Mac — a platform-dependent hash would silently break dedup across the two, and `GoldenValueTests` is the reference. |
| **AC-2.4** | 🔴 DEVICE | **ONNX spike** (plan task 7). Construct `OnnxNsfwClassifier` against the real `nsfw.onnx` on both devices and score a known fixture; the score matches the desktop score within a stated tolerance. ⚠ The plan cites microsoft/onnxruntime**#11618**, which is closed and Xamarin-era. The live risk is **#29270** (open, filed June 2026): a plain-`net10.0` library `ProjectReference`d from a `net10.0-android` head and **built on macOS** gets the wrong Linux/glibc native library bundled and crashes at launch. That is SnapZap's exact topology and exact dev host. The spike must reproduce or clear #29270 specifically, and the one "fixed" report on that thread came from a Windows host — macOS was never cleared. |
| **AC-2.7** | ✅ **DECIDED** | `SnapZap.Core` multi-targets `net10.0;net10.0-android36.0`, **behind an opt-in flag**: `-p:IncludeAndroid=true`. Default builds keep a singular `<TargetFramework>` and are entirely non-cross-targeting, so the desktop build is unchanged and no contributor or CI runner needs Android tooling to build the shipping product. Verified: default `dotnet build` clean, 245 tests pass, `win-x64` publish unaffected. The Android leg's native assets are conditioned on `TargetPlatformIdentifier`, not a literal TFM string. |
| **AC-2.9** | 🟡 SDK | **The opt-in's own failure mode, and the price of AC-2.7.** A TFM nobody builds rots silently. CI must build `-p:IncludeAndroid=true`, or the Android leg breaks unnoticed and the opt-in is decoration rather than a guard. Open until CI actually does this. |
| **AC-2.8** | 🟡 SDK | `SQLitePCL.Batteries_V2.Init()` is confirmed either called or provably unnecessary on Android. Verified: **no explicit init call exists anywhere in `src/`** today — the bundle's module initializer covers desktop, and whether it fires under this Android TFM is exactly the kind of thing that fails at first DB open rather than at startup. Confirm during the E2 SQLite spike (AC-2.5). |
| **AC-2.5** | 🔴 DEVICE | **SQLite spike.** `catalog.db` opens, the schema is created, a row round-trips, and the file survives a full app restart. |
| **AC-2.6** | 🟡 SDK | Each of AC-2.3/2.4/2.5 has an independently recorded outcome. Per the plan, an ONNX failure degrades NSFW scoring gracefully and does not block the port; a **SkiaSharp or SQLite failure is a v1 blocker** and must be escalated rather than worked around. |

### E3 — Platform services

| ID | Gate | Criterion |
|---|---|---|
| **AC-3.1** | 🟢 NOW | A portable folder-backed `ITrashService` exists in `SnapZap.Core`, taking its trash root as a constructor parameter and referencing **no** Android SDK type. The Android `Context` is used only by the host to supply that path. |
| **AC-3.2** | 🟢 NOW | Unit tests (macOS, in the existing suite) cover: trash-then-restore round-trips the bytes; two files with the same filename from different source folders both survive in one flat trash root; restoring recreates a missing parent directory; a trashed file's original path no longer exists. |
| **AC-3.3** | 🟢 NOW | `SendToTrashAsync` returns a non-null location that `RestoreAsync` accepts, matching the `ITrashService` contract and the behaviour `DeleteService`'s undo log already relies on. Existing `DeleteTests` continue to pass. |
| **AC-3.4** | 🟢 NOW | Contract edges are defined and tested, not left to throw: restoring when the original path is already occupied returns `false` (it does not throw and does not overwrite); restoring a missing/null trashed location returns `false`; trashing across a volume boundary either succeeds or fails with a caught, reported error. |
| **AC-3.5** | 🟢 NOW | `ILinkService` Android stub: `SameVolume` returns `false` (so `ExportEngine` degrades to copy rather than throwing, per the plan), `CreateHardLink` throws `NotSupportedException`. Covered by a test asserting the degrade-not-throw path. |
| **AC-3.6** | 🟡 SDK | The app-private trash is a **stated** design decision, surfaced in the UI: the user can see how much space the trash occupies and empty it. Silently accumulating originals in app storage with no read-out is not acceptable. |
| **AC-3.7** | 🟡 SDK | `AndroidWebViewAppHost : IAppHost` exists in the Android head; `AppHostFactory` gains an Android branch; Windows and macOS host selection is unchanged and its existing tests still pass. |

### E4 — Storage and permissions

| ID | Gate | Criterion |
|---|---|---|
| **AC-4.1** | 🟡 SDK | `AndroidManifest.xml` declares `MANAGE_EXTERNAL_STORAGE` and `INTERNET`, and references a `network_security_config.xml` that permits cleartext for `127.0.0.1` **only** — not a blanket `usesCleartextTraffic="true"`. |
| **AC-4.2** | 🔴 DEVICE | The `MANAGE_EXTERNAL_STORAGE` grant flow is exercised end-to-end on **both** devices — Samsung's One UI flow and the Motorola's stock-adjacent flow are known to differ. Both reach a granted state, and the difference (if any) is recorded in `docs/ANDROID-VERIFY.md`. |
| **AC-4.3** | 🟡 SDK | Until the permission is granted the app **blocks** rather than degrades: a gating screen explains what is needed and offers the grant action. It does not show an empty grid, and it does not let a scan start. |
| **AC-4.4** | 🔴 DEVICE | Permission revoked from system settings while the app is backgrounded → on resume the app returns to the gating screen rather than throwing `UnauthorizedAccessException` mid-scan. |
| **AC-4.5** | 🔴 DEVICE | A folder **outside** the system Photos library (not indexed by `MediaStore`) is scannable end-to-end. This is the whole reason `MediaStore` was ruled out; it must be proven on-device, not assumed from the permission being granted. |

### E5 — Android-specific paths

| ID | Gate | Criterion |
|---|---|---|
| **AC-5.1** | 🟢 NOW | `DirectoryPickerDialog`'s `DefaultStart()` and `Roots()` (`DirectoryPickerDialog.razor:110-116`) are extracted behind a testable seam rather than calling `OperatingSystem.IsWindows()` / `DriveInfo` inline. Windows and macOS behaviour is unchanged and is pinned by tests that did not exist before. |
| **AC-5.2** | 🟢 NOW | That seam has an Android branch returning `/storage/emulated/0` as both default start and sole root, unit-tested without an Android runtime. Removable-volume enumeration is explicitly **out** of v1 and recorded as a follow-up. |
| **AC-5.3** | 🔴 DEVICE | On-device, the picker opens at `/storage/emulated/0`, lists real subfolders, navigates into and back out of them, and reports a permission error as an inline message rather than an empty list. |
| **AC-5.4** | 🔴 DEVICE | `CatalogService`'s resolved `AppDataDir` is logged on-device, confirmed to be app-private internal storage, and `catalog.db` + thumbnails persist across a full app kill and relaunch (plan §3.6, §5's quiet-failure risk). |

### E6 — Touch pass

| ID | Gate | Criterion |
|---|---|---|
| **AC-6.1** | 🟢 NOW | A written audit of every hover-*reveal* affordance (invisible until `:hover`) and every hover-only affordance across `app.css`, `Card.razor`, `Rail.razor`, `Home.razor`, `UndoDialog.razor`, `PhotoGrid.razor`, `SelectionBar.razor`, `PreviewModal.razor`. Each entry records: selector, what it reveals, whether a keyboard/`:focus-visible` path already exists, and the proposed touch treatment. Output: `docs/ANDROID-TOUCH-AUDIT.md`. Distinguish hover-reveal from mere hover-*styling*, which needs no fix. **Note:** `Toolbar.razor` and `FilterBar.razor` are named in CLAUDE.md but do not exist in this tree — that logic lives in `Home.razor`/`Rail.razor`. See §1.5. |
| **AC-6.2** | 🟢 NOW | The audit covers desktop-only interactions with no touch equivalent — right-click menus, keyboard shortcuts (`HelpDialog`), drag-select, modifier-key selection — and states for each whether v1 needs an alternative or the feature is simply absent on Android. |
| **AC-6.3** | 🟡 SDK | Every hover-reveal affordance in scope for v1 is reachable by touch. `.undo-thumb-restore` specifically (app.css:977-984) is either always-shown or tap-to-reveal on Android; its existing `:focus-visible` path is preserved so desktop keyboard access does not regress. |
| **AC-6.4** | 🔴 DEVICE | Tap targets for primary actions meet the 48dp minimum on both devices. |
| **AC-6.5** | 🔴 DEVICE | **The scroll-windowing regression check.** A grid of ≥4,000 photos scrolls smoothly under real touch on both devices, in both directions, with no runaway to either end. `interop.js`'s geometry math and `.grid { overflow-anchor: none }` are the specific suspects (CLAUDE.md), touch momentum is the specific new input mode, and OEM WebView variance is the specific reason both devices are required. |

### E7 — Definition of done (plan §6, made checkable)

| ID | Gate | Criterion |
|---|---|---|
| **AC-7.1** | 🔴 DEVICE | Installs and runs on both the S23 Ultra and the Motorola. |
| **AC-7.2** | 🔴 DEVICE | Grants all-files access and scans a folder outside the system Photos library (AC-4.5). |
| **AC-7.3** | 🔴 DEVICE | Exact, variant and burst duplicate detection, blur detection and every filter produce results consistent with the same folder scanned on the dev Mac. Divergence in group membership is a fail, not a rounding difference. |
| **AC-7.4** | 🔴 DEVICE | NSFW scoring works, **or** is absent and explained through the existing `DependencyChecker` "optional sidecar, gracefully degrades" surface. Silently broken is a fail. |
| **AC-7.5** | 🔴 DEVICE | Delete-to-trash and restore round-trip correctly for both a whole batch and a single item from the `UndoDialog` thumbnail strip. |
| **AC-7.6** | 🔴 DEVICE | Catalog DB persists across an app restart (AC-5.4). |
| **AC-7.7** | 🟡 SDK | No export, Hide/Extract or DirectML path is reachable from the Android UI — verified by inspecting the rendered component tree, not by assuming the toolbar entry points were skipped. |
| **AC-7.8** | 🟢 NOW | `docs/ANDROID-VERIFY.md` exists, mirroring `WINDOWS-VERIFY.md`'s role: the per-device checklist for everything gated 🔴 DEVICE above, with a column per test device. |
| **AC-7.9** | 🟢 NOW | The safety invariants hold unchanged on Android: no destructive step precedes hash verification, and the source folder is untouched unless explicitly requested (CLAUDE.md §"Key invariants"). Restated in the Android context and confirmed by inspection of the trash implementation. |

---

## 3. Blocked vs. executable

| Epic | 🟢 NOW | 🟡 SDK | 🔴 DEVICE |
|---|---|---|---|
| E0 spike | — | 0.1, 0.2, 0.5 | 0.3, 0.4, 0.6 |
| E1 host | 1.1–1.5 | 1.6, 1.7 | — |
| E2 native deps | 2.1, 2.2, 2.7 ✅ | 2.6, 2.8, 2.9 | 2.3, 2.4, 2.5 |
| E3 platform services | 3.1–3.5 | 3.6, 3.7 | — |
| E4 storage/perms | — | 4.1, 4.3 | 4.2, 4.4, 4.5 |
| E5 paths | 5.1, 5.2 | — | 5.3, 5.4 |
| E6 touch | 6.1, 6.2 | 6.3 | 6.4, 6.5 |
| E7 DoD | 7.8, 7.9 | 7.7 | 7.1–7.6 |

**18 of 46 ACs are executable today.** They are also the ones that hold their value if the §0 spike
fails and the port re-scopes to `BlazorWebView` — the host extraction, the portable trash service,
the picker seam, the multi-targeting decision, and all three audits are prerequisites either way.

---

## 4. Parallel execution plan

Five independent workstreams, partitioned by **file ownership** so no two touch the same file.

| # | Workstream | ACs | Owns | Depends on |
|---|---|---|---|---|
| **W1** | Host composition extraction | 1.1–1.5 | `App/Program.cs`, new `App/SnapZapWebHost.cs`, `App/SnapZap.App.csproj`, `App/Services/AppHost.cs` | — |
| **W2** | Portable trash + link stub | 3.1–3.5 | new `Core/Delete/FolderTrashService.cs`, new `Core/Platform/PortableLinkStub.cs`, new `tests/FolderTrashTests.cs` | — |
| **W3** | Directory-picker platform seam | 5.1, 5.2 | `App/Components/DirectoryPickerDialog.razor`, new seam type, new `tests/DirectoryRootsTests.cs` | — |
| **W4** | Native dependency audit | 2.1, 2.2 | new `docs/ANDROID-DEPS-AUDIT.md` (docs only) | — |
| **W5** | Touch/hover audit + verify checklist | 6.1, 6.2, 7.8 | new `docs/ANDROID-TOUCH-AUDIT.md`, new `docs/ANDROID-VERIFY.md` (docs only) | — |

W1–W3 run in isolated git worktrees so their concurrent `dotnet build`/`dotnet test` runs do not
contend on shared `obj/`/`bin/`. W4 and W5 are read-and-write-docs only.

**Serialized after these land:** E0 (needs toolchain), E1's 1.6/1.7, E4, and everything 🔴 DEVICE.
