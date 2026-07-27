---
title: 'Hide Photos in Image (Steganographic Encrypt/Decrypt)'
slug: 'image-steganography-hide-encrypt'
created: '2026-07-26'
status: 'Completed'
stepsCompleted: [1, 2, 3, 4]
tech_stack:
  - '.NET 10 / C# (nullable enabled), both SnapZap.Core.csproj and SnapZap.App.csproj target net10.0'
  - 'ASP.NET Core + Blazor Server (interactive server components, no HTTP/JSON layer for app logic)'
  - 'SkiaSharp 4.150.1 (SKBitmap/SKCanvas pixel access — ImageSharp forbidden, licence trap)'
  - 'System.Security.Cryptography (AesGcm, Rfc2898DeriveBytes/PBKDF2) — BCL, no new package needed'
  - 'System.IO.Compression (ZipArchive) — BCL, no new package needed'
  - 'xUnit 2.9.3 + Xunit.SkippableFact 1.5.61 + coverlet.collector 6.0.4 (tests/SnapZap.Tests)'
files_to_modify:
  - 'src/SnapZap.Core/Stego/HideProgress.cs (new)'
  - 'src/SnapZap.Core/Stego/ExtractProgress.cs (new)'
  - 'src/SnapZap.Core/Stego/HideResult.cs (new)'
  - 'src/SnapZap.Core/Stego/ExtractResult.cs (new)'
  - 'src/SnapZap.Core/Stego/StegoException.cs (new)'
  - 'src/SnapZap.Core/Stego/PayloadCrypto.cs (new — AES-GCM + PBKDF2 passphrase KDF)'
  - 'src/SnapZap.Core/Stego/PayloadZipper.cs (new — ZipArchive wrapper for selected photos)'
  - 'src/SnapZap.Core/Stego/StegoEngine.cs (new — LSB embed/extract over SKBitmap pixel data)'
  - 'tests/SnapZap.Tests/PayloadCryptoTests.cs (new)'
  - 'tests/SnapZap.Tests/StegoTests.cs (new)'
  - 'tests/SnapZap.Tests/PayloadZipperTests.cs (new)'
  - 'src/SnapZap.App/Components/HideDialog.razor (new — clone of ExportDialog structure)'
  - 'src/SnapZap.App/Components/ExtractDialog.razor (new — clone of ExportDialog structure, library-independent)'
  - 'src/SnapZap.App/Components/SelectionBar.razor (add "Hide in Image" action + OnOpenHide EventCallback)'
  - 'src/SnapZap.App/Components/Toolbar.razor (add "Extract Hidden Images" action, own path input)'
  - 'src/SnapZap.App/Components/Pages/Home.razor (add _showHide/_showExtract fields, wire dialogs)'
  - 'CLAUDE.md (document Stego/ subdirectory and new dialogs)'
  - 'docs/DESIGN.md (document SNZ1 wire format, capacity formula, lossy-transport non-goal)'
code_patterns:
  - 'Dialog overlay: [Parameter] bool Show + EventCallback OnClose, root <div class="dlg" role="dialog" aria-modal="true"> gated by @if(Show), focus trap via _needsFocus/OnAfterRenderAsync, Escape via HandleKeyDown (ExportDialog.razor:1-9, SetupDialog.razor:23)'
  - 'Action-dispatch: no command bus — SelectionBar exposes EventCallback params (e.g. OnOpenExport, declared SelectionBar.razor:51) invoked on click (SelectionBar.razor:28), wired by parent Home.razor:130; simple ops call AppState methods directly from code-behind (Toolbar.razor:107-109)'
  - 'Path input: NO native OS file/folder picker anywhere in the app — plain <input type="text" @bind @bind:event="oninput"> for absolute paths, validated server-side via Path.IsPathRooted (ExportDialog.razor:15-17,193-199); Hide/Extract carrier and output paths must follow this same text-input convention'
  - 'Progress reporting: dialog-scoped IProgress<T> + own EtaEstimator + own CancellationTokenSource + inFlight guard against late-post races (ExportDialog.razor:245-257) — used instead of AppState.RunAsync (RunAsync itself defined at AppState.cs:998; simple ops call it directly like AppState.cs:893) because Hide/Extract need preflight/confirm UI like Export does'
  - 'Core engine construction: engines are NOT DI-registered — constructed per-call with dependencies passed in, e.g. new ExportEngine(Catalog.Db, Links, Trash) inside dialog code-behind (ExportDialog.razor:211,240); only genuinely cross-cutting singletons (CatalogService, DependencyChecker, SessionStore) plus per-OS platform singletons (ITrashService, ILinkService) get AddSingleton in Program.cs:44-47,55-61 — AppState is AddScoped, not AddSingleton'
  - 'Pixel access: SkiaImageService.cs pattern — MemoryMarshal.Cast<byte,uint>(bitmap.GetPixelSpan()) for raw pixel read/write (SkiaImageService.cs:183,256), never ImageSharp. CAUTION: the only decode entry point, DecodeScaled(path, maxEdge) (SkiaImageService.cs:71-89), always downsamples to maxEdge and always decodes as SKAlphaType.Premul (SkiaImageService.cs:82) — every existing call site uses a small maxEdge (320/512/1600) for thumbnails/hashing/previews. Stego carrier decode must NOT reuse those call patterns as-is (see Wire Format and Task 5 notes below).'
  - 'Collision-safe naming: lives in DestinationPlanner.Resolve(ImageRecord) (src/SnapZap.Core/Export/DestinationPlanner.cs:44-67), not in ExportEngine.cs directly — it is coupled to ImageRecord + content-hash comparison (exists/hashOf probes) and produces "name (n).ext" alternates, skipping a slot if it already holds identical content. This is NOT a generic filename utility PayloadZipper can call as-is (no ImageRecord/content-hash available for arbitrary zip entries) — replicate the same "name (n).ext" strategy locally in PayloadZipper instead of attempting direct reuse.'
test_patterns:
  - 'xUnit [Fact]/[Theory]; Xunit.SkippableFact for platform-gated tests'
  - 'One test class per feature area, PascalCase + Tests suffix (ExportTests, DedupTests, AnalysisTests) — new: StegoTests, PayloadCryptoTests'
  - 'IDisposable test class with per-test temp dir (Path.Combine(Path.GetTempPath(), "pc_stego_" + Guid.NewGuid().ToString("N"))), cleaned up in Dispose() (ExportTests.cs:12-45)'
  - 'PNG fixtures built in-test via SKBitmap/SKCanvas/SKImage.Encode, never committed binary fixtures'
  - 'Golden-value / round-trip style: byte-identical embed-then-extract round trip (mirrors GoldenValueTests.cs), plus a safety-invariant test that a wrong passphrase fails cleanly without corrupting output (mirrors ExportTests "Move_that_fails_verification_leaves_source_intact")'
---

# Tech-Spec: Hide Photos in Image (Steganographic Encrypt/Decrypt)

**Created:** 2026-07-26

> **⚠ SUPERSEDED — read this before anything else below.** This document describes the
> *original* design: LSB pixel-embedding, PNG-only, a fixed pixel-capacity ceiling, a
> required passphrase. **None of that is what shipped.** The implementation was changed
> mid-build, at explicit user request, to file concatenation (the classic
> `copy /b photo.jpg + archive.zip output.jpg` technique) — any image format, no capacity
> ceiling, optional passphrase. The rest of this file is kept as the historical record of
> the original design's rationale and adversarial review, **not as documentation of the
> current implementation.** For what actually shipped, see `docs/DESIGN.md` §13 and the
> "Post-Implementation Addendum" section at the very end of this file.

