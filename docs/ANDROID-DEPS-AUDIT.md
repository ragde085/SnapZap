# Android dependency audit (W4 / AC-2.1, AC-2.2)

**Scope:** every native-backed `PackageReference` in `src/SnapZap.Core/SnapZap.Core.csproj`, plus
`Photino.NET` in `src/SnapZap.App/SnapZap.App.csproj` (for context on why it can't be reused for an
Android head). Written against the versions actually pinned in this repo as of this audit:

```
MetadataExtractor                    2.9.3
Microsoft.Data.Sqlite                10.0.10
Microsoft.ML.OnnxRuntime              1.27.1
SkiaSharp                            4.150.1
SkiaSharp.NativeAssets.macOS         4.150.1
SkiaSharp.NativeAssets.Win32         4.150.1
SQLitePCLRaw.bundle_e_sqlite3        3.0.4
Photino.NET (App only)               4.0.16
```

## Method — read this before trusting any verdict below

**This environment has no Android SDK, no `dotnet workload install android`, and no `adb`.**
Nothing in this document was confirmed by an actual `dotnet restore`/`dotnet build -f
net10.0-android` — that is exactly the device/SDK-gated work `docs/ANDROID-PORT-ACS.md` assigns to
AC-2.3–AC-2.6, not this doc.

What *was* done, in three tiers of decreasing confidence:

1. **Package-inspection-verified** (strongest available without an SDK): the actual `.nupkg` files
   for the pinned versions were downloaded from `api.nuget.org`'s flat-container endpoint and their
   `.nuspec` dependency graphs and file listings were inspected directly with `unzip -l` — real
   package contents, not a rendered web page. Used for SkiaSharp, SkiaSharp.NativeAssets.Android,
   SQLitePCLRaw's dependency chain (SourceGear.sqlite3), Microsoft.ML.OnnxRuntime, MetadataExtractor,
   Photino.NET, and Microsoft.Data.Sqlite. This tells us what NuGet *would* resolve and what files
   *would* land in an output folder — it does not tell us the app launches, loads the native
   library, or produces correct results on-device.
2. **Documentation/community-sourced**: NuGet package pages, Microsoft Learn, official docs,
   GitHub issues/discussions. Used for API-level defaults, known bugs, and license text where the
   nuspec's `<license>` element was sufficient on its own.
3. **Inference from (1) and (2)**: e.g. "no `net10.0-android` fallback framework group exists in
   this nuspec, therefore adding this package to a plain `net10.0` project would fail to restore."
   This is standard, well-documented NuGet TFM-compatibility behavior (cited below), not a guess,
   but it is still **unverified by an actual restore** and flagged as such.

Every claim below is tagged `[VERIFIED IN PACKAGE]`, `[DOCUMENTED]`, or `[INFERRED — NOT
BUILD-TESTED]`. Treat the third tier as the reason AC-2.3–AC-2.6's on-device spikes still have to
happen — this document reduces risk, it does not retire it.

---

## Verdict table