## Overview

### Problem Statement

Users want a way to conceal a set of selected photos inside an ordinary-looking carrier
image, protected by a passphrase, so a casual viewer only ever sees the carrier image —
never that anything is hidden inside it, let alone what.

### Solution

Add a new `SnapZap.Core.Steganography` service that: zips the selected photos in memory,
AES-encrypts the zip bytes with a user-supplied passphrase, then LSB-embeds the ciphertext
into a PNG carrier image's pixel data (least-significant-bit encoding — imperceptible to the
eye, recoverable byte-for-byte only by someone who knows to look and has the passphrase).

Two new UI entry points, reusing existing SnapZap overlay/action patterns:

- **"Hide in Image"** — new action in `SelectionBar` (alongside Export/Delete), shown when
  one or more photos are selected. Opens a dialog (same overlay pattern as `ExportDialog`) to
  pick a PNG carrier, enter a passphrase, and confirm — after a capacity pre-check.
- **"Extract Hidden Images"** — new action on the main `Toolbar`, independent of the scanned
  library (the carrier image a user received may never have been scanned). Opens a file
  picker for any PNG, prompts for the passphrase, decrypts, unzips, and writes the recovered
  photos to a chosen output folder.

### Scope

**In Scope:**
- Hide flow: select photos → pick PNG carrier → capacity pre-check → passphrase entry →
  zip → AES-encrypt → LSB-embed → save output PNG.
- Extract flow: pick any PNG from disk → passphrase entry → LSB-extract → AES-decrypt →
  unzip → save recovered photos to a chosen folder.
- Capacity pre-check: compute required carrier capacity from the zipped+encrypted payload
  size before allowing embed; block with a clear error if the chosen carrier is too small.
- PNG-only enforcement for carrier and output (LSB data does not survive lossy
  recompression); non-PNG carriers are rejected or converted via `SkiaImageService`.
- Passphrase-based AES encryption/decryption of the payload.
- Error handling: wrong passphrase, corrupted/non-hidden carrier, insufficient capacity.

**Out of Scope:**
- DCT/frequency-domain or JPEG-tolerant steganography techniques.
- Cloud upload, sharing, or transmission of the carrier image.
- Splitting one payload across multiple carrier images.
- Passphrase recovery/reset (a lost passphrase means the hidden data is unrecoverable).

## Context for Development

### Codebase Patterns

- **Confirmed clean slate.** No existing zip, encryption, or steganography code anywhere in
  `src/` or `tests/`. A grep for `AesGcm|Aes\.Create|ZipFile|System.IO.Compression|
  Steganography|LSB|BitPlane` hit only build artifacts (`obj/`) plus one false positive —
  `VariantFinder.cs:271` uses "LSB" to mean least-significant-*bit* in the perceptual-hash
  bit-indexing scheme, unrelated to steganography. No `project-context.md` exists in the repo.
- `SnapZap.Core/Scanning/Hasher.cs` is the existing precedent for
  `System.Security.Cryptography` usage (currently SHA-256 only); the new AES-GCM logic
  should live alongside it in spirit (isolated behind a single type, swappable, no native
  deps).
- `SnapZap.Core/Imaging/SkiaImageService.cs` is the only sanctioned path to raw pixel
  data/decoding — **never use ImageSharp** (Six Labors split license trap). Pixel read/write
  for LSB embedding must use the same `MemoryMarshal.Cast<byte,uint>(bitmap.GetPixelSpan())`
  pattern (`SkiaImageService.cs:183,256`). **Caution:** the only decode entry point,
  `DecodeScaled(path, maxEdge)` (`SkiaImageService.cs:71-89`), always downsamples to `maxEdge`
  (every existing call site uses a small value — 320/512/1600 for thumbnails/hashing/previews)
  and always decodes as `SKAlphaType.Premul` (`SkiaImageService.cs:82`). Neither behavior is
  safe to inherit unmodified for stego — see the Wire Format section and Task 5.
- `SelectionBar` is the contextual-action pattern (Export/Delete appear only while photos
  are selected): it exposes `EventCallback` params like `OnOpenExport`
  (declared `SelectionBar.razor:51`, invoked on click at `SelectionBar.razor:28`), wired by
  the parent (`Home.razor:130`) — "Hide in Image" adds a new `OnOpenHide` callback the same way.
- `Toolbar` is the reference pattern for a library-independent action; simple ops call
  `AppState` methods directly from code-behind (`Toolbar.razor:107-109`,
  e.g. `Task Dedup() => AppState.DedupAsync();`). "Extract Hidden Images" follows this —
  it must work without the target PNG being part of the scanned catalog.
- **No native OS file/folder picker exists anywhere in the app** (confirmed: zero hits for
  `webkitdirectory`/`OpenFileDialog`/`showDirectoryPicker`). Both scan-folder
  (`Toolbar.razor:6-13`) and export-destination (`ExportDialog.razor:15-17`) use a plain
  `<input type="text" @bind @bind:event="oninput">` for an absolute path, validated
  server-side with `Path.IsPathRooted` (`ExportDialog.razor:193-199`). The carrier-image path
  (Hide) and target-PNG path (Extract) must use this same text-input convention, not a picker.
- **Progress reporting** has two established variants: (1) `AppState.RunAsync`
  (defined `AppState.cs:998`, doc comment from `:993`) centralizes
  `Busy`/`BusyLabel`/`BusyDone`/`BusyTotal`/`BusyEta` for simple operations, called directly
  by e.g. `ScanAsync` (`AppState.cs:893`) and `DedupAsync` (`AppState.cs:933`). (2)
  Dialog-scoped, self-contained progress — `ExportDialog.razor:245-257` builds its own
  `EtaEstimator` + `Progress<ExportProgress>` + `CancellationTokenSource` + an `inFlight`
  guard against late-post races, because Export needs preflight/confirm UI that `RunAsync`
  doesn't model. Hide/Extract are dialog-driven flows like Export, so they should copy variant
  (2) exactly, reporting `IProgress<HideProgress>` / `IProgress<ExtractProgress>` from
  `SnapZap.Core`.
- **Core engines are not DI-registered.** `Program.cs:44-47` registers `CatalogService`,
  `DependencyChecker`, `SessionStore` as singletons and `AppState` as scoped;
  `Program.cs:55-61` additionally registers per-OS platform singletons (`ITrashService`,
  `ILinkService`). `ExportEngine` itself is `new`'d per-call inside dialog code-behind with
  dependencies passed through (`ExportDialog.razor:211,240`). A new
  `StegoEngine`/`PayloadCrypto`/`PayloadZipper` should follow this same non-registered,
  constructed-per-call convention (all three are static, so "construction" is moot — they're
  called directly, same as `Hasher`).
- **Collision-safe naming lives in `DestinationPlanner.Resolve(ImageRecord)`**
  (`src/SnapZap.Core/Export/DestinationPlanner.cs:44-67`), not in `ExportEngine.cs` itself. It
  is coupled to `ImageRecord` + a content-hash probe (`exists`/`hashOf` functions injected for
  testability) and produces `"name (n).ext"` alternates, skipping a slot that already holds
  identical content. **This cannot be called directly from `PayloadZipper`** — there is no
  `ImageRecord` for an arbitrary zip entry. `PayloadZipper` must replicate the same
  `"name (n).ext"` avoidance strategy locally, not attempt to reuse `DestinationPlanner`.
- Passphrase must be provided by the user for both hide and extract; it must never be
  persisted anywhere (no `settings.json` storage, unlike `DependencyChecker`'s suppressible
  dialog preference), and must be non-empty (minimum 8 characters) — Confirm stays disabled
  otherwise.

### Files to Reference

| File | Purpose |
| ---- | ------- |
| `src/SnapZap.Core/Scanning/Hasher.cs:1-30` | Precedent for `System.Security.Cryptography` usage and single-type isolation pattern |
| `src/SnapZap.Core/Imaging/SkiaImageService.cs:71-89,183,256` | Sanctioned pixel decode/encode path (`MemoryMarshal.Cast<byte,uint>`); `DecodeScaled`'s `maxEdge` downsampling and `SKAlphaType.Premul` (line 82) are pitfalls, not just a pattern to copy — see Wire Format |
| `src/SnapZap.Core/Export/ExportEngine.cs` | Precedent for a multi-step file operation with hash-verification and manifest-style reporting |
| `src/SnapZap.Core/Export/DestinationPlanner.cs:44-67` | The actual collision-safe naming logic (`Resolve(ImageRecord)`, `"name (n).ext"` pattern) — coupled to `ImageRecord`/content-hash, replicate the strategy in `PayloadZipper`, do not call directly |
| `src/SnapZap.App/Components/ExportDialog.razor:1-17,193-199,211,240,245-257` | Reference overlay-dialog: text-path input, per-call engine construction, dialog-scoped `IProgress`/`EtaEstimator`/`CancellationTokenSource` |
| `src/SnapZap.App/Components/Toolbar.razor:6-13,107-109` | Reference for library-independent, always-visible actions and direct `AppState` calls |
| `src/SnapZap.App/Components/SelectionBar.razor:28,51` | Reference for contextual, selection-driven actions via `EventCallback` params (`OnOpenExport` declared at 51, invoked at 28) |
| `src/SnapZap.App/Services/AppState.cs:893,933,998` | `RunAsync` (defined line 998) busy-state pattern, called by `ScanAsync`/`DedupAsync`; scoped-per-circuit state + `Changed` subscription new dialogs must follow |
| `src/SnapZap.App/Components/SetupDialog.razor:23,281` | Reference for dialogs with pre-check/validation (`role="dialog"` at 23) and nested `.confirm`/`role="alertdialog"` focus trap (281) |
| `src/SnapZap.App/Components/Pages/Home.razor:130,136,145` | Where new dialogs get instantiated (`<ExportDialog>` at 136), wired to `SelectionBar`/`Toolbar` callbacks (130), and their `bool _show*` fields declared (145) |
| `src/SnapZap.App/Services/EtaEstimator.cs:15-40` | Rolling-window rate estimator + `Format(TimeSpan)` shared by progress UIs |
| `tests/SnapZap.Tests/ExportTests.cs:12-45` | Test style: `IDisposable`, per-test temp dir, `SKBitmap`-built PNG fixtures, direct (non-DI) engine construction |
| `docs/DESIGN.md` | Architecture, data model, safety invariants to keep consistent with |

### Technical Decisions

- **Encryption:** AES-GCM (`System.Security.Cryptography.AesGcm`) keyed from a user
  passphrase via `Rfc2898DeriveBytes`/PBKDF2 — both BCL, no new package. Decided over
  "hide only, no password" so the feature is real encryption, not just obscurity.
  **Passphrase must be non-empty, minimum 8 characters** — Confirm is disabled otherwise;
  this was missing from the initial draft (adversarial review F4) and is now a hard
  requirement, not a nice-to-have.
- **KDF iteration count is stored per-file, not hardcoded.** The payload blob includes a
  4-byte iteration-count field (see Wire Format) so it can be raised in a future release
  without breaking previously-hidden images (adversarial review F8) — `Decrypt` always reads
  the count from the blob, never assumes a compile-time constant. Pick the concrete default
  at implementation time by checking the *current* OWASP Password Storage Cheat Sheet
  guidance for PBKDF2-HMAC-SHA256 (a prior draft of this spec cited 210,000 iterations as
  "current," which is very likely stale — OWASP raised this figure over time, plausibly to
  ~600,000 as of recent guidance; verify rather than trust either number). Weigh the
  resulting UI latency (PBKDF2 is deliberately slow) against security margin, and consider
  showing a brief "Deriving key…" spinner state in both dialogs rather than assuming the
  derivation is instant.
- **Compression:** `System.IO.Compression.ZipArchive` (BCL, no new package) to bundle
  selected photos before encryption. Zipping already-compressed JPEGs yields little to no
  size reduction (DEFLATE on pre-compressed data is close to a no-op, sometimes slightly
  larger from container overhead) — capacity math must NOT assume zipping shrinks the
  payload (adversarial review F11).
- **Selection size is capped.** An unbounded selection held entirely in memory as `byte[]`
  zip + `byte[]` ciphertext risks out-of-memory on this app's realistic library sizes
  (tens of thousands of photos per `docs/DESIGN.md`) — adversarial review F5. HideDialog
  must enforce a practical ceiling (exact figure TBD at implementation time, e.g. a total
  selected-file-size limit in the few-hundred-MB range) with a clear inline message when
  exceeded, rather than leaving selection size unbounded.
- **Steganography technique:** LSB (least-significant-bit) embedding directly in pixel data
  via SkiaSharp's `SKBitmap.GetPixelSpan()`, chosen over DCT/frequency-domain approaches
  (explicitly out of scope) for implementation simplicity and because it pairs naturally
  with a PNG-only constraint. **Carrier must be decoded at full native resolution with
  straight (non-premultiplied) alpha** — not via the existing `DecodeScaled` call patterns,
  every one of which downsamples and/or premultiplies (adversarial review F1, F2). See
  Wire Format and Task 5.
- **Carrier format:** PNG enforced for both carrier input and output image, because LSB data
  does not survive JPEG's lossy recompression. Capacity is pre-checked (payload size vs.
  carrier pixel count) before embedding begins, using the *exact* formula `EmbedAsync` uses
  internally (frame header + crypto framing + zip length) — not a looser approximation, since
  a UI estimate that disagrees with the engine's real check defeats the point of pre-checking
  (adversarial review F6).
- **Path input:** carrier/output/target paths use the same plain text-input + server-side
  `Path.IsPathRooted` validation as `ExportDialog`/`Toolbar` — no native OS picker exists in
  this app and none should be introduced for this feature.
- **Engine construction:** `StegoEngine`, `PayloadCrypto`, `PayloadZipper` are constructed
  per-call in dialog code-behind, not DI-registered — matching `ExportEngine`'s convention.
  Unlike `ExportEngine` (which has real constructor dependencies), all three are static
  classes called directly, since none of them need `Database`/`ILinkService`/`ITrashService`.
- **UI placement:** "Hide in Image" in `SelectionBar` (selection-driven, discoverable to
  anyone who selects photos); "Extract Hidden Images" in `Toolbar` (library-independent,
  since the carrier image a user receives may not be in their scanned catalog). Chosen over
  a fully undiscoverable/gesture-only trigger to keep the spec's UI surface concrete and
  testable.