| # | Dependency | Verdict | One-line reason |
|---|---|---|---|
| 1 | **SkiaSharp** + native assets | 🟡 **NEEDS PACKAGE ADDED** (+ `SnapZap.Core` must multi-target) | `SkiaSharp.NativeAssets.Android` 4.150.1 exists, is MIT, and ships real `.so` files for all 4 ABIs — but it's gated to `net9.0-android35.0`/`net10.0-android36.0` TFMs only. `SnapZap.Core` is single-targeted at plain `net10.0` today, so the fix is multi-targeting Core, not just adding a package reference. |
| 2 | **Microsoft.ML.OnnxRuntime** | 🔴 **RISKY — SPIKE REQUIRED** | Package formally supports `net9.0-android35.0`. But an **open** GitHub issue (microsoft/onnxruntime#29270, filed 2026-06-26) reproduces the *exact* SnapZap topology — a plain `net10.0` library referencing ONNX Runtime, consumed via `ProjectReference` by a `net10.0-android` head, built from a **macOS host** — and gets the wrong (Linux/glibc) native library bundled, which crashes on launch. A maintainer could not reproduce on a newer SDK on Windows. Inconclusive for SnapZap's actual (macOS-hosted) dev setup. This supersedes the older #11618 the port plan cites, which is closed and stale (2022, pre-.NET-for-Android). |
| 3 | **SQLitePCLRaw.bundle_e_sqlite3** | 🟢 **SAFE** | `[VERIFIED IN PACKAGE]` The exact pinned version (3.0.4) depends on `SourceGear.sqlite3` 3.53.3, whose `.nupkg` contains real `libe_sqlite3.so` for all 4 Android RIDs (`android-arm`, `android-arm64`, `android-x64`, `android-x86`), delivered under a generic `.NETStandard2.0` dependency group — **no Android-specific TFM required**. Resolves as-is from Core's current plain `net10.0`. Apache-2.0. |
| 4 | **Microsoft.Data.Sqlite** | 🟢 **SAFE** | Pure managed ADO.NET wrapper, `.NETStandard2.0`-targeted (no platform gating). All Android capability comes from #3 above. MIT. |
| 5 | **MetadataExtractor** | 🟢 **SAFE** | `[VERIFIED IN PACKAGE]` Pure managed — the `.nupkg` contains only `lib/net8.0`, `lib/netstandard2.0`, `lib/netstandard2.1` DLLs, zero native files. Apache-2.0. One caveat: transitive dependency `XmpCore` is **not** MIT/Apache — see §5. |
| 6 | **Photino.NET** (`SnapZap.App`, not Core) | ⚫ **BLOCKER** (for reusing `SnapZap.App` as-is) | `[VERIFIED IN PACKAGE]` Desktop-only by construction — nuspec dependency groups list only `net8.0`/`net9.0`, no Android group, no Android native asset anywhere in the package. Confirms `docs/ANDROID-PORT-PLAN.md` §2's own conclusion: Android needs a new `IAppHost` implementation, full stop, not a Photino variant. |

---

## 1. SkiaSharp + native assets

**Highest priority per the task brief** — SkiaSharp backs decode, thumbnailing, EXIF geometry,
blur scoring and the perceptual hash (`docs/ANDROID-PORT-PLAN.md` doesn't mention it at all; this
is the gap this audit exists to close).

### Does an Android package exist at/compatible with the pinned version?

Yes. `SkiaSharp.NativeAssets.Android` **4.150.1** exists on NuGet — the identical version number
to the pinned `SkiaSharp` 4.150.1, which is the expected/supported pairing for this package family.
`[VERIFIED IN PACKAGE]` — downloaded `skiasharp.nativeassets.android.4.150.1.nupkg` directly from
`api.nuget.org/v3-flatcontainer` and inspected its nuspec and file list:

```xml
<!-- SkiaSharp.NativeAssets.Android.nuspec, abridged -->
<license type="expression">MIT</license>
<dependencies>
  <group targetFramework="net9.0-android35.0" />
  <group targetFramework="net10.0-android36.0" />
</dependencies>
```

```
runtimes/android-arm64/native/libSkiaSharp.so     (9.4 MB)
runtimes/android-x64/native/libSkiaSharp.so       (10.2 MB)
runtimes/android-x86/native/libSkiaSharp.so       (11.4 MB)
runtimes/android-arm/native/libSkiaSharp.so       (6.3 MB)
buildTransitive/net9.0-android35.0/SkiaSharp.NativeAssets.Android.targets
buildTransitive/net10.0-android36.0/SkiaSharp.NativeAssets.Android.targets
```

### Exact package id and version to add

`SkiaSharp.NativeAssets.Android`, version **4.150.1** (match the pinned `SkiaSharp` version —
this family is versioned in lockstep; nothing suggests otherwise and no other version was tested).

**Important nuance, `[VERIFIED IN PACKAGE]`:** you likely don't need to add this reference
explicitly at all. The bare `SkiaSharp` 4.150.1 meta-package (already referenced in
`SnapZap.Core.csproj`) has its own **per-TFM** dependency graph:

```xml
<!-- SkiaSharp.nuspec, abridged -->
<group targetFramework="net10.0">
  <dependency id="SkiaSharp.NativeAssets.macOS" version="4.150.1" />
  <dependency id="SkiaSharp.NativeAssets.Win32" version="4.150.1" />
</group>
<group targetFramework="net10.0-android36.0">
  <dependency id="SkiaSharp.NativeAssets.Android" version="4.150.1" />
</group>
```

This is exactly why the current `net10.0` build already pulls in macOS+Win32 native assets from
the bare `SkiaSharp` reference alone (redundant with the explicit macOS/Win32 lines already in
`SnapZap.Core.csproj`, but harmless). If `SnapZap.Core` multi-targets to include
`net10.0-android36.0` (or whatever TFM the Android SDK resolves to at implementation time), the
`net10.0-android36.0` leg of the *existing* `SkiaSharp` reference will automatically pull in
`SkiaSharp.NativeAssets.Android` with **no new `<PackageReference>` needed** — though pinning it
explicitly matches this repo's existing style (explicit macOS/Win32 lines) and is the safer,
more auditable choice.

### Supported ABIs

`[VERIFIED IN PACKAGE]` All four: `arm64-v8a` (`android-arm64`), `armeabi-v7a` (`android-arm`),
`x86` (`android-x86`), `x86_64` (`android-x64`). Both test devices (Galaxy S23 Ultra, mid-range
Motorola) are `arm64-v8a`; an x86_64 emulator is also covered.

### Minimum Android API level vs. the plan's API 30+

`[DOCUMENTED]` The package's TFM (`net9.0-android35.0`/`net10.0-android36.0`) pins which Android
API *surface* it's compiled against, not the floor an app can run on — that's a separate property,
`SupportedOSPlatformVersion` (maps to `android:minSdkVersion`), set by the *consuming app project*.
Per Microsoft's own breaking-change note, .NET 9/10-for-Android templates default
`SupportedOSPlatformVersion` to **24** (Android 7.0), with an opt-in floor of **21** — both well
under the plan's API 30+ target, so there's no floor conflict.
Source: [Breaking change: Minimum Android API level raised to 24 — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/compatibility/maui/11/android-minimum-api-level).

### License

MIT (`[VERIFIED IN PACKAGE]`, nuspec `<license type="expression">MIT</license>`) — same as the
existing `SkiaSharp`/`SkiaSharp.NativeAssets.macOS`/`.Win32` already in the project. Compatible
with CLAUDE.md's MIT/Apache-2.0-only rule.

### Known issues on .NET Android targets

`[DOCUMENTED]` One current, relevant one, unrelated to SkiaSharp's own correctness: Android 15/16's
16 KB memory page size requirement broke some `.so` builds that assumed 4 KB pages (warning
`XA0141` in `.NET` MAUI tooling). SkiaSharp's Android native assets are current (built 2026-07-14
per the nupkg timestamp) and this is a build-time warning class, not a report against
`libSkiaSharp.so` specifically — flagged for awareness, not as a blocker.
Source: [Preparing Your Avalonia Apps for Android's 16 KB Page Size Requirement](https://avaloniaui.net/blog/preparing-your-avalonia-apps-for-android-s-16-kb-page-size-requirement) (general
16 KB page-size context; SkiaSharp itself was not reported broken by this in any source found).

No SkiaSharp-Android-specific open bug was found that plausibly affects this app's usage
(`SkiaImageService`'s decode/thumbnail/geometry calls are exactly the SkiaSharp mainstream path
every mobile SkiaSharp consumer exercises).

---

## 2. Microsoft.ML.OnnxRuntime — the important finding

### Does an Android package exist at/compatible with the pinned version?

Yes, formally. `[VERIFIED IN PACKAGE]` `Microsoft.ML.OnnxRuntime` 1.27.1's nuspec declares:

```xml
<group targetFramework="net9.0-android35.0">
  <dependency id="Microsoft.ML.OnnxRuntime.Managed" version="1.27.1" />
</group>
```

and the downloaded `.nupkg` (135 MB) contains:

```
runtimes/android/native/onnxruntime.aar          (44.5 MB)
runtimes/linux-arm64/native/libonnxruntime.so     (20.0 MB)
runtimes/linux-x64/native/libonnxruntime.so       (23.7 MB)
build/net9.0-android35.0/Microsoft.ML.OnnxRuntime.targets
buildTransitive/net9.0-android35.0/Microsoft.ML.OnnxRuntime.targets
```

No new package id is needed — `Microsoft.ML.OnnxRuntime` 1.27.1 already carries the Android AAR.
This is the correct, current-generation .NET-for-Android delivery mechanism (an AAR, not a plain
`runtimes/android-arm64/native/*.so` RID folder like SkiaSharp/SQLite use) — ONNX Runtime needs
JNI glue the AAR provides.

### The `#11618` the port plan cites is stale — the risk moved