## Implementation Plan

### Wire Format (pin this before coding — all tasks below depend on it)

Revised after adversarial review (findings F1, F2, F6, F8, F11, F14 — see Technical Decisions
above for rationale on each change from the first draft):

- **Carrier decode requirement:** the carrier PNG MUST be decoded at its full native
  resolution (no `maxEdge` downsampling) with straight, non-premultiplied alpha
  (`SKAlphaType.Unpremul`, not the `Premul` every existing `SkiaImageService.DecodeScaled`
  call site uses). `StegoEngine` needs its own decode call — reusing an existing
  `DecodeScaled(path, smallMaxEdge)` call pattern verbatim silently destroys capacity and
  risks corrupting embedded bits on partially-transparent carriers.
- **Stego frame**, embedded via LSB (1 bit per R/G/B channel, alpha untouched, row-major
  raster order starting at pixel 0,0): `4-byte magic "SNZ1"` + `4-byte UInt32 BigEndian
  payload length` + `payload bytes`.
- **Payload** (the AES-GCM blob, now versioned — F8): `1-byte format version (0x01)` +
  `4-byte UInt32 BigEndian PBKDF2 iteration count` + `16-byte PBKDF2 salt` +
  `12-byte AES-GCM nonce` + `16-byte AES-GCM tag` + `ciphertext` (ciphertext length ==
  plaintext zip length, GCM is a stream cipher mode). Storing the iteration count per-file
  means it can be raised in a future release without breaking previously-hidden images —
  `Decrypt` always reads it from the blob rather than assuming a hardcoded constant.
- **KDF:** `Rfc2898DeriveBytes.Pbkdf2(passphraseUtf8Bytes, salt, iterations: <verify current
  OWASP PBKDF2-HMAC-SHA256 guidance at implementation time — do not trust a number written
  into this spec>, HashAlgorithmName.SHA256, outputLength: 32)` → 256-bit AES key. (F14: an
  earlier draft of this spec asserted 210,000 as "current OWASP guidance"; that figure is
  very likely stale. Re-check before picking a default, and weigh the resulting passphrase-
  prompt latency — PBKDF2 at a strong iteration count is deliberately slow enough to be
  user-noticeable.)
- **Capacity (bytes) = `floor(width * height * 3 / 8)`** (3 = R+G+B channels, 1 bit each, 8
  bits/byte), computed against the *full native resolution* decode above. A Hide operation
  needs `8 (frame header) + 49 (payload framing: 1 version + 4 iterations + 16 salt + 12
  nonce + 16 tag) + zipLength` bytes of capacity. **This exact formula — not an
  approximation — is what both the engine's pre-check and the dialog's live UI readout must
  compute (F6)**, and `zipLength` must not be assumed smaller than the sum of the source
  file sizes: zipping already-compressed JPEGs rarely shrinks them (F11), so the UI estimate
  should use the raw file-size sum as a *lower*, not upper, bound and add a small safety
  margin (e.g. +2%) for zip container/entry overhead.
- **Cancellation:** `PayloadCrypto.Encrypt`/`Decrypt` and `PayloadZipper.CreateZip`/
  `ExtractZip` all accept a `CancellationToken` (checked between zip entries / before the
  single AES-GCM call, which is not itself interruptible mid-block but is fast relative to
  zipping a large selection) — see Tasks 3–4. This closes the gap where `HideProgress`/
  `ExtractProgress` define `Zipping`/`Encrypting`/`Decrypting`/`Unzipping` phases (Task 1)
  that the engine methods previously had no way to observe cancellation during (F7).

### Tasks

- [x] Task 1: Define progress/result record types
  - File: `src/SnapZap.Core/Stego/HideProgress.cs`, `ExtractProgress.cs`, `HideResult.cs`, `ExtractResult.cs` (new)
  - Action: Add `record HideProgress(HidePhase Phase, long BytesDone, long BytesTotal)` with `enum HidePhase { Zipping, Encrypting, Embedding, Writing }`; mirror with `ExtractProgress`/`ExtractPhase { Reading, Decrypting, Unzipping, Writing }`. `HideResult(string OutputPath, long PayloadBytes, long CapacityBytes)`; `ExtractResult(IReadOnlyList<string> WrittenFiles)`.
  - Notes: Shape mirrors `ExportProgress`/`ExportResult` in `src/SnapZap.Core/Export/` — check that file for the exact record style to match (e.g. positional vs. init-only properties) before finalizing.

- [x] Task 2: Add the stego-specific exception type
  - File: `src/SnapZap.Core/Stego/StegoException.cs` (new)
  - Action: `public sealed class StegoException(string message) : Exception(message)`. Thrown for: insufficient carrier capacity, non-PNG carrier, "no SNZ1 magic found" on extract, and an existing unrelated file at `outputPath` (see Task 5). Wrong-passphrase/tampered-data failures are NOT wrapped in this type — they surface as the BCL `AuthenticationTagMismatchException` (derives from `CryptographicException`) from `AesGcm.Decrypt`, which callers catch separately.

- [x] Task 3: Implement the crypto layer
  - File: `src/SnapZap.Core/Stego/PayloadCrypto.cs` (new, static class — same isolation style as `Hasher.cs`)
  - Action: `public static byte[] Encrypt(byte[] plaintext, string passphrase, int iterations, CancellationToken ct = default)` — generate random 16-byte salt + 12-byte nonce (`RandomNumberGenerator.Fill`), derive key via PBKDF2 at the caller-supplied `iterations`, `AesGcm.Encrypt(nonce, plaintext, ciphertext, tag)`, return `0x01 || BE(iterations) || salt || nonce || tag || ciphertext` (version byte + iteration count are now part of the blob — Wire Format). `public static byte[] Decrypt(byte[] blob, string passphrase, CancellationToken ct = default)` — read the version byte (reject unknown versions via `StegoException`), read the stored iteration count, slice the remaining fixed fields (16/12/16/rest), derive the key using the *stored* iteration count (not a caller-supplied or hardcoded one), call `AesGcm.Decrypt`; let `AuthenticationTagMismatchException` propagate on wrong passphrase or corrupted data.
  - Notes: No SnapZap-specific stego framing here — this class only knows about the crypto blob, not the LSB frame. `ct` is checked before the (fast, non-chunkable) `AesGcm` call so a cancellation requested during a preceding long zip step is honored promptly rather than after an unnecessary encrypt/decrypt. Keep it swappable/testable in isolation, per `Hasher.cs`'s precedent.

- [x] Task 4: Implement the zip layer
  - File: `src/SnapZap.Core/Stego/PayloadZipper.cs` (new, static class)
  - Action: `public static byte[] CreateZip(IReadOnlyList<string> filePaths, CancellationToken ct = default)` — build a `ZipArchive` in a `MemoryStream` (`ZipArchiveMode.Create`), checking `ct` before each entry; name entries via `Path.GetFileName` with a **local collision-safe suffix** for duplicate names within the same selection (`name.ext`, `name (2).ext`, `name (3).ext`, ... — same numbering convention as `DestinationPlanner.Resolve`, tracked with an in-memory `HashSet<string>` of names already added to *this* zip, since two selected photos from different source folders can share a filename), return `.ToArray()`. `public static IReadOnlyList<string> ExtractZip(byte[] zipBytes, string outputDir, CancellationToken ct = default)` — open `ZipArchive` over a `MemoryStream(zipBytes)` (`ZipArchiveMode.Read`), write each entry into `outputDir` using the same local `"name (n).ext"` collision-safe strategy against files already on disk (do **not** attempt to call `DestinationPlanner` — it requires an `ImageRecord`/content-hash this call site doesn't have), checking `ct` before each entry, return the list of written paths.
  - Notes: `Directory.CreateDirectory(outputDir)` before writing. Do not overwrite existing files silently — matches the non-destructive posture of Export/Delete's safety invariants in `docs/DESIGN.md`. If cancelled partway through `ExtractZip`, the files already written up to that point remain on disk (see Task 9's cancellation note — this is a deliberate "no rollback" choice, not an oversight, to keep the engine simple; the dialog is responsible for telling the user what was recovered before the cancel).