`docs/ANDROID-PORT-PLAN.md` §2 cites
[microsoft/onnxruntime#11618](https://github.com/microsoft/onnxruntime/issues/11618) as the
documented risk. `[VERIFIED VIA `gh api`]`: **that issue is closed** (closed 2022-07-25, fixed in
the 1.12 release) and was about Xamarin.Android-era package targeting (`MonoAndroid11.0`), a
problem that predates .NET-for-Android entirely. Citing it as the live risk understates what's
actually current.

**The real, current risk is a different, open issue:**
[microsoft/onnxruntime#29270](https://github.com/microsoft/onnxruntime/issues/29270) — "Android:
Linux-targeted libonnxruntime.so (from runtimes/linux-arm64) gets bundled into APK instead of the
Android AAR's library." `[VERIFIED VIA `gh api`]`:

- **Filed 2026-06-26, still open** as of this audit (last activity 2026-07-07).
- The reporter's topology is, structurally, **SnapZap's own architecture**: *"`Microsoft.ML.OnnxRuntime` ... referenced via `<PackageReference>` in a shared class library (plain `net10.0`, no
  platform TFM), consumed by the Android app project via `<ProjectReference>`"* — this is exactly
  `SnapZap.Core` (plain `net10.0`) being referenced by a future Android head project, which is
  precisely what `docs/ANDROID-PORT-PLAN.md` §4 task 2 proposes.
- Host was **macOS (Apple Silicon)**, .NET SDK 10.0.203 — **SnapZap is developed on macOS**
  (CLAUDE.md's first line). Reproduced identically across three separate ORT version pairs
  (1.22.0, 1.23.0, 1.24.1), ruling out a one-off regression.
- Symptom: the wrong native library — the desktop Linux/glibc build from
  `runtimes/linux-arm64/native/` — gets bundled into the APK instead of the Android/Bionic build
  from the AAR. It depends on glibc-only symbols (`libdl.so.2`, `librt.so.1`, `libstdc++.so.6`,
  `ld-linux-aarch64.so.1`) absent on Android, so the app **crashes on launch** with
  `UnsatisfiedLinkError` → native abort in `Ort::InitApi()`. This is a load-time crash, not a
  subtle inference bug — it would be caught immediately at the ONNX spike (plan task 7 /
  `docs/ANDROID-PORT-ACS.md` AC-2.4), not silently shipped.
- **Workaround posted by a community member** (comment, 2026-07-01): a custom MSBuild target that
  strips `linux-*` native assets from `NativeCopyLocalItems`/`ResolvedFileToPublish` when
  `$(TargetFramework)` contains `-android`, forcing the correct AAR-sourced library through.
  **Second workaround suggested by an ONNX Runtime maintainer** (skottmckay): explicitly set the
  *class library's* `TargetFramework` to `net9.0-android` (not just the app head) so the package's
  `targets/net9.0-android` config path is used.
- **Most recent comment (2026-07-07, from the same maintainer)**: could **not** reproduce on
  .NET SDK 10.0.301 / Windows host / ORT 1.27.0, in either topology (direct `PackageReference` in
  the app head, or the reporter's plain-library-plus-`ProjectReference` topology) — ELF inspection
  of the resulting `.so` confirmed the correct Android/Bionic build was picked up. The maintainer's
  own words: *"Suspected difference from the original report: reporter was on SDK 10.0.203 /
  macOS. This points to a .NET-for-Android SDK fix between 10.0.203 and 10.0.301 or a
  macOS-host-specific factor."*

**Read literally, this is inconclusive for SnapZap specifically**: the bug is confirmed reproducible
on a macOS host at one SDK version and not reproducible on a Windows host at a later SDK version —
nobody in the thread has confirmed or denied it on a **macOS** host at the later SDK. Since SnapZap
is developed on macOS, this is the exact combination nobody has cleared. It may already be fixed by
whatever SDK version is installed when the port actually starts, or it may not be — the port plan's
own §4 task 7 (parallel ONNX spike) is the only way to know, and per plan and
`docs/ANDROID-PORT-ACS.md` AC-2.6, an ONNX failure degrades gracefully (NSFW scoring off) rather
than blocking the port, so `RISKY — SPIKE REQUIRED` rather than `BLOCKER` is the correct verdict —
but the spike needs to specifically check for this failure mode (inspect the APK's bundled
`libonnxruntime.so` for glibc vs. Bionic symbols, not just "did it crash"), since the maintainer's
"could not reproduce" was itself based on exactly that ELF-level check, not just a smoke test.

### Supported ABIs / minimum API level

`[DOCUMENTED]` ONNX Runtime's Android build supports `arm64-v8a` (default) and `armeabi-v7a`; CPU
inference (no NNAPI) works down to API 21, NNAPI execution provider needs API 24+ (26+/27+
recommended for full NNAPI feature coverage) — all under the plan's API 30+ floor, so no conflict.
`x86_64` support for the emulator case was not explicitly confirmed in official docs found, though
the AAR is a standard multi-ABI artifact and x86_64 is conventional for ORT's mobile builds; treat
as **unverified, not blocking** for the two physical test devices (both arm64).
Sources: [NNAPI Execution Provider — onnxruntime.ai](https://onnxruntime.ai/docs/execution-providers/NNAPI-ExecutionProvider.html), [Minimal SDK API version for android runtime — GitHub Discussion #6438](https://github.com/microsoft/onnxruntime/discussions/6438).

### License

MIT (nuspec references a `LICENSE` file; ONNX Runtime is MIT-licensed upstream, consistent with
CLAUDE.md's dependency notes).

---

## 3. SQLitePCLRaw.bundle_e_sqlite3 (and its dependents)

### Does an Android-supporting package exist at the pinned version?

Yes, and it's simpler than the other two: `[VERIFIED IN PACKAGE]` the pinned
`SQLitePCLRaw.bundle_e_sqlite3` **3.0.4**'s nuspec resolves to exactly:

```xml
<group targetFramework=".NETStandard2.0">
  <dependency id="SQLitePCLRaw.config.e_sqlite3" version="3.0.4" />
  <dependency id="SourceGear.sqlite3" version="3.53.3" />
</group>
```

`SourceGear.sqlite3` 3.53.3's `.nupkg` (39 MB) was downloaded and its file list inspected directly:

```
runtimes/android-arm/native/libe_sqlite3.so
runtimes/android-arm64/native/libe_sqlite3.so
runtimes/android-x64/native/libe_sqlite3.so
runtimes/android-x86/native/libe_sqlite3.so
```

— all four Android RIDs present, alongside `linux-*`, `osx-*`, `win-*`, `ios-*` and
`maccatalyst-*` builds in the same package. Its own nuspec dependency groups are just
`.NETFramework4.7.1` and `.NETStandard2.0` — **no platform-specific TFM gating at all**. This is
the RID-folder delivery model (like `SkiaSharp.NativeAssets.macOS`/`.Win32` already in this repo),
not the AAR/TFM-gated model SkiaSharp's Android package and ONNX Runtime's Android package use.
Practically: **no new package reference is needed anywhere** — the existing pinned
`SQLitePCLRaw.bundle_e_sqlite3` 3.0.4 already carries working Android native binaries and will
restore fine against `SnapZap.Core`'s current plain `net10.0` TFM as-is, before and after any
Android multi-targeting.

One related nuance worth recording: `SQLitePCLRaw.config.e_sqlite3` 3.0.4's own dependency graph
has **explicit, separate** groups for `net10.0-ios26.0`/`net10.0-tvos26.0` (pointing at
`SQLitePCLRaw.provider.internal`, a static-linking provider — those platforms don't allow dynamic
library loading) but **no explicit Android group** — Android falls through to the
`.NETStandard2.0` group's `SQLitePCLRaw.provider.e_sqlite3` (the normal dynamic-load provider),
which is correct since Android permits `dlopen()`. This is `[VERIFIED IN PACKAGE]` from the nuspec
and is a good sign the SQLitePCLRaw maintainers treat Android as "just another dynamic-load RID,"
not a special case needing extra plumbing.

### Android 15/16 16 KB page-size note

`[DOCUMENTED]` `SQLitePCLRaw.bundle_e_sqlite3` **2.1.10+** already carries the fix for Android's
16 KB memory page alignment requirement (`XA0141` warning class) — the pinned 3.0.4 is well above
that floor. Source: [Cezar Wagenheimer — SQLite vs. Android 16KB Page Sizes](https://wagenheimer.com/blog/sqlite-vs-android-16kb-page-sizes-fixing-warning-xa0141-in-net-maui).

### Supported ABIs / minimum API level

All four (`android-arm`, `android-arm64`, `android-x64`, `android-x86` = `armeabi-v7a`,
`arm64-v8a`, `x86_64`, `x86`), `[VERIFIED IN PACKAGE]`. No Android-specific minimum API level
documented for the native SQLite build itself — the floor is whatever the consuming app project's
`SupportedOSPlatformVersion` sets (see §1), not this package.

### License

Apache-2.0 (`SQLitePCLRaw.bundle_e_sqlite3`, `.config.e_sqlite3`, `.core` all `[VERIFIED IN
PACKAGE]` via `<license type="expression">Apache-2.0</license>`).

### Known issues

`[DOCUMENTED]` The common Android `DllNotFoundException: e_sqlite3` reports found in searches are
almost universally attributed (in Microsoft Q&A threads) to using the **wrong/legacy package**
(`Mono.Data.Sqlite`, or `SQLitePCLRaw.bundle_e_sqlite3` below 2.1.10) or a missing
`SQLitePCL.Batteries_V2.Init()` call at startup — not a defect in the current 3.0.4 bundle itself.
`SnapZap.Core` doesn't appear to call `Batteries_V2.Init()` explicitly today (not part of this
audit's scope to confirm/fix, but worth a note for whoever wires up the Android host — `[INFERRED
— NOT BUILD-TESTED]`, flag for the implementer to check `Data/` init code during the spike).

---

## 4. Microsoft.Data.Sqlite

`[VERIFIED IN PACKAGE]` nuspec has a single dependency group, `.NETStandard2.0`:

```xml
<dependency id="Microsoft.Data.Sqlite.Core" version="10.0.10" />
<dependency id="SQLitePCLRaw.bundle_e_sqlite3" version="2.1.11" />
<dependency id="SQLitePCLRaw.core" version="2.1.11" />
```

No platform gating — it's a plain ADO.NET wrapper (connections, commands, readers) over whatever
SQLitePCLRaw resolves natively, per CLAUDE.md's own description ("`Microsoft.Data.Sqlite`" is
listed as one of the five deps to audit, but structurally it has no native code of its own). Its
declared minimum `SQLitePCLRaw.bundle_e_sqlite3` (2.1.11) is satisfied by the repo's pinned 3.0.4
(NuGet takes the higher of a transitive minimum and a direct pin). **Verdict: SAFE, no new package
needed** — all Android risk for the SQLite layer lives in §3, not here.

License: MIT (`[VERIFIED IN PACKAGE]`).

---

## 5. MetadataExtractor

`[VERIFIED IN PACKAGE]` Confirm pure-managed claim directly: the downloaded 2.9.3 `.nupkg`
contains exactly:

```
lib/net8.0/MetadataExtractor.dll
lib/netstandard2.0/MetadataExtractor.dll
lib/netstandard2.1/MetadataExtractor.dll
```

— no `runtimes/`, no native files of any kind. A pure-managed IL library restores and runs
identically on any TFM/RID including `net10.0-android`, with **zero Android-specific work
required**. **Verdict: SAFE.**

License: Apache-2.0 (`[VERIFIED IN PACKAGE]`, nuspec `<license type="expression">Apache-2.0</license>`)
— compatible with CLAUDE.md's MIT/Apache-2.0-only rule.

**One flag, not previously called out anywhere in the port docs:** MetadataExtractor's own nuspec
declares a transitive dependency, `XmpCore` **6.1.10.1**, whose nuspec licenses under a
**non-SPDX, non-MIT/Apache Adobe EULA**:

```
<licenseUrl>https://www.adobe.com/devnet/xmp/library/eula-xmp-library-java.html</licenseUrl>
```

`[VERIFIED IN PACKAGE]`. This is a pre-existing condition of the desktop app today (it's already a
transitive dependency of the currently-shipping `net10.0` build, not something Android
introduces), so it's not new risk from this port — but CLAUDE.md's "no paid, no subscription-gated,
MIT/Apache-2.0-only" rule is stated as covering "all other deps," and this one is neither. **Flagged
for the project owner's own license review, independent of Android** — out of scope to resolve in
this audit, but not honest to omit given the task's stated bar ("license compatibility is a real
constraint here, not a formality").

---

## 6. Photino.NET (`SnapZap.App`, for context only — not in `SnapZap.Core`)

`docs/ANDROID-PORT-PLAN.md` already concludes Android needs a new `IAppHost`
(`AndroidWebViewAppHost`) rather than Photino, and this audit's package inspection **confirms
that's not optional**: `[VERIFIED IN PACKAGE]` `Photino.NET` 4.0.16's nuspec dependency groups are

```xml
<group targetFramework="net8.0"><dependency id="Photino.Native" version="4.0.22" /></group>
<group targetFramework="net9.0"><dependency id="Photino.Native" version="4.0.22" /></group>
```

— no Android group, no Android native asset in the package family at all. Corroborated by
`[DOCUMENTED]` search results: Photino's own FAQ states it "does not run on mobile devices and
only runs on Windows, Mac and Linux," with mobile only "on the roadmap to evaluate."
Source: [Photino Docs — Frequently Asked Questions](https://docs.tryphotino.io/Frequently-Asked-Questions).

**Implication for an Android head referencing `SnapZap.App`:** it can't, not as a single
cross-platform project. If a future Android head project needs `Program.cs`'s `WebApplication`
composition (per the port plan's §0 spike and §4 task 2), that composition needs to be extracted
into something the Android head can call *without* pulling in `Photino.NET` — e.g. the shared
builder method the plan already proposes extracting out of `Program.cs`, referenced by both the
existing `SnapZap.App` (which keeps its `Photino.NET` reference for Windows/macOS/Linux desktop)
and a new, separate Android head project that never references `Photino.NET` at all. This is
consistent with, and reinforces, the plan's own §2 guidance to keep Android-specific code in a
separate host project rather than bolting it onto `SnapZap.App`.

License: Apache-2.0 (`[VERIFIED IN PACKAGE]`) — moot for Android since it won't be referenced
there, but noted for completeness.

---

## AC-2.2 — does adding `SkiaSharp.NativeAssets.Android` affect the existing macOS/Win32 publish?

**Short answer: not if it's added correctly (via multi-targeting); yes/build-breaking if it's
added the naive way (as a bare, unconditional `<PackageReference>` on `SnapZap.Core`'s current
single `net10.0` TFM).**

Longer answer, `[VERIFIED IN PACKAGE]` + `[INFERRED — NOT BUILD-TESTED]` for the restore-failure
claim specifically:

- `SnapZap.Core.csproj` today is **single-targeted**: `<TargetFramework>net10.0</TargetFramework>`.
- `SkiaSharp.NativeAssets.Android` 4.150.1's nuspec declares dependency groups *only* for
  `net9.0-android35.0` and `net10.0-android36.0` — there is no `.NETStandard2.0`, no `net10.0`
  (plain), no catch-all group of any kind (contrast with `SQLitePCLRaw.bundle_e_sqlite3`'s generic
  `.NETStandard2.0` group in §3, or ONNX's `.NETCoreApp0.0`/`.NETStandard0.0` catch-all groups in
  §2).
- Per standard, documented NuGet target-framework compatibility rules
  ([Target Frameworks Reference — Microsoft Learn](https://learn.microsoft.com/en-us/nuget/reference/target-frameworks)),
  a package with **no compatible framework group** for the consuming project's TFM fails restore
  outright (`NU1201`/`NU1202`-class error), rather than silently contributing nothing. **If someone
  added a bare, unconditional `<PackageReference Include="SkiaSharp.NativeAssets.Android"
  Version="4.150.1" />` to `SnapZap.Core.csproj` as it stands today (still single-targeted at plain
  `net10.0`), the win-x64 and macOS builds would very likely fail to restore, not just bloat.**
  This specific restore-failure claim is `[INFERRED — NOT BUILD-TESTED]` — it follows directly from
  the nuspec's declared groups and documented NuGet behavior, but nobody ran `dotnet restore` in
  this environment to confirm the exact error code.
- **The correct fix is multi-targeting `SnapZap.Core`** — e.g.
  `<TargetFrameworks>net10.0;net10.0-android36.0</TargetFrameworks>` (exact Android TFM version to
  be confirmed against whatever Android workload version the implementation session installs) —
  with the `SkiaSharp.NativeAssets.Android` reference either left implicit (per §1, the bare
  `SkiaSharp` meta-package already carries it for the `net10.0-android36.0` leg) or pinned
  explicitly with `Condition="$(TargetFramework.Contains('android'))"`.
- **Under multi-targeting, the two TFM legs build and publish completely independently** — this is
  the entire point of multi-targeting, and it's exactly the mechanism already relied on
  (implicitly, for RIDs rather than TFMs) by the existing macOS/Win32 split: `dotnet publish -r
  win-x64` only pulls `runtimes/win-x64/native/*` assets into the publish folder, never
  `runtimes/osx-arm64/*`. The `net10.0-android36.0` leg's obj/bin/publish output — and everything
  that leg alone depends on, including `SkiaSharp.NativeAssets.Android` — is fully separate from
  the `net10.0` leg's output. **A correctly multi-targeted `SnapZap.Core` would add zero bytes and
  zero new assets to the existing win-x64/macOS publish outputs.** This part is a direct,
  well-established consequence of how SDK-style multi-targeting works and does not require an
  Android SDK to be confident about — it's the same mechanism the two existing platform TFM-less
  RID splits already rely on, just at the TFM level instead of the RID level.
- This also matters for `Microsoft.ML.OnnxRuntime` (§2) and would matter for
  `SQLitePCLRaw.bundle_e_sqlite3` (§3) *if* it were ever TFM-gated the same way — it currently
  isn't (its Android delivery is RID-folder-based under a generic `.NETStandard2.0` group), so it's
  the one dependency in this audit that needs **no** multi-targeting to work on Android; it already
  restores under the single `net10.0` TFM today, before any Android-specific project changes.

---

## What to add to which `.csproj` — a proposal, not an applied change

**Nothing in this repo was modified by this audit.** This section is what a later implementation
session should do, to be validated against whatever the §0 spike in `docs/ANDROID-PORT-PLAN.md`
concludes about project shape (bare Android head vs. MAUI head).

### `src/SnapZap.Core/SnapZap.Core.csproj`

1. Multi-target: change
   `<TargetFramework>net10.0</TargetFramework>` to
   `<TargetFrameworks>net10.0;net10.0-android36.0</TargetFrameworks>` (confirm the exact
   `-android` platform-version suffix against the installed Android workload at implementation
   time — `35.0` vs `36.0` vs whatever is current then).
2. No change needed for `MetadataExtractor`, `Microsoft.Data.Sqlite`,
   `SQLitePCLRaw.bundle_e_sqlite3` — all three already resolve correctly for both TFM legs as
   pinned (§3, §4, §5).
3. `Microsoft.ML.OnnxRuntime` — no version bump needed to *get* Android support (1.27.1 already
   ships it), but this is the dependency to prioritize spiking (§2) before assuming NSFW-on-Android
   works, specifically checking for microsoft/onnxruntime#29270's failure mode on a macOS dev host.
4. Add (or leave implicit per §1)
   `<PackageReference Include="SkiaSharp.NativeAssets.Android" Version="4.150.1"
   Condition="$(TargetFramework.Contains('android'))" />` — explicit pin matches this repo's
   existing style of listing macOS/Win32 explicitly rather than relying on `SkiaSharp`'s own
   transitive graph silently.
5. Leave the existing unconditional `SkiaSharp.NativeAssets.macOS`/`SkiaSharp.NativeAssets.Win32`
   references exactly as they are — per AC-2.2's analysis, they contribute nothing to the
   `net10.0-android36.0` leg's restore (no compatible group for that TFM either, symmetric to the
   Android package's situation on the plain `net10.0` leg) and cost nothing extra there. Confirm
   this doesn't itself throw a restore error the same way §AC-2.2 worries about for the reverse
   case — `[INFERRED — NOT BUILD-TESTED]`, worth a specific check during the spike since it's the
   mirror image of the exact failure mode this audit flags.

### New Android host project (`src/SnapZap.Android` or similar, per plan §4 task 2)

- References `SnapZap.Core` (the `net10.0-android36.0` leg) and, per the plan, the shared
  `WebApplication`-building method extracted out of `SnapZap.App/Program.cs` — **not**
  `SnapZap.App` wholesale, since `SnapZap.App.csproj` carries `Photino.NET` (§6), which has no
  Android asset and would fail the same way an unconditional Android-only package would fail on a
  desktop-only TFM.
- Owns `AndroidTrashService`, `AndroidLinkService`, `AndroidWebViewAppHost` (plan §2) — none of
  these have NuGet dependency concerns; they're plain C# against the Android SDK's `Context`/
  `Android.Webkit` types, out of scope for this dependency audit.

### `src/SnapZap.App/SnapZap.App.csproj`

No changes needed or proposed — it stays desktop-only (`Photino.NET` + browser fallback), exactly
as it is today.

---

## Summary for the implementer

- **SkiaSharp is the one the existing port plan missed, and it needs the most structural work**:
  not a missing package (one exists, is MIT, ships all 4 ABIs) but a missing *shape* —
  `SnapZap.Core` has to become multi-targeted before an Android-specific native asset package can
  be referenced at all without breaking the existing win-x64/macOS restore.
- **ONNX Runtime's real current risk is a different, open, more specific bug** than the one the
  port plan cites (#29270, not #11618) — and it reproduces on exactly SnapZap's own topology
  (plain-`net10.0`-library + `ProjectReference` + macOS host), with the only "doesn't reproduce"
  report coming from a different host OS and a newer SDK, i.e. not yet cleared for SnapZap's actual
  setup.
- **SQLite is the one dependency that needs nothing at all** — already Android-capable at the
  exact pinned version, via a RID-folder mechanism that doesn't require Core to multi-target.
- **MetadataExtractor is clean**, with one pre-existing (not Android-caused) license flag on its
  transitive `XmpCore` dependency worth a separate look.
- **Photino.NET confirms, rather than surprises**: the port plan's own decision to write a new
  `IAppHost` for Android was already correct; this audit adds the mechanical detail (no Android
  asset exists in the package family at all, so there's no lighter-weight alternative to consider).

None of this replaces AC-2.3–AC-2.6's on-device spikes. It should make them faster and better
targeted: the SkiaSharp spike now knows exactly which csproj change to make first; the ONNX spike
now knows the specific failure signature to check for (ELF symbol inspection of the bundled
`libonnxruntime.so`, not just "did NSFW scoring crash").