- [x] Task 5: Implement the LSB stego engine
  - File: `src/SnapZap.Core/Stego/StegoEngine.cs` (new, static class)
  - Action:
    - `public static long CalculateCapacityBytes(int width, int height) => (long)width * height * 3 / 8;`
    - `public static async Task<HideResult> EmbedAsync(string carrierPath, string outputPath, byte[] payload, IProgress<HideProgress>? progress, CancellationToken ct)` — decode carrier at **full native resolution with straight (non-premultiplied) alpha** — do NOT reuse `SkiaImageService.DecodeScaled` with any of its existing small `maxEdge` call patterns, and decode with `SKAlphaType.Unpremul` (not the `Premul` `DecodeScaled` uses); reject if source format isn't PNG (`StegoException`). If `outputPath` already exists, refuse and throw `StegoException` unless it is byte-identical to `carrierPath` (the deliberate same-path-overwrite case a user can type explicitly). Build the frame header (`"SNZ1"` + BigEndian length) and confirm `payload` already includes the version+iteration-count framing from `PayloadCrypto.Encrypt`. Compute total capacity via `CalculateCapacityBytes`, throw `StegoException` with a message including required-vs-available bytes if `header.Length + payload.Length > capacity`. Walk pixels via `MemoryMarshal.Cast<byte,uint>(bitmap.GetPixelSpan())` (same access pattern as `SkiaImageService.cs:183,256`) writing one bit per R/G/B channel LSB in raster order for `header ++ payload`, encode the mutated bitmap back to PNG (`SKBitmap.Encode(SKEncodedImageFormat.Png, 100)`) and write to `outputPath`, reporting `HideProgress` at each phase boundary.
    - `public static async Task<byte[]> ExtractAsync(string carrierPath, IProgress<ExtractProgress>? progress, CancellationToken ct)` — decode via the same full-resolution/unpremultiplied path as `EmbedAsync` (extraction must mirror embedding's exact channel-reading order), reject non-PNG, read 8 bytes' worth of LSBs first; if the first 4 don't equal `"SNZ1"`, throw `StegoException("This image doesn't appear to contain hidden data.")`; else read the BigEndian length, then read that many more payload bytes via LSB, return the payload byte array (the encrypted blob, still to be decrypted by `PayloadCrypto.Decrypt` at the call site).
  - Notes: Alpha channel is never touched (keeps fully-opaque PNGs visually and structurally unchanged; partially-transparent carriers are handled correctly now via `Unpremul` decode, not silently corrupted). All pixel bit-twiddling stays inside this one file — `PayloadCrypto`/`PayloadZipper` know nothing about pixels, `StegoEngine` knows nothing about zip/crypto internals beyond treating the payload as opaque bytes. Per the "source folder never touched unless explicitly requested" invariant in `docs/DESIGN.md`, `EmbedAsync` must never write to `carrierPath` itself unless `outputPath == carrierPath` was explicitly typed by the user in the dialog.

- [x] Task 6: Unit tests for the crypto layer
  - File: `tests/SnapZap.Tests/PayloadCryptoTests.cs` (new)
  - Action: Follow the `ExportTests.cs` style (plain `[Fact]` methods, no `IDisposable`/temp-dir needed since this layer is pure in-memory). Cover: (1) encrypt-then-decrypt round trip returns the original plaintext byte-for-byte; (2) decrypting with a wrong passphrase throws `AuthenticationTagMismatchException`; (3) flipping one byte of the ciphertext/tag before decrypting throws (tamper detection); (4) two `Encrypt` calls on identical plaintext+passphrase produce different blobs (random salt/nonce, not a leak); (5) `Decrypt` correctly recovers and uses the iteration count stored in the blob even when called with a different default than the one `Encrypt` used (proves the stored-iteration-count design from Wire Format actually works, not just the happy path where they match by coincidence); (6) `Decrypt` throws `StegoException` on an unrecognized version byte.

- [x] Task 7: Unit tests for the zip layer
  - File: `tests/SnapZap.Tests/PayloadZipperTests.cs` (new)
  - Action: `IDisposable` class with per-test temp dir per `ExportTests.cs:12-45` style. Cover: (1) `CreateZip`-then-`ExtractZip` round trip recovers byte-identical file contents; (2) `CreateZip` with two source paths that share a filename (different source directories) produces two distinct, non-colliding zip entries — neither silently overwrites the other; (3) `ExtractZip` into an output directory that already contains a file with the same name as a recovered entry writes to a `"name (n).ext"` alternate instead of overwriting; (4) `ExtractZip` creates `outputDir` if it doesn't exist; (5) passing a `CancellationToken` already in a cancelled state throws `OperationCanceledException` before any file is written.

- [x] Task 8: Unit tests for the stego engine
  - File: `tests/SnapZap.Tests/StegoTests.cs` (new)
  - Action: `IDisposable` class with per-test temp dir (`Path.Combine(Path.GetTempPath(), "pc_stego_" + Guid.NewGuid().ToString("N"))`), fixtures built via `SKBitmap`/`SKCanvas`/`SKImage.Encode` per `ExportTests.cs:12-45`. Cover: (1) `CalculateCapacityBytes` returns the exact expected value for known width/height (golden-value test, mirroring `GoldenValueTests.cs`); (2) embed-then-extract round trip on an in-memory payload returns byte-identical bytes; (3) `EmbedAsync` throws `StegoException` when payload exceeds capacity, and — critically — does not create/modify `outputPath` when it throws; (4) `ExtractAsync` on a carrier PNG with no embedded frame throws `StegoException` with the "doesn't appear to contain hidden data" message; (5) `EmbedAsync`/`ExtractAsync` reject a `.jpg`-sourced `SKBitmap`/non-PNG input; (6) **embed-then-extract round trip on a carrier with genuine partial transparency (alpha strictly between 0 and 255 on some pixels) recovers byte-identical payload bytes** — this is the regression test for the premultiplied-alpha corruption risk (F2); (7) `EmbedAsync` on a large carrier confirms the full native resolution is used (capacity matches `width*height*3/8` for the *original* dimensions, not a downsampled size) — regression test for F1; (8) `EmbedAsync` throws `StegoException` (does not silently overwrite) when `outputPath` already exists and is not byte-identical to `carrierPath`.

- [x] Task 9: Build the Hide dialog
  - File: `src/SnapZap.App/Components/HideDialog.razor` (new)
  - Action: Clone `ExportDialog.razor`'s structure (`[Parameter] bool Show`, `[Parameter] EventCallback OnClose`, `[Parameter] IReadOnlyList<ImageView> Selected`, focus trap, Escape handling per `SetupDialog.razor:23`). Fields: carrier path (`<input type="text">`, validated `Path.IsPathRooted` + `.png` extension per `ExportDialog.razor:193-199`), output path (defaulted to carrier path's directory + `-hidden.png` suffix, never defaulted equal to the carrier path itself), passphrase + confirm-passphrase inputs (`type="password"`, must be non-empty, minimum 8 characters, and match to enable Confirm). Enforce a selection-size ceiling (reject with an inline message before the carrier/passphrase fields even matter if the selected photos' total size exceeds the practical limit from Technical Decisions). As soon as carrier path resolves to a readable PNG, decode its full-resolution dimensions and show a live "`{used} / {capacity}` available" readout computed with the *exact* Wire Format formula (`8 + 49 + zipLength` against `StegoEngine.CalculateCapacityBytes`, using the selected photos' summed file size plus a small safety margin as the `zipLength` estimate, per Technical Decisions/F6/F11); disable Confirm when the estimate exceeds capacity, the carrier isn't a `.png`, the passphrase is too short/blank, or the selection exceeds the size ceiling. Show explicit UI copy warning that the output must be transferred byte-for-byte (no re-upload through anything that recompresses it) or the hidden data will be destroyed. On Confirm: build own `EtaEstimator` + `Progress<HideProgress>` + `CancellationTokenSource` + `inFlight` guard exactly like `ExportDialog.razor:245-257`; call `PayloadZipper.CreateZip(ct)` → `PayloadCrypto.Encrypt(..., iterations, ct)` → `StegoEngine.EmbedAsync(..., ct)` in sequence, updating a progress bar off each reported phase; catch `StegoException` and show its message inline (no crash).
  - Notes: Passphrase fields must not be logged or included in any progress/result object that could be displayed or persisted.

- [x] Task 10: Build the Extract dialog
  - File: `src/SnapZap.App/Components/ExtractDialog.razor` (new)
  - Action: Same overlay/focus/progress skeleton as Task 9, but with no `Selected` parameter (library-independent). Fields: source PNG path (text input, `Path.IsPathRooted` + must exist), output folder path (text input, created if missing), passphrase (single field, no confirm — this is decrypt, not encrypt). On Confirm: `StegoEngine.ExtractAsync(ct)` → `PayloadCrypto.Decrypt(..., ct)` → `PayloadZipper.ExtractZip(..., ct)`, reporting `ExtractProgress`. Catch `StegoException` (bad/no hidden data) and `AuthenticationTagMismatchException`/`CryptographicException` (wrong passphrase) as two distinct, clearly-worded inline errors — do not let either bubble as an unhandled exception. On success, show the count and list of recovered files (mirrors any existing post-export summary UI in `ExportDialog.razor`, if present). On cancellation partway through, explicitly tell the user how many files were already recovered before the cancel (per `PayloadZipper.ExtractZip`'s no-rollback design in Task 4) rather than presenting a bare "cancelled" state that hides that partial output exists on disk.

- [x] Task 11: Wire "Hide in Image" into SelectionBar
  - File: `src/SnapZap.App/Components/SelectionBar.razor`
  - Action: Add `[Parameter] public EventCallback OnOpenHide { get; set; }` (declared alongside the existing `OnOpenExport` at `SelectionBar.razor:51`) and a new button beside the existing Export/Delete actions (near `SelectionBar.razor:27-30`), `@onclick="() => OnOpenHide.InvokeAsync()"`, following the exact style of the existing `OnOpenExport` wiring at `SelectionBar.razor:28`.

- [x] Task 12: Wire "Extract Hidden Images" into Toolbar
  - File: `src/SnapZap.App/Components/Toolbar.razor`
  - Action: Add a new toolbar button that toggles `_showExtract` (local field) rather than calling an `AppState` method directly (Extract needs dialog state, unlike the one-line `AppState.DedupAsync()` calls at `Toolbar.razor:107-109`) — expose an `EventCallback OnOpenExtract` parameter following the same pattern SelectionBar uses for `OnOpenExport`/`OnOpenHide`, so `Home.razor` owns the actual dialog-visibility state consistently for all three dialogs.

- [x] Task 13: Wire both dialogs into Home.razor
  - File: `src/SnapZap.App/Components/Pages/Home.razor`
  - Action: Add `bool _showHide` and `bool _showExtract` fields beside the existing `_showExport` field (declared at `Home.razor:145`); add `<HideDialog Show="_showHide" Selected="AppState.SelectedImages" OnClose="CloseHide" />` and `<ExtractDialog Show="_showExtract" OnClose="CloseExtract" />` beside the existing `<ExportDialog>` instantiation at `Home.razor:136`; wire `<SelectionBar OnOpenExport="OpenExport" OnOpenHide="OpenHide" />` and `<Toolbar OnOpenExtract="OpenExtract" />` (matching the existing `OnOpenExport="OpenExport"` wiring at `Home.razor:130`); add the trivial `OpenHide`/`CloseHide`/`OpenExtract`/`CloseExtract` toggle methods.

- [x] Task 14: Document the feature
  - File: `CLAUDE.md`, `docs/DESIGN.md`
  - Action: In `CLAUDE.md`'s "Key subdirectories in `Core/`" list, add a `Stego/` bullet describing `StegoEngine`/`PayloadCrypto`/`PayloadZipper` (matching the style of the existing `Export/`/`Delete/` bullets at `CLAUDE.md:179-180`); in the "Key subdirectories in `App/`" Components bullet, add `HideDialog`/`ExtractDialog` to the overlay list at `CLAUDE.md:161`. In `docs/DESIGN.md`, add a short section documenting the versioned SNZ1 wire format (including the per-file iteration-count field), the capacity formula, the full-resolution/unpremultiplied-alpha decode requirement, and the explicit non-goal (JPEG/lossy-transport survival) so future readers don't assume the feature is more robust than it is.

### Acceptance Criteria

- [x] AC1: Given N selected photos, a PNG carrier with sufficient capacity, and a valid passphrase, when the user confirms Hide, then a new PNG is written to the output path whose LSB frame decodes back to the original zip bytes, and the output is visually indistinguishable from the carrier to the naked eye.
- [x] AC2: Given a carrier PNG whose capacity is smaller than the selected photos' estimated payload size (computed with the exact Wire Format formula, not an approximation), when the HideDialog computes capacity, then the Confirm button is disabled and a "carrier too small — needs ~X KB, has ~Y KB" message is shown before any zipping, encryption, or file I/O occurs — and this holds precisely at the capacity boundary, not just for grossly oversized selections (regression coverage for the F6 estimate-mismatch gap).
- [x] AC3: Given a non-PNG file chosen as the carrier (e.g. a `.jpg`), when the user enters that path in HideDialog, then the dialog rejects it inline and Confirm stays disabled — no embed attempt is made.
- [x] AC4: Given a PNG produced by a Hide operation and the correct passphrase, when the user runs Extract with that PNG, then the originally-selected photos are recovered byte-identical into the chosen output folder — including when the carrier has genuine partial transparency (alpha strictly between 0 and 255 somewhere) and when the carrier's native resolution is large enough that a downsampled decode would have lost data (regression coverage for F1/F2).
- [x] AC5: Given a PNG produced by a Hide operation and an incorrect passphrase, when the user runs Extract, then decryption fails with a friendly "Incorrect passphrase or corrupted image" message and no files are written to the output folder.
- [x] AC6: Given an ordinary PNG with no SNZ1 frame embedded, when the user runs Extract on it, then the dialog shows "This image doesn't appear to contain hidden data" without an unhandled exception or crash.
- [x] AC7: Given a Hide or Extract operation in progress, when the user cancels via the dialog's cancel control, then the operation stops promptly (including during the Zipping/Encrypting/Decrypting/Unzipping phases, not just Embedding — `PayloadCrypto`/`PayloadZipper` accept a `CancellationToken`), no partially-written *output PNG* (Hide) is left behind, and the dialog returns to its idle state. For Extract specifically: if cancellation occurs after some files have already been written to the output folder, the dialog explicitly reports how many files were recovered before the cancel — it does not claim nothing happened.
- [x] AC8: Given the user explicitly types the same absolute path for both carrier and output in HideDialog, when they confirm, then the app allows the overwrite (explicit user choice) — but the dialog's own default suggested output path is never pre-filled equal to the carrier path, and if `outputPath` instead points at a *different*, pre-existing, unrelated file, Hide refuses with an inline error rather than silently overwriting it.
- [x] AC9: Given an output folder that already contains a file with the same name as a recovered entry, when Extract writes files, then `PayloadZipper.ExtractZip` uses its own local collision-safe naming (`"name (n).ext"`) rather than silently overwriting — covered by dedicated `PayloadZipperTests`, not left as an untested claim.
- [x] AC10: Given one or more photos selected in `PhotoGrid`, when the user clicks "Hide in Image" in `SelectionBar`, then `HideDialog` opens showing the count and total size of the selected photos.
- [x] AC11: Given a blank or fewer-than-8-character passphrase entered in HideDialog, when the user attempts to confirm, then Confirm stays disabled with an inline message — no zip/encrypt/embed work is attempted with a weak or empty passphrase.
- [x] AC12: Given a selection whose total file size exceeds the practical size ceiling defined in Technical Decisions, when the user attempts Hide, then the dialog rejects the selection inline before any carrier/passphrase work matters, rather than attempting to hold a multi-gigabyte payload in memory.
- [x] AC13: Given two selected photos that share the same filename but come from different source folders, when Hide zips the selection, then both are preserved as distinct entries in the zip (via `PayloadZipper.CreateZip`'s local collision-safe naming) — neither silently overwrites the other before encryption.
- [x] AC14: Given a hidden image created with one PBKDF2 iteration count, when a later app version's default iteration count changes, then Extract on that older image still succeeds, because the iteration count actually used is read from the payload's stored field, not assumed from the current default.

## Additional Context

### Dependencies

- No new NuGet packages — `System.Security.Cryptography` (`AesGcm`, `Rfc2898DeriveBytes`)
  and `System.IO.Compression` (`ZipArchive`) are both in the .NET 10 BCL; pixel access reuses
  the existing SkiaSharp 4.150.1 dependency already referenced by `SnapZap.Core.csproj`.
- Depends on `SkiaImageService`'s general decode/encode machinery for PNG I/O, but **not** on
  its existing `DecodeScaled(path, maxEdge)` call patterns as-is — every current call site
  downsamples and premultiplies alpha, both wrong for this feature (see Wire Format). Do not
  introduce a second unrelated image-decoding *library*; do add a decode path appropriate to
  stego's requirements (full resolution, unpremultiplied alpha) within `SkiaImageService` or
  alongside it.
- **Does not depend on `ExportEngine`'s collision-safe naming being reusable** — verified
  during adversarial review that the actual logic lives in `DestinationPlanner.Resolve
  (ImageRecord)` (`src/SnapZap.Core/Export/DestinationPlanner.cs:44-67`) and is coupled to
  `ImageRecord`/content-hash, not callable from `PayloadZipper` as-is. `PayloadZipper`
  replicates the same `"name (n).ext"` strategy locally instead (Task 4).
- No changes to `Program.cs` DI registration — all three new Core types are static, called
  directly, matching `Hasher`'s and `ExportEngine`'s per-call construction convention.

### Testing Strategy

- **Unit (Core):** `PayloadCryptoTests`, `PayloadZipperTests`, and `StegoTests` per Tasks
  6–8 — round-trip byte-identity, tamper/wrong-passphrase rejection, stored-iteration-count
  correctness, capacity math at full native resolution, non-PNG rejection, partial-alpha
  round-trip correctness, zip entry/output collision-safe naming, and a "no partial output on
  failure" safety-invariant test (mirroring `ExportTests`' verified move-failure invariant).
- **Manual (App):** walk through each AC explicitly rather than only the happy path:
  - AC1/AC4: Hide 3–5 real photos into a carrier PNG in the running app; confirm the output
    is visually identical to the carrier; Extract with the correct passphrase and verify the
    recovered files match the originals, including once with a carrier PNG that has genuine
    partial transparency.
  - AC2: attempt Hide with a carrier sized just under the required capacity and confirm the
    pre-check blocks it (not just a grossly-undersized carrier).
  - AC3: attempt Hide with a `.jpg` carrier and confirm inline rejection.
  - AC5/AC6: Extract with an incorrect passphrase, and separately Extract on an ordinary PNG
    with no hidden data; verify both friendly errors.
  - AC7: cancel mid-operation on a larger batch (e.g. 50 photos into a large carrier) during
    both the Zipping phase and the Embedding phase; for Extract, cancel after some files have
    already been written and confirm the dialog reports the partial recovery count.
  - AC8: attempt Hide with `outputPath` pointed at a different, pre-existing file and confirm
    it's refused; separately confirm the default suggested output path is never carrier==output.
  - AC10: select photos and confirm HideDialog opens showing the correct count/size.
  - AC11: attempt Hide with a blank and with a 4-character passphrase; confirm both are blocked.
  - AC12: attempt Hide with a selection larger than the size ceiling and confirm the inline
    rejection.
  - AC13: select two photos with the same filename from different folders and confirm both
    survive the round trip.
  - AC14 is covered by `PayloadCryptoTests` (Task 6, item 5) rather than manually, since it
    requires simulating a future default-iteration-count change.
- No integration/E2E test framework exists in this repo beyond xUnit against `SnapZap.Core`
  directly — Blazor component tests (`HideDialog`/`ExtractDialog`) are out of scope for
  automated coverage here, consistent with the rest of the `Components/` tree having no
  existing component-test precedent to follow; this is why the manual script above is
  written to explicitly enumerate every AC rather than relying on "spot check it works."

### Notes

- **High risk — lossy transport destroys hidden data.** LSB steganography does not survive
  any re-encoding of the output PNG. Messaging apps, social platforms, and some cloud photo
  services recompress or convert images on upload/send. This must be called out to the user
  in the HideDialog UI copy itself (e.g. "Share this file as-is — recompressing or
  re-uploading through some apps will destroy the hidden data"), not just in code comments.
- **Known limitation — capacity scales with carrier resolution.** A large photo batch needs a
  proportionally large carrier image (roughly 2.9 payload bytes per carrier pixel at 1
  bit/channel). Very large hides may require the user to pick a correspondingly large carrier;
  this is inherent to LSB and not a bug.
- **Open implementation-time decision — exact selection-size ceiling and PBKDF2 iteration
  count are deliberately left as "verify at implementation time" rather than pinned numbers**
  (adversarial review F5, F14): the size ceiling should be picked against real memory
  headroom on the target machines, and the iteration count against whatever OWASP's Password
  Storage Cheat Sheet says *at implementation time* for PBKDF2-HMAC-SHA256, balanced against
  UI latency. Both are stored/enforced in a way that survives being changed later (size
  ceiling is a UI-only check with no wire-format impact; iteration count is stored per-file
  per the versioned Wire Format), so under-guessing now is not a correctness risk, only a
  UX/security-margin tuning question.
- **Adversarial-review-driven design changes from the first draft:** full-resolution,
  unpremultiplied-alpha carrier decode (F1, F2); versioned payload with stored iteration
  count (F8); exact (not approximate) capacity formula shared between UI and engine (F6);
  zip-side collision-safe naming in addition to extract-side (F15); `CancellationToken`
  threaded through the crypto/zip layers (F7); non-empty passphrase enforcement (F4);
  selection-size ceiling (F5); `outputPath` collision handling distinct from the AC8
  carrier==output case (F12); explicit partial-recovery reporting on cancelled Extract (F13);
  dedicated `PayloadZipperTests` (F9); and corrected file:line citations throughout (F3, F10).
- **Future consideration (explicitly out of scope now):** DCT/frequency-domain steganography
  that could tolerate JPEG recompression; splitting a payload across multiple carrier images
  when one carrier lacks capacity; a passphrase-strength meter beyond the minimum-length gate.

## Review Notes

- Adversarial review completed (isolated subagent, diff-only context).
- Findings: 11 total, 6 fixed, 5 skipped (by-design/spec-compliant/cosmetic, each with rationale).
- Resolution approach: fix automatically.

**Fixed:**
- Added a catch-all `catch (Exception)` fallback in `HideDialog`/`ExtractDialog`'s run methods,
  matching `ExportDialog`'s existing pattern — an I/O error mid-run (disk full, a selected file
  deleted mid-zip) now shows inline instead of crashing the Blazor circuit.
- `PayloadCrypto.Decrypt` now rejects a stored iteration count of `0` or above 10,000,000 with a
  clean `StegoException` — Extract explicitly processes untrusted carriers ("a PNG someone gave
  you"), and an unbounded count read from the blob could otherwise hang for hours or throw an
  uncaught `ArgumentOutOfRangeException`.
- `PayloadZipper.ExtractZip` now rejects an entry with an empty or invalid name (e.g. a
  directory-only entry) with a `StegoException` instead of an uncaught native I/O exception —
  covers malformed/corrupted zip content reaching extract.
- `AppState.SelectedImages` is now cached and invalidated alongside `SelectedBytes`, instead of
  rescanning the whole library on every render.
- `HideDialog`/`ExtractDialog` now cancel their `CancellationTokenSource` on `Dispose()`, so
  closing the dialog mid-run without pressing Stop no longer leaves the background operation
  running unobserved.
- Reworded DESIGN.md's capacity section: the `8 + 49` overhead constant is shared exactly between
  `StegoEngine` and the Hide dialog's live readout, but the payload-length term is necessarily an
  estimate in the dialog (the real zipped size isn't known until zipping happens) — the previous
  wording overclaimed formula-level precision.
- Added regression tests for the two new guards: implausible/zero PBKDF2 iteration counts
  (`PayloadCryptoTests`) and a directory-only zip entry (`PayloadZipperTests`).

**Skipped (with rationale):**
- Synchronous header-probe on every carrier-path keystroke in `HideDialog` — the tech-spec
  explicitly asks for a live capacity readout "as soon as carrier path resolves," and the probe
  is a cheap header-only read, not a full pixel decode.
- ETA never producing a real "~Xm left" estimate — cosmetic only, no crash risk; `HideProgress`/
  `ExtractProgress` only report phase boundaries by design, since the underlying zip/crypto calls
  don't expose finer-grained progress.
- The overwrite guard compares carrier/output by content hash, not path — this is exactly what
  the tech-spec's Task 5 specifies (AC8), not a deviation to correct.
- PBKDF2/AES-GCM not being interruptible mid-call on Stop — the same accepted tradeoff the
  tech-spec already made explicitly for AES-GCM; PBKDF2 is the same order of magnitude.

## Post-Implementation Addendum: Concatenation Replaces LSB

**This spec's core technique (Wire Format, Capacity, and Task 5's LSB pixel embedding) was
superseded after initial implementation, at explicit user request.** The sections above describe
the original design and remain as the historical record of that design's rationale and adversarial
review — they no longer describe what ships. This addendum is the authoritative summary of what
actually shipped.

**What changed:** The user asked for the feature to work like the classic
`copy /b photo.jpg + archive.zip output.jpg` file-concatenation trick, rather than bit-level pixel
embedding. `StegoEngine` was rewritten accordingly:

- The carrier's own bytes are copied unmodified, followed by the payload, followed by a 13-byte
  `SNZC` footer (magic + encrypted flag + BE64 payload length) — no pixel data is read or written
  at all. See `docs/DESIGN.md` §13 for the full wire format.
- **The passphrase is now optional**, not required. Blank → the zip is appended unencrypted
  (readable directly by common zip tools, though not by .NET's own `ZipArchive` — see DESIGN.md
  for why). Non-blank (minimum 8 characters) → the zip is AES-GCM encrypted first, exactly as
  before.
- **The PNG-only constraint is gone.** Any image format `SkiaImageService.Probe` can read works as
  a carrier, since nothing about the format's pixel layout matters anymore.
- **The capacity concept is gone entirely** — `CalculateCapacityBytes`, the `8 + 49` frame/payload
  overhead constants, and the HideDialog's live capacity readout were all removed; concatenation
  has no pixel-count-derived ceiling; the only remaining limit is the pre-existing in-memory
  selection-size cap.
- `PayloadCrypto` and `PayloadZipper` are unchanged — only how the (optionally encrypted) payload
  attaches to the carrier changed.

**Superseded from the original spec (no longer true):**
- Wire Format's `SNZ1` pixel-embedded frame, the `8 + 49` capacity formula, and the full-resolution/
  unpremultiplied-alpha decode requirement (Task 5, F1/F2/F6 rationale) — replaced by the `SNZC`
  footer above.
- PNG-only enforcement for carrier/output (Technical Decisions, Scope) — any readable image format
  now works.
- Non-empty/minimum-length passphrase as a hard requirement (AC11, F4 rationale) — passphrase is
  now optional; the minimum-length rule still applies whenever one *is* provided.
- AC2, AC12's capacity/selection-size framing in terms of pixel capacity — selection-size ceiling
  still applies (memory, not pixels), capacity-vs-carrier framing does not.

**Still true, unchanged:** AES-GCM + PBKDF2 encryption with a stored (not hardcoded) iteration
count when a passphrase is used; zip-side and extract-side collision-safe naming
(`PayloadZipper`); cancellation threaded through zip/encrypt/write; the same-path-overwrite rule
(AC8); no rollback on a cancelled Extract with explicit partial-recovery reporting; the "lossy
transport destroys hidden data" warning (now broadened to "any re-save or re-encode," not just
JPEG-specific); the `HideDialog`/`ExtractDialog`/`SelectionBar`/`Toolbar`/`Home.razor` UI wiring
and dialog-scoped progress/cancellation pattern.

Full corrected code is in `src/SnapZap.Core/Stego/StegoEngine.cs`, `docs/DESIGN.md` §13, and
`tests/SnapZap.Tests/StegoTests.cs` (fully rewritten for the concatenation engine).
