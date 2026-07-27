---
title: 'CPU Performance Optimisation — Scan, Dedup and Serving'
slug: 'cpu-performance-optimisation'
created: '2026-07-26'
status: 'ready-for-dev'
stepsCompleted: [1, 2, 3, 4]
tech_stack:
  - '.NET 10 / C# (nullable enabled, implicit usings, primary constructors)'
  - 'ASP.NET Core + Blazor Server (interactive server components, no HTTP/JSON layer for app logic)'
  - 'SkiaSharp 4.150.1 (decode, resize, thumbnails) — ImageSharp forbidden, licence trap'
  - 'Microsoft.Data.Sqlite 10.0.10 + SQLitePCLRaw.bundle_e_sqlite3 3.0.4 (WAL, synchronous=NORMAL)'
  - 'Microsoft.ML.OnnxRuntime 1.27.1 (CPU EP only)'
  - 'MetadataExtractor 2.9.3 (EXIF)'
  - 'xUnit 2.9.3 + Xunit.SkippableFact 1.5.61'
files_to_modify:
  - 'src/SnapZap.Core/Imaging/SkiaImageService.cs'
  - 'src/SnapZap.Core/Scanning/Scanner.cs'
  - 'src/SnapZap.Core/Scanning/Hasher.cs'
  - 'src/SnapZap.Core/Analysis/BlurDetector.cs'
  - 'src/SnapZap.Core/Data/Database.cs'
  - 'src/SnapZap.Core/Data/ImageRepository.cs'
  - 'src/SnapZap.Core/Dedup/VariantFinder.cs'
  - 'src/SnapZap.Core/Dedup/PerceptualHash.cs'
  - 'src/SnapZap.Core/Dedup/SimilarityGrouper.cs'
  - 'src/SnapZap.Core/Nsfw/NsfwPreprocess.cs'
  - 'src/SnapZap.App/Program.cs'
  - 'src/SnapZap.App/CatalogService.cs'
  - 'src/SnapZap.App/Services/AppState.cs'
  - 'src/SnapZap.App/Services/ImageView.cs (ThumbUrl — cache-bust on recipe change)'
  - 'src/SnapZap.App/Components/PhotoGrid.razor'
  - 'src/SnapZap.App/Components/Card.razor'
  - 'src/SnapZap.App/SnapZap.App.csproj (PublishReadyToRun)'
  - 'src/SnapZap.Core/Nsfw/OnnxNsfwClassifier.cs (Task 27, conditional)'
  - 'src/SnapZap.Core/Nsfw/NsfwScorer.cs (Task 27, conditional)'
  - 'tests/SnapZap.Tests/ (new: PerfBaselineTests, GoldenValueTests, band-prefilter parity)'
  - 'docs/PERFORMANCE.md'
  - 'docs/DEDUP-V2.md'
  - 'docs/ROADMAP.md'
  - 'CLAUDE.md'
  - 'README.md (publish command)'
code_patterns:
  - 'Primary constructors on services; sealed records with init-only properties for data'
  - 'Doc-comments explain WHY and record rejected alternatives — preserve this when editing'
  - 'Best-effort signals: a failure yields a null column, never a dropped catalogue row'
  - 'PathScope is the single sargable definition of "inside the scanned folder"'
  - 'Repositories are cheap per-operation objects wrapping one shared Database'
  - 'DupeKinds bitmask records which detectors covered a row'
test_patterns:
  - 'xUnit, one class per feature (XxxTests), namespace SnapZap.Tests'
  - 'IDisposable fixtures with per-test temp dirs under Path.GetTempPath()'
  - 'SkippableFact for platform-specific paths; Category traits for conditional suites'
  - 'GrouperTests drives an explicit distance matrix — no images, no DB'
  - 'fixtures/ copied to output via CopyToOutputDirectory=PreserveNewest'
---

# Tech-Spec: CPU Performance Optimisation — Scan, Dedup and Serving

**Created:** 2026-07-26

## Overview

### Problem Statement

`docs/PERFORMANCE.md` establishes, with measurements taken against this repo's pinned dependency
versions, that SnapZap leaves large multiples on the table across three subsystems — and that two
of the findings are correctness defects rather than performance ones.

**Scan (the dominant cost).** `Scanner.Analyze` decodes every image at full resolution **twice** —
once in `WriteThumbnail`, once in `DecodeGray` — then the NSFW pass decodes a third time.
`CLAUDE.md` correctly identifies sharing the greyscale decode between blur and the perceptual hash
as "the largest single saving in this rewrite"; that saving is real but is then handed back by the
thumbnail decode immediately preceding it. Measured: **3.2×** recoverable on per-image scan CPU
(63.4 ms → 19.8 ms), roughly 5.3 min → 1.7 min of scan CPU across a 40k-photo library.

**Dedup.** `VariantFinder` is an O(n²) brute-force sweep. `DEDUP-V2` defends this and sets the
revisit threshold at ~150k photos, on the reasoning that `DistanceTo` bails on the first 64-bit
word. Two measured corrections: the early bail is **~8× less effective than described** when
rotations are enabled (the five-word loop runs once per rotation and `best` rarely reaches 0), and
a pigeonhole band prefilter — which is **exact**, not approximate — reaches **6.5× / 12× / 20×** at
20k / 50k / 100k photos. The revisit threshold is nearer than documented.

**Data layer and serving.** Per-row autocommit upserts cost **10.8×** against prepared, batched
statements. The tier-1 cache probe runs a single-row `SELECT` under a process-wide lock for every
file, which also intrudes on the cache-miss path (**8×** on the probe itself). `/api/thumb/{hash}`
serves content-addressed, permanently immutable thumbnails with **no cache headers at all**, so the
browser refetches them on every grid re-window.

**Two correctness defects surfaced during the analysis**, both in scope here:

1. **`SqliteConnection` is shared across threads.** `CatalogService` is a singleton holding one
   `Database` holding one connection, used concurrently by scanner workers (guarded by `Scanner`'s
   private lock), every Blazor circuit's `AppState`, and both HTTP endpoints (unguarded).
   `SqliteConnection` is not thread-safe.
2. **EXIF orientation is never applied.** Nothing in `src/` reads `codec.EncodedOrigin`. Verified
   against a JPEG tagged `Orientation=6`: `SKCodec.EncodedOrigin` reports `RightTop` correctly, but
   `SKBitmap.Decode` returns unrotated pixels. Every perceptual hash is therefore computed on raw
   sensor pixels, and portrait-tagged thumbnails are expected to render sideways in the grid while
   `/api/full/{id}` (served as the original file) renders upright in the preview modal.

Underlying several of these: **every downscale in the app is nearest-neighbour**.
`SKSamplingOptions.Default` resolves to `Filter=Nearest, Mipmap=None` (verified against SkiaSharp
4.150.1). `PerceptualHash.BoxSample` box-averages specifically to defend against point sampling —
while the resize immediately upstream has already point-sampled, keeping 1 pixel in 61.

### Solution

Work the three subsystems in the dependency order `PERFORMANCE.md §9` already establishes, taking
the two correctness defects alongside the performance work they are entangled with — the connection
refactor *is* the fix that removes the probe lock, and orientation normalisation *is* what makes the
perceptual hash describe the photo as displayed rather than as stored.

NSFW inference is held to a stricter rule than the rest of the codebase: **no change that trades
detection quality for throughput.** See Technical Decisions.

### Scope

**In Scope:**

- **§3 Scan path** — consolidate to one scaled decode feeding both thumbnail and greyscale buffer;
  replace nearest-neighbour sampling with linear+mipmap at the scan's resize sites; `GetPixelSpan()`
  in place of `.Pixels`; pre-populated `FileInfo` from `DirectoryInfo.EnumerateFiles`; remove the
  redundant `Task.Run` inside `Parallel.ForEachAsync`; fuse the Laplacian and variance passes;
  throttle per-file progress reports; allocation-free `SHA256.HashData` with a sequential-scan
  `FileStream`.
- **§4 Data layer** — prepared, parameter-reused, transaction-batched upserts; bulk-load the tier-1
  probe map instead of per-file queries under a lock; drop `SqliteCacheMode.Shared`; add
  `mmap_size` / `temp_store` / `cache_size` pragmas.
- **§5 Correctness** — connection per unit of work with a single dedicated writer, so scanner
  threads, Blazor circuits and the HTTP endpoints stop sharing one `SqliteConnection`.
- **§6.1 Dedup** — pigeonhole band prefilter for `VariantFinder`, exact and recall-preserving,
  landing behind a test asserting parity with the brute-force pair set.
- **§6.2 Correctness** — apply `codec.EncodedOrigin` (all eight values) so hashing, blur and
  thumbnails operate on display orientation, plus thumbnail-cache regeneration.
- **Signature-recipe versioning (new, from adversarial review)** — a `phash.recipe` version in `meta`
  driving invalidation whenever signature derivation changes. Required by Phase C, not just Phase D:
  the decode-scale and resampling changes alter signatures too, and without this the catalogue
  silently mixes old- and new-recipe hashes. Invalidation is eager (an `UPDATE` at catalogue open);
  the expensive backfill is lazy, running as a phase of `DuplicateService.DetectAsync`.
- **§6.3 Dedup** — even out the triangular load imbalance; flatten signature storage; remove the
  delegate + dictionary lookups from `SimilarityGrouper`'s inner loop.
- **§7 Serving** — `Cache-Control: public, max-age=31536000, immutable` on `/api/thumb/{hash}`; a
  cached mid-size preview for `/api/full/{id}`; make `AppState.LoadAsync` genuinely asynchronous;
  add `PublishReadyToRun` to the Windows publish.
- **NSFW, strictly score-preserving only** — flat tensor packing via `GetPixelSpan()` (bit-identical
  output), and batched inference gated behind the existing `Category=NsfwModelValidation` fixture
  suite proving scores are unchanged.
- **`ImageRepository.ByIds` (found during Step 2 investigation, not in `PERFORMANCE.md`)** — replace
  the full-catalogue scan with a parameterised `WHERE id IN (…)`, preserving caller-supplied result
  ordering. Hit by every preview-modal open, every export and every delete. See TD-6.

**Out of Scope:**

- **All GPU acceleration.** DirectML execution provider, fp16 model conversion, CUDA/TensorRT,
  `IInferenceProvider` wiring, and any GPU compute path for the Hamming sweep. `IInferenceProvider`
  stays declared-and-unimplemented exactly as it is today.
- **int8 quantisation of the NSFW model.** It is a throughput-for-accuracy trade and is excluded by
  the NSFW rule, notwithstanding the 75% sidecar-size reduction it would bring.
- **Scaled decode and resampling changes to the NSFW model input.** Both move the pixels the model
  scores and therefore move scores. The nearest-neighbour finding at `NsfwPreprocess.ToTensor:20`
  is documented here but deliberately left unactioned — it is a model-accuracy decision requiring
  labelled-fixture evidence, not a performance decision.
- Changing `VariantRotations`' default. `PERFORMANCE.md §6.2` argues it should stay on; after
  orientation normalisation lands it becomes a backstop rather than the primary mechanism, at which
  point it is a fresh conversation.
- Replacing SHA-256 with a non-cryptographic hash.
- `InvariantGlobalization` (marginal size win, needs a culture-sensitivity audit first).
- Any change to the complete-linkage grouping semantics, the `Exact → Burst → Variant` precedence,
  or `IsBulkSelectable()`.

## Context for Development

### Codebase Patterns

**Doc-comments carry the reasoning, and they are load-bearing.** This codebase documents *why* far
more than *what*, and repeatedly records the alternative that was tried and rejected —
`PerceptualHash` on why min-canonicalisation breaks, `SimilarityGrouper` on why union-find destroys
a library, `PathScope` on why `substr` was replaced with a range predicate. Any change to these
files must update the reasoning rather than silently invalidate it. Where this spec contradicts an
existing comment (notably `VariantFinder`'s "revisit past roughly 150k photos"), the comment gets
rewritten with the new measurement, not deleted.

**Best-effort signals never drop a row.** `Scanner.Analyze` wraps blur/phash/EXIF in `try/catch`
so a signal that cannot be computed becomes a null column while the identity row survives. New
failure modes introduced by orientation handling or scaled decode must follow the same rule.

**Safety invariants that constrain this work.** Nothing hard-deletes before hash verification;
`Exact | Variant` are bulk-selectable and `Burst` deliberately is not; grouping is complete-linkage
because perceptual similarity is not transitive; `GroupReconciler` precedence is
`Exact → Burst → Variant`. None of these may shift.

**Repositories are cheap per-operation wrappers** over a single shared `Database`. This is exactly
what the §5 refactor changes, so the pattern itself is in play — currently `new ImageRepository(db)`
is constructed freely (including inside a per-request endpoint handler) on the assumption that the
underlying connection is free to share.

**Scoping is centralised.** `PathScope.Sql` is a half-open range predicate specifically so it stays
sargable against the `UNIQUE` index on `images.path`. Any new query must use it rather than
re-deriving prefix matching, and must not wrap `path` in a function.

### Files to Reference

| File | Purpose |
| ---- | ------- |
| `src/SnapZap.Core/Scanning/Scanner.cs` | `Analyze` (the double decode), the probe lock, `Enumerate`'s `FileInfo` construction, per-file progress |
| `src/SnapZap.Core/Imaging/SkiaImageService.cs` | All three decode/resize sites; where scaled decode and `EncodedOrigin` land |
| `src/SnapZap.Core/Analysis/BlurDetector.cs` | `Laplacian` + `Variance` — the two-pass, extra-allocation fusion target |
| `src/SnapZap.Core/Dedup/PerceptualHash.cs` | 272-bit / 4-rotation layout, `DistanceTo` ceiling logic, `FromBytes` wrong-length precedent for TD-3 |
| `src/SnapZap.Core/Dedup/VariantFinder.cs` | The O(n²) sweep and its triangular `Parallel.For`; where the band prefilter attaches |
| `src/SnapZap.Core/Dedup/SimilarityGrouper.cs` | Complete-linkage contract — semantics must not change; `within` delegate hot path |
| `src/SnapZap.Core/Data/Database.cs` | Single connection, `SqliteCacheMode.Shared`, pragmas, `Migrate()` for the TD-3 migration |
| `src/SnapZap.Core/Data/ImageRepository.cs` | `Upsert`, `Probe`, `NeedingPhash`/`SetPhash` (unused — the migration's anchor), `ByIds` (see TD-6) |
| `src/SnapZap.App/CatalogService.cs` | Singleton owning the one `Database` — origin of the §5 sharing defect |
| `src/SnapZap.App/Program.cs` | `/api/thumb/{hash}` (no cache headers), `/api/full/{id}` (calls `ByIds`) |
| `src/SnapZap.App/Services/ImageView.cs` | `ThumbUrl => /api/thumb/{ContentHash}` — confirms thumbnails are content-addressed and immutable |
| `src/SnapZap.App/Services/AppState.cs` | `LoadAsync` synchronous-despite-Task; scoped per circuit |
| `src/SnapZap.Core/Nsfw/NsfwPreprocess.cs` | `ToTensor` — the only NSFW file eligible under TD-1 |
| `tests/SnapZap.Tests/GrouperTests.cs` | Distance-matrix pattern to extend for band-prefilter parity |
| `tests/SnapZap.Tests/PerceptualHashTests.cs` | Existing hash invariants that orientation normalisation must not break |
| `docs/DEDUP-V2.md` | Rationale that the prefilter measurement supersedes; needs updating alongside |

### Technical Decisions

**TD-1 — NSFW is held to a stricter standard than the rest of the codebase.** Per direction from
Gary: *do not modify anything on NSFW that affects performance vs detection quality.* Operationally,
an NSFW change is eligible only if it is provably score-preserving. Mechanical packing changes that
produce bit-identical tensors qualify. Anything that alters the pixels the model sees — quantisation,
scaled decode, resampling filter — does not, regardless of its speedup. Batched inference sits at the
boundary (batched GEMM kernels can reorder floating-point accumulation) and therefore ships only with
fixture evidence that scores are unchanged; absent the fixtures, it is deferred rather than assumed.

**TD-2 — Safety-critical invariants are untouchable.** The band prefilter changes *which pairs reach*
the grouper, never how the grouper decides. Exactness is by pigeonhole construction: with a threshold
of `t` bits split across `≥ t+1` bands, two hashes within `t` bits must agree exactly on at least one
band. This must be demonstrated by test, not asserted.

**TD-3 — Signature invalidation is versioned, not per-change.** *Revised after adversarial review.*
The original framing — "orientation normalisation invalidates every `phash`" — was too narrow.
**Phase C invalidates them too**, because changing decode scale (`GetScaledDimensions` yields 500×375
where the code asked for 512) and resampling filter (nearest → linear+mipmap) both change the
greyscale buffer the signature is derived from. Since the tier-1 probe never re-analyses an unchanged
file, a stale signature is never recomputed and the catalogue would silently hold two incompatible
hash populations being compared against each other. So invalidation is driven by a `phash.recipe`
version constant (Task 13) covering decode scale, resampling filter, orientation handling and grid
size. Invalidation fires at catalogue open (one `UPDATE`); the backfill that actually recomputes
signatures is deferred to the next detection run, so it neither blocks startup nor repeats when two
releases bump the recipe in succession. This generalises the precedent in
`PerceptualHash.FromBytes`, where a wrong-length blob is already treated as "not hashed" so a grid
change degrades to a re-scan rather than a corrupt comparison.

**TD-4 — The connection refactor and the probe-lock fix are one change.** `PERFORMANCE.md` lists
them as §5 and §4.2, but removing the global lock is only safe once connection ownership is sorted.
Sequencing them apart would mean briefly shipping unsynchronised concurrent access.

**TD-5 — Decode consolidation and the sampling fix ship together.** They touch the same ~20 lines of
`SkiaImageService`, and the 3.2× was measured *with* linear+mipmap sampling on the optimised side —
the quality improvement is already paid for by the decode saving. Splitting them would report a
misleadingly larger speedup for the first half.

**TD-6 — `ByIds` is O(catalogue) and belongs in scope. New finding, not in `PERFORMANCE.md`.**
`ImageRepository.ByIds` (line 253) is implemented as `All().ToDictionary(i => i.Id)` — a full,
*unscoped* table scan selecting all 17 columns including the 160-byte `phash` blob for every row in
the catalogue, materialised into a dictionary, in order to return the handful of ids asked for.
Callers: `/api/full/{id}` (**every preview modal open**), `ExportEngine` three times per export
(lines 18, 47, 126), and `DeleteService` on every delete. At 40k photos this is tens of megabytes
materialised to serve one image. The fix is a parameterised `WHERE id IN (…)`, but it must preserve
the existing contract — `ByIds` returns results **in the order the caller supplied**, with unknown
ids dropped — so the SQL result has to be reordered in memory rather than returned in `id` order.

**TD-7 — The backfill has an anchor already, and one trap.** `NeedingPhash` (selects
`phash IS NULL`) and `SetPhash` both exist and currently have **no callers anywhere in `src/`** —
they are ready-made for the TD-3 recipe backfill, and Task 13 is what gives them one. The trap:
nulling `phash` alone will not cause the scanner to recompute it, because the tier-1 probe keys on
`(path, file_size, mtime)` and `analyzed_at`, none of which change — so every file is a cache hit and
stays unhashed indefinitely. The migration must therefore either clear `analyzed_at` too (forcing
full re-analysis) or drive an explicit backfill pass over `NeedingPhash`. The second is cheaper and
more honest: only the signature is stale, not the hash, dimensions or EXIF.

**TD-8 — `docs/DEDUP-V2.md` is part of the change surface.** It states matching is brute force
"deliberately" and sets the revisit point at ~150k photos. The measured band-prefilter results
supersede that guidance. Landing the prefilter without updating DEDUP-V2 would leave the repo's own
rationale contradicting its implementation — the precise failure mode the codebase's comment style
exists to prevent.

**TD-9 — Measurement is part of the deliverable.** Every headline claim in `PERFORMANCE.md` is a
measured multiple with a documented method and stated caveats (synthetic 4000×3000 test image;
uniformly-random hashes in the dedup benchmarks; per-image CPU rather than wall clock). Changes
land with before/after numbers taken the same way, against a real catalogue where the synthetic
caveat matters most — the band prefilter especially, since real libraries cluster and bucket skew
will not resemble the uniform-random benchmark.

## Implementation Plan

Nine phases. **Phase A must come first and Phase I last.** Only Phase B is genuinely independent;
everything else carries an ordering constraint recorded in Dependencies and repeated on each task.

**The signature-invalidation rule.** Any change to how a perceptual signature is derived — decode
scale, resampling filter, orientation, grid — invalidates every stored `phash`. Because the tier-1
probe skips unchanged files, a stale signature is *never* recomputed on its own, so old-recipe and
new-recipe hashes would coexist in one catalogue and be compared against each other. That is silent
matching degradation with no error and no user-visible signal. Task 13 makes this structural: a
`phash_recipe` version in `meta`, checked at open, driving an automatic backfill. **Both Phase C and
Phase D change the recipe and must bump it.** Phase C cannot ship without Task 13.

Sizing: **XS** < 1h · **S** ≈ half day · **M** 1–2 days · **L** 3+ days.

### Tasks

#### Phase A — Baseline and golden values (blocks everything; TD-9)

- [ ] **Task 1 (M): Capture baseline measurements and golden values on Windows x64**
  - File: `tests/SnapZap.Tests/PerfBaselineTests.cs` (new, `[Trait("Category","Perf")]`) and
    `tests/SnapZap.Tests/GoldenValueTests.cs` (new); plus a scratch console harness — do **not** add
    a benchmark dependency to the shipped projects
  - Action: **(a) Performance baselines** on the Windows test machine against a real library
    (≥20k photos, mixed formats, some portrait-tagged) and a copy of a real `catalog.db`: fresh-scan
    per-image CPU, fresh-scan wall clock, fully-cached re-scan wall clock,
    `DuplicateService.DetectAsync` elapsed at threshold 20, `ByIds([oneId])` elapsed at full
    catalogue size **and** at 1k rows, total bytes allocated during a scan
    (`GC.GetTotalAllocatedBytes`), and peak working set.
    **(b) Golden values**, captured *before any code changes*: SHA-256 for a fixed fixture set, the
    full `NsfwPreprocess.ToTensor` output for a fixture image, and `BlurDetector` scores for a
    fixture set. Commit these as test data.
  - Notes: Record hardware, OS build, .NET version and library composition alongside the numbers.
    Every performance AC compares against **these** figures, not the Apple M1 numbers in
    `PERFORMANCE.md`. Golden values must be captured now — recorded after a change lands, they
    would enshrine whatever that change did, including a regression. Excluded from the default test
    run via the Category trait, following the `NsfwModelValidation` precedent.

#### Phase B — Quick wins (the only genuinely independent phase)

- [ ] **Task 2 (XS): Add immutable cache headers to the thumbnail endpoint**
  - File: `src/SnapZap.App/Program.cs:41`
  - Action: Set `Cache-Control: public, max-age=31536000, immutable` on the `/api/thumb/{hash}`
    response before returning `Results.File(...)`.
  - Notes: Safe because `ImageView.ThumbUrl` is `/api/thumb/{ContentHash}` — content-addressed, so
    different bytes mean a different URL and a stale entry is unreachable. Keep the hex-validation
    guard on `hash` ahead of any path construction. **Interacts with Phase D:** thumbnails
    regenerated after orientation normalisation keep the same content-hash URL, so caches must be
    busted then — see Task 16.

- [ ] **Task 3 (S): Stop `ByIds` scanning the whole catalogue**
  - File: `src/SnapZap.Core/Data/ImageRepository.cs:253`
  - Action: Replace `All().ToDictionary(i => i.Id)` with a parameterised
    `SELECT … FROM images WHERE id IN ($id0, …)`, chunked at 500 parameters, then reorder results in
    memory to match the caller-supplied `ids` order, dropping unknown ids.
  - Notes: **Contract preservation is the whole risk.** `ByIds` returns results in the order the
    *caller* supplied, not `id` order — `ExportEngine` depends on this. Reassembly must preserve
    caller order *across* chunk boundaries, and must handle a duplicated id in the input the same
    way the current implementation does (verify: the current dictionary lookup emits it twice).
    Empty input returns empty without issuing a query. See TD-6.

- [ ] **Task 4 (S): Replace `.Pixels` with `GetPixelSpan()` at both pixel-loop sites**
  - File: `src/SnapZap.Core/Imaging/SkiaImageService.cs:69`, `src/SnapZap.Core/Nsfw/NsfwPreprocess.cs:24`
  - Action: Read pixels via `GetPixelSpan()` (RGBA bytes, stride 4) rather than the allocating
    `SKColor[]` property. In `ToTensor`, write through `tensor.Buffer.Span` with a flat plane index
    instead of the `tensor[0, c, y, x]` four-dimensional indexer.
  - Notes: The NSFW half is eligible under TD-1 **only** because it is bit-identical — same source
    bytes, same arithmetic, same result. Do not change the resize filter or decode here. AC 10
    proves the bit-identity, which is also what makes Task 27's baseline valid despite being taken
    after this task.

- [ ] **Task 5 (XS): Remove the redundant thread hop in the scan loop**
  - File: `src/SnapZap.Core/Scanning/Scanner.cs:105`
  - Action: Call `Analyze(file, mtime)` directly instead of `await Task.Run(() => Analyze(...), token)`.
  - Notes: `Parallel.ForEachAsync` already owns a worker; the inner `Task.Run` parks it and hands
    CPU-bound work to a second pool thread. Removing it also removes `Task.Run`'s cancellation
    observation — add an explicit `token.ThrowIfCancellationRequested()` at the top of the body and
    keep the `OperationCanceledException` rethrow path intact.

- [ ] **Task 6 (XS): Make file hashing allocation-free and sequential-read friendly**
  - File: `src/SnapZap.Core/Scanning/Hasher.cs`
  - Action: Use static `SHA256.HashData(stream)` instead of `SHA256.Create()` + `ComputeHash`, and
    open with `new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan)`.
  - Notes: This type is the identity function for exact-duplicate detection **and** export
    verification. Both overloads must stay behaviourally identical; hash values must not change
    (AC 9, against Task 1 golden values).

- [ ] **Task 7 (XS): Throttle scan progress reporting**
  - File: `src/SnapZap.Core/Scanning/Scanner.cs:229` (`Report`)
  - Action: Emit at most ~10 reports/second via an `Environment.TickCount64` check, always emitting
    the final state so the UI never ends mid-count.
  - Notes: Currently one `IProgress.Report` per image marshals across the Blazor circuit 40k times.
    Counters stay exact; only notification rate changes.

- [ ] **Task 8 (XS): Enable ReadyToRun for the Windows publish**
  - File: `src/SnapZap.App/SnapZap.App.csproj`, publish command in `CLAUDE.md` and `README.md`
  - Action: Add `<PublishReadyToRun>true</PublishReadyToRun>` for the `win-x64` publish.
  - Notes: Record published size delta and cold-start delta. Do **not** enable trimming — SkiaSharp
    and ORT native interop will break.

#### Phase C — Imaging pipeline and signature invalidation (the 3.2×; TD-5)

- [ ] **Task 9 (M): Add a single-decode, correctly-sampled primitive to the imaging service**
  - File: `src/SnapZap.Core/Imaging/SkiaImageService.cs`
  - Action: Add a method that opens `SKCodec.Create(path)` once, picks a decode size via
    `codec.GetScaledDimensions(maxEdge / (float)Math.Max(info.Width, info.Height))`, decodes with
    `SKBitmap.Decode(codec, info)`, and returns that bitmap plus its `DecodedInfo`. Resize from it
    with `new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)`, not
    `SKSamplingOptions.Default`.
  - Notes: `SKSamplingOptions.Default` is `Filter=Nearest, Mipmap=None` (verified, SkiaSharp
    4.150.1). Decode at the larger of the two consumers' requirements (512 grey, 320 thumbnail) so
    one buffer serves both. `GetScaledDimensions` returns the nearest codec-supported size, not the
    requested one (measured: 512/4000 → 500×375) — downstream code must read actual dimensions.
    **This changes the greyscale buffer and therefore every signature; Task 13 is mandatory.**

- [ ] **Task 10 (M): Rewire `Scanner.Analyze` to decode once**
  - File: `src/SnapZap.Core/Scanning/Scanner.cs:171`
  - Action: Replace the separate `imaging.Probe`, `WriteThumbnail` and `DecodeGray` calls with one
    call to the Task 9 primitive, deriving thumbnail, greyscale buffer, blur score and perceptual
    hash from that single decode. Preserve width/height/format reporting by reading `codec.Info`
    from the same codec instance.
  - Notes: Preserve the best-effort contract exactly — a thumbnail failure must still yield a
    catalogue row, and a decode failure must still return `null` so `ClassifyUnreadable` can
    attribute the reason (AC 11). Update the `CLAUDE.md` / `DEDUP-V2.md` claim that the shared
    greyscale decode is "the largest single saving": after this it is one decode feeding three
    consumers.

- [ ] **Task 11 (XS): Avoid the redundant `stat` per file during enumeration**
  - File: `src/SnapZap.Core/Scanning/Scanner.cs:236` (`Enumerate`)
  - Action: Use `new DirectoryInfo(root).EnumerateFiles("*", opts)` so returned `FileInfo` objects
    are pre-populated from the directory walk, instead of `Directory.EnumerateFiles` + `new FileInfo(path)`.
  - Notes: Keep the same `EnumerationOptions` (recursive, `IgnoreInaccessible`, skip
    System/ReparsePoint) and the unsupported-extension tally unchanged.

- [ ] **Task 12 (S): Fuse the Laplacian and variance passes**
  - File: `src/SnapZap.Core/Analysis/BlurDetector.cs:35-57`
  - Action: Accumulate the Laplacian response's sum and sum-of-squares inside the loop that computes
    it, removing the intermediate `float[w*h]` and the second pass. **Divide by `w * h` — the full
    buffer length, including the border pixels the interior loop never visits — not by the interior
    count.**
  - Notes: This denominator looks like a bug and is load-bearing. The current `Variance` divides by
    `v.Length` (all pixels, border included, which are zero); the fused loop naturally yields the
    interior count `(w-2)*(h-2)`, which would re-scale every score by ~`wh/((w-2)(h-2))`. The blur
    threshold is a user-facing slider already calibrated against the current scale, so a silent
    re-scale invalidates every threshold users have chosen. Verify against Task 1 golden values
    (AC 12).

- [ ] **Task 13 (M): Make signature invalidation structural — eager invalidation, lazy backfill**
  - File: `src/SnapZap.Core/Data/Database.cs` (`Migrate`), `src/SnapZap.Core/Data/ImageRepository.cs`,
    `src/SnapZap.Core/Dedup/PerceptualHash.cs`, `src/SnapZap.Core/Dedup/DuplicateService.cs`
  - Action: Introduce a `PhashRecipeVersion` constant covering everything affecting signature
    derivation (decode scale, resampling filter, orientation handling, grid size), persisted in
    `meta` as `phash.recipe`. **Split invalidation from backfill — they have very different costs:**
    **(a) Invalidate eagerly, at catalogue open.** If the stored value differs from the constant, set
    `phash = NULL`, `dupe_checked_at = NULL`, `dupe_checked_kinds = 0` for all rows and store the new
    value. One `UPDATE`, no decoding, effectively instant.
    **(b) Backfill lazily, as a phase inside `DuplicateService.DetectAsync`.** Before the detectors
    run, drive the existing `NeedingPhash(root)` / `SetPhash(id, bytes)` pair to recompute signatures
    from disk — cancellable, with progress reported like the other phases ("Recomputing signatures"),
    and counted in `DetectAsync`'s phase total.
    Bump the constant as part of **this** task, since Tasks 9–12 changed the recipe.
  - Notes: **This is the fix for the phase-boundary hazard described above, and Phase C must not ship
    without it.** The trap (TD-7): nulling `phash` alone does not cause recomputation — the tier-1
    probe keys on `(path, file_size, mtime)` and `analyzed_at`, none of which change, so every file
    is a cache hit and stays unhashed forever. Hence an explicit backfill rather than relying on
    re-scan. `NeedingPhash`/`SetPhash` currently have **no callers anywhere in `src/`**; this task
    gives them one.
    **Why lazy rather than at open.** Three reasons, in order of importance. First, `Migrate()` runs
    synchronously inside the `Database` constructor, which `CatalogService`'s constructor calls,
    which DI resolves on first circuit — so a multi-minute backfill there hangs app launch with no
    progress bar and no cancellation. Second, it makes consecutive recipe bumps cheap: Phase C's
    invalidation and Phase D's are both `UPDATE`s, and a user upgrading through both pays for one
    backfill, not two (AC 6b). Third, it puts the work where the user just asked for it — clicking
    "Find duplicates" — rather than in an unexplained startup freeze.
    **This fits the existing architecture rather than fighting it.** `DetectAsync` already reports
    phased progress, and it already handles rows with no signature: `Note()` currently emits
    "N of M photos have no visual signature yet — re-scan to include them". **That message becomes
    wrong** once the backfill is automatic and must be updated or dropped.
    Clearing `dupe_checked_*` is required so the folder tree re-flags folders as pending. Follow the
    idempotency style of `AddColumnIfMissing` / `RenameSimilarToVariant`.
    **User-visible consequence to surface in the UI:** this clears duplicate-checked state for
    *every folder in the catalogue*, not just the current scan root, so libraries scanned weeks ago
    also re-flag as pending. That is correct — their signatures really are stale — but it must be
    stated, not discovered.
    **Rollback:** forward-only by design. Take a `catalog.db` backup before the first invalidation
    and tell the user where it is; reverting the code means restoring that file.
    **Rejected alternative — backfill from cached thumbnails instead of originals.** Far cheaper
    (320px JPEGs already on disk) but signatures derive from a 512px buffer, and re-deriving from a
    lossy 320px re-encode yields measurably worse hashes than the originals. It would silently
    degrade the exact thing the invalidation exists to correct. Record this in the doc-comment.

#### Phase D — EXIF orientation correctness (TD-3, TD-7; must precede Phase F)

- [ ] **Task 14 (M): Apply encoded origin during decode**
  - File: `src/SnapZap.Core/Imaging/SkiaImageService.cs`
  - Action: Read `codec.EncodedOrigin` and apply the corresponding transform to the decoded bitmap
    before any downstream use, so thumbnails, greyscale buffer, blur and perceptual hash all operate
    on display orientation. Handle **all eight** `SKEncodedOrigin` values — the four rotations
    (`TopLeft`, `RightTop`, `BottomRight`, `LeftBottom`) and the four mirrored forms
    (`TopRight`, `RightBottom`, `BottomLeft`, `LeftTop`).
  - Notes: Verified — for a JPEG tagged `Orientation=6`, `SKCodec.EncodedOrigin` reports `RightTop`
    correctly but `SKBitmap.Decode` returns **unrotated** pixels. Width/height written to the
    catalogue must be post-orientation for the 90°/270° cases, which also changes the `pixels` figure
    driving the `HighestResolution` keeper rule. The mirrored origins are the ones most likely to be
    implemented backwards and are covered explicitly by AC 5.

- [ ] **Task 15 (XS): Bump the signature recipe version**
  - File: `src/SnapZap.Core/Dedup/PerceptualHash.cs` (the Task 13 constant)
  - Action: Increment `PhashRecipeVersion` so orientation-derived signatures replace the Phase C ones.
  - Notes: Trivial only because Task 13 built the mechanism, and cheap because that mechanism is lazy
    — this costs an `UPDATE` against a `phash` column that is very likely still `NULL` from Phase C.
    A user who upgrades through both phases without running detection in between pays for exactly one
    backfill (AC 6b). Without this bump, orientation normalisation would silently mix old and new
    signatures — the same hazard as Phase C, second occurrence.

- [ ] **Task 16 (S): Regenerate thumbnails and confirm grid and preview agree**
  - File: `src/SnapZap.App/CatalogService.cs`, `src/SnapZap.App/Components/PhotoGrid.razor`,
    `src/SnapZap.App/Components/Card.razor`
  - Action: Invalidate and regenerate the thumbnail cache as part of the Phase D migration, then
    confirm a portrait-tagged photo shows the same orientation in the grid and in the
    `/api/full/{id}` preview modal.
  - Notes: Thumbnails are keyed by content hash, which does **not** change when orientation handling
    does — so they will not self-invalidate and stale sideways thumbnails would persist indefinitely.
    Combined with Task 2's one-year `immutable` cache header, browsers would also hold the old image;
    the regeneration must therefore change the URL (e.g. append a recipe-version query parameter to
    `ImageView.ThumbUrl`) or the cache header will defeat the fix. Before this phase, grid and preview
    are expected to **disagree** — that disagreement is the cheapest confirmation the defect was real.

#### Phase E — Data layer and connection ownership (TD-4)

- [ ] **Task 17 (XS): Tune SQLite pragmas and drop shared-cache mode**
  - File: `src/SnapZap.Core/Data/Database.cs:19,23`
  - Action: Remove `Cache = SqliteCacheMode.Shared`; add `PRAGMA temp_store=MEMORY`, a larger
    `PRAGMA cache_size`, and `PRAGMA mmap_size` alongside the existing `journal_mode=WAL` /
    `synchronous=NORMAL`.
  - Notes: Shared-cache adds table-level locking between connections and works directly against the
    WAL concurrency enabled on the next line. Must land before Task 18 opens multiple connections.

- [ ] **Task 18 (L): Give each unit of work its own connection, with one dedicated writer**
  - File: `src/SnapZap.App/CatalogService.cs:46`, `src/SnapZap.Core/Data/Database.cs`
  - Action: Stop exposing one long-lived `SqliteConnection`. Open a connection per operation
    (readers concurrent under WAL) and funnel writes through a single owned writer. `AppState`, both
    HTTP endpoints, and the scan/dedup/NSFW passes each take their own.
  - Notes: **This is a correctness fix, not an optimisation.** `SqliteConnection` is not thread-safe
    and is currently shared by scanner workers (guarded only by `Scanner`'s private `writeLock`),
    every Blazor circuit, and both endpoints (unguarded). Connection open is cheap and
    `Microsoft.Data.Sqlite` pools by connection string. `Database.Meta`/`SetMeta` and every repository
    must route through the new ownership model. **`CatalogService.Forget` and `Dispose` need explicit
    review** — `Forget` deletes all rows and vacuums while other connections may hold reads; the
    existing `catch (SqliteException)` around VACUUM already hints this was fragile with one
    connection. Verified structurally by AC 7a, not just by absence of exceptions.

- [ ] **Task 19 (M): Batch and prepare catalogue writes**
  - File: `src/SnapZap.Core/Data/ImageRepository.cs:31` (`Upsert`), `src/SnapZap.Core/Scanning/Scanner.cs`
  - Action: Reuse one prepared command with typed, value-reassigned parameters instead of a new
    `SqliteCommand` with `AddWithValue` per row; commit in transactions of ~500 rows.
  - Notes: Measured 10.8× in isolation (20k rows: 998 ms → 92 ms). Durability trade: a crash
    mid-scan loses up to one batch, which the next scan re-analyses — the same recovery path an
    interrupted scan already has. `Upsert` uses `RETURNING id`; confirm no caller needs the id per
    row before dropping it from the batched form.

- [ ] **Task 20 (S): Replace the per-file probe query with a bulk-loaded map**
  - File: `src/SnapZap.Core/Scanning/Scanner.cs:97`, `src/SnapZap.Core/Data/ImageRepository.cs:13`
  - Action: Load `(path → id, file_size, mtime, analyzed)` for the scan root once into a
    `Dictionary<string, …>` with `StringComparer.Ordinal`; look up from it instead of a single-row
    `SELECT` under `writeLock` per file.
  - Notes: Measured 8× on the probe path (40k files: 267 ms → 32 ms) at ~3 MB. The larger benefit is
    removing lock contention from the cache-**miss** path, where workers queue on the same mutex
    before they may begin decoding. Depends on Task 18.

#### Phase F — Duplicate detection (TD-2, TD-8; requires Phase D complete)

- [ ] **Task 21 (L): Add a pigeonhole band prefilter to variant matching**
  - File: `src/SnapZap.Core/Dedup/VariantFinder.cs:40`
  - Action: Index rotation 0 of every signature into `B` band tables; for each image probe all four
    of its rotations across those tables to collect candidates; run the existing `DistanceTo` only on
    candidates. Derive `B = VariantMaxBits + 1`, band width `ceil(272 / B)`. **Retain the
    brute-force sweep as a callable code path** — both for the high-threshold fallback and so AC 2
    and AC 3 can compare the two in-process. Expose which path executed so tests can assert on it.
  - Notes: **Exactness is by pigeonhole and must be tested, not assumed** — two hashes within `t`
    bits differ in at most `t` bands, so with `≥ t+1` bands at least one matches exactly.
    **Critical constraint:** `VariantMaxBits` is user-configurable 4–60 (`SetupDialog.razor:63`). At
    high thresholds band count rises and width collapses (t=60 → 61 bands of ~5 bits → 32 distinct
    values per band), so nearly everything collides and the prefilter degenerates to brute force
    *plus* indexing overhead. **Measure the crossover on the real catalogue and record it**; fall
    back above it. Measured at default t=20 with 21 bands × 13 bits: 12× at 50k, 20× at 100k — but on
    uniformly random hashes; real libraries cluster, so re-measure (TD-9).

- [ ] **Task 22 (S): Even out the triangular sweep and flatten signature storage**
  - File: `src/SnapZap.Core/Dedup/VariantFinder.cs:40`, `src/SnapZap.Core/Dedup/PerceptualHash.cs:47`
  - Action: Pair index `i` with `n-1-i` so threads get equal work, instead of `Parallel.For`'s
    contiguous chunks giving thread 0 an `n`-length inner loop and the last thread none. Store
    signatures in one flat `ulong[]` for the sweep rather than one heap `ulong[20]` per image.
  - Notes: Largely subsumed by Task 21 for the common case; still needed for the fallback path.
    `PerceptualHash`'s public shape and `ByteLength` must not change — internal sweep layout only,
    and `FromBytes`/`ToBytes` round-tripping must work unchanged.

- [ ] **Task 23 (S): Remove dictionary lookups from the grouper's inner loop**
  - File: `src/SnapZap.Core/Dedup/SimilarityGrouper.cs:120,132`, `src/SnapZap.Core/Dedup/VariantFinder.cs:60`
  - Action: Pass dense indices rather than image ids into the `within` predicate so `Merge` and
    `Admit` stop doing two `byId[…]` lookups plus a delegate dispatch per comparison.
  - Notes: **Grouping semantics must not change at all.** Complete linkage, closest-first ordering
    and the id tiebreak that makes runs reproducible all stay exactly as they are. `GrouperTests`
    must pass unmodified.

- [ ] **Task 24 (S): Update the dedup rationale to match the implementation**
  - File: `docs/DEDUP-V2.md`, `src/SnapZap.Core/Dedup/VariantFinder.cs` (class doc-comment), `CLAUDE.md`
  - Action: Rewrite the "brute force, deliberately … revisit past roughly 150k photos" guidance with
    the measured prefilter results and the threshold-dependent fallback. Correct the "one XOR and one
    PopCount" claim — with rotations enabled the sweep costs ~8× that, because the five-word loop
    runs once per rotation and `best` rarely reaches 0 to trigger the outer break.
  - Notes: TD-8 — doc-comments here record rejected alternatives and are load-bearing. Leaving them
    contradicting the code is exactly the failure their style exists to prevent.

#### Phase G — UI and serving

- [ ] **Task 25 (M): Make `AppState.LoadAsync` genuinely asynchronous**
  - File: `src/SnapZap.App/Services/AppState.cs:369`
  - Action: Move the catalogue read, group read and LINQ passes off the circuit thread
    (`await Task.Run(...)`), returning a real `Task` instead of `Task.CompletedTask` after doing all
    the work inline.
  - Notes: Currently a full `Under(scanRoot)` read, a `Groups()` read and ~a dozen whole-library LINQ
    passes run synchronously on the Blazor circuit. Mutation of `AppState` fields must land back on
    the circuit before `Changed` fires, or subscribed components render torn state. Depends on
    Task 18.

- [ ] **Task 26 (M): Serve a cached mid-size preview instead of the original file**
  - File: `src/SnapZap.App/Program.cs:50`
  - Action: Generate and cache a ~1600px preview alongside the thumbnail cache; serve it from
    `/api/full/{id}`, falling back to the original when one cannot be produced.
  - Notes: Today a 24 MP original is ~20 MB over the wire plus a full-resolution browser decode for a
    modal. Keep the guard that only catalogue-present paths are served. **Interacts with Phase D:** a
    generated preview is orientation-normalised in its pixels, whereas the original relied on the
    browser applying EXIF — the preview must bake the rotation in, or the modal will disagree with
    the grid in the opposite direction.

#### Phase H — NSFW (conditional; TD-1)

- [ ] **Task 27 (M): Batch NSFW inference — only if scores are provably unchanged**
  - File: `src/SnapZap.Core/Nsfw/OnnxNsfwClassifier.cs`, `src/SnapZap.Core/Nsfw/NsfwScorer.cs:57`
  - Action: **First** add a score-capture step that records per-image raw scores across the labelled
    fixture set — `Category=NsfwModelValidation` validates scores against *labels* and is not a
    capture harness, so this is new work, not a re-run. Then batch 16–32 images per `Run` call and
    re-capture, asserting per-image scores match within a tight tolerance. Ship only if that holds.
  - Notes: TD-1 — batched GEMM kernels can reorder floating-point accumulation, so this sits at the
    boundary of "score-preserving" and must be demonstrated, not assumed. The capture baseline is
    taken after Task 4, which is acceptable **only because AC 10 proves Task 4 is bit-identical**;
    if AC 10 fails, this baseline is invalid. Requires `PC_NSFW_MODEL` and `PC_NSFW_FIXTURES` — **if
    labelled fixtures are unavailable, defer this task entirely** rather than ship unverified. Do not
    touch decode, resize filter or precision.

#### Phase I — Close-out

- [ ] **Task 28 (S): Re-measure and reconcile the documentation**
  - File: `docs/PERFORMANCE.md`, `docs/DEDUP-V2.md`, `docs/ROADMAP.md`, `CLAUDE.md`
  - Action: Re-run every Task 1 measurement on the same Windows machine and library; record actual
    before/after figures beside the predicted ones; annotate every prediction that did not hold,
    including the measured prefilter crossover threshold from Task 21.
  - Notes: TD-9. Predictions that missed are the most valuable output — `PERFORMANCE.md`'s method
    section flags the synthetic-image and uniform-random-hash caveats precisely because they were
    expected to move the real numbers.

### Acceptance Criteria

Performance criteria carry explicit floors. Floors are set **below** the M1-measured figures because
the shipping target is Windows x64 with different JPEG-decode and storage behaviour; they mark the
point below which the change did not deliver and should be reconsidered, not the expected result.

**Correctness — must hold before any performance claim is accepted**

- [ ] **AC 1:** Given the full test suite on a clean checkout, when `dotnet test` runs after every
  phase, then all pre-existing tests pass unmodified — specifically `GrouperTests`, `DedupTests`,
  `PerceptualHashTests`, `ScannerTests`, `ExportTests` and `SelectionCommandTests`.
- [ ] **AC 2:** Given a catalogue of real signatures, when the band prefilter and the retained
  brute-force path are both run **in the same process against the same signature set**, then they
  produce identical pair sets, identical groups, identical keepers and identical `DupeKind`
  assignments. (In-process comparison is required because Phase D changes every hash, so no
  pre-change run is comparable.)
- [ ] **AC 3:** Given thresholds 4, 12, 20, 32, 45 and 60, when the parity test of AC 2 runs at each,
  then pair sets are equal at every threshold, **and the test asserts which path executed**, and at
  minimum thresholds 4, 12 and 20 exercise the prefilter path rather than the fallback. A threshold
  where both sides run brute force does not count as coverage.
- [ ] **AC 4:** Given a threshold above the measured crossover, when detection runs, then the
  implementation reports that it fell back to brute force and still returns the correct pair set.
- [ ] **AC 5:** Given one fixture image per `SKEncodedOrigin` value — all eight, including the four
  mirrored forms — when each is scanned after Phase D, then its thumbnail, its stored `width`/`height`
  and its perceptual hash all reflect display orientation, and all eight produce the **same**
  perceptual hash as the untagged upright original.
- [ ] **AC 6a:** Given a `catalog.db` written before Phase C, when the app opens it, then the recipe
  invalidation runs exactly once and completes without a perceptible startup delay — `phash` is
  cleared, `dupe_checked_*` clears catalogue-wide, affected folders re-flag as pending in the folder
  tree, a `catalog.db` backup exists at a path reported to the user, and a second launch performs no
  migration work. **No image decoding occurs during open.**
- [ ] **AC 6b:** Given a catalogue invalidated by the Phase C recipe bump and then invalidated again
  by the Phase D bump **without duplicate detection having run in between**, when detection is first
  invoked, then signatures are recomputed exactly once — not twice. (This is the property the
  eager-invalidate / lazy-backfill split exists to buy, and it is the regression most likely to creep
  back in.)
- [ ] **AC 6c:** Given a catalogue with cleared signatures, when duplicate detection runs, then the
  backfill executes as a reported, cancellable phase of `DetectAsync`; cancelling mid-backfill leaves
  the catalogue consistent (partially rehashed rows are valid, the rest still `NULL`) and a
  subsequent run resumes rather than restarting from scratch.
- [ ] **AC 7a:** Given the completed Phase E, when the code is inspected, then no `SqliteConnection`
  instance is reachable from more than one thread, and every write path routes through the single
  owned writer. (Structural check — the observable-symptom check below cannot fail-negative.)
- [ ] **AC 7b:** Given a scan running concurrently with an open browser session issuing thumbnail and
  preview requests plus a second circuit calling `LoadAsync`, when the contention test runs for a
  sustained period, then no `SqliteException` or torn read occurs.
- [ ] **AC 8:** Given ids in arbitrary order, including ids absent from the catalogue, a repeated id,
  and a list exceeding 500 entries, when `ByIds` is called, then results come back in caller-supplied
  order across chunk boundaries with unknown ids dropped, matching pre-change behaviour exactly.
- [ ] **AC 9:** Given the Task 1 golden fixture set, when hashed after Task 6, then every SHA-256
  value is byte-identical to its golden value, and export hash-verification passes.
- [ ] **AC 10:** Given the Task 1 golden tensor, when `NsfwPreprocess.ToTensor` runs after Task 4,
  then the output is element-wise identical.
- [ ] **AC 11:** Given an unreadable file, a permission-denied file and a non-image with an image
  extension, when the scan encounters them after Phase C, then each is attributed the same
  `ScanFailure` reason as before, and no catalogue row is dropped for a merely-failed signal.
- [ ] **AC 12:** Given the Task 1 golden blur scores, when scoring runs after Task 12, then scores
  match within floating-point tolerance — confirming the `w * h` denominator was preserved and
  user-chosen thresholds still select the same photos.
- [ ] **AC 13:** Given a thumbnail cached by a browser before Phase D, when Phase D regenerates it,
  then the browser fetches the new image rather than serving the stale one from the one-year
  `immutable` cache entry.

**Performance — measured on the Windows test machine against Task 1 baselines**

- [ ] **AC 14:** Given the Task 1 reference library, when a fresh scan runs after Phase C, then
  per-image scan CPU improves by **at least 2.0×** (M1 measured 3.2×).
- [ ] **AC 15:** Given a fully cached library, when a re-scan runs after Phase E, then **zero
  per-file SQL queries execute** during the probe phase, and wall-clock time is no worse than
  baseline.
- [ ] **AC 16:** Given a catalogue of at least 20k signatures at the default threshold of 20, when
  duplicate detection runs after Phase F, then elapsed detection time improves by **at least 3×**
  (M1 measured 6.5× at 20k).
- [ ] **AC 17:** Given catalogues of 1k and of full size, when `/api/full/{id}` is requested after
  Task 3, then response time at full size is **within 2× of response time at 1k** — i.e. effectively
  independent of catalogue size.
- [ ] **AC 18:** Given a grid scrolled repeatedly over the same photos, when thumbnails are requested
  after Task 2, then the browser issues **zero repeat network requests** for already-fetched hashes.
- [ ] **AC 19:** Given a scan of the reference library, when total allocated bytes are sampled via
  `GC.GetTotalAllocatedBytes` after Phase C, then allocation drops by **at least 30%** against
  baseline, reflecting removal of the per-image `SKColor[]` and the Laplacian intermediate.
- [ ] **AC 20:** Given the published `win-x64` executable, when cold-start is timed after Task 8,
  then startup improves against baseline and the size delta is recorded.

**Scope discipline**

- [ ] **AC 21:** Given the completed work, when the diff is reviewed, then it contains no DirectML,
  CUDA, fp16 or int8 artefacts, no `IInferenceProvider` implementation, and no change to NSFW decode,
  resize filter or model precision.
- [ ] **AC 22:** Given the completed work, when `docs/DEDUP-V2.md`, `docs/PERFORMANCE.md` and
  `CLAUDE.md` are read, then no statement in them contradicts the shipped implementation, and the
  measured prefilter crossover threshold is recorded.

## Additional Context

### Dependencies

**No new NuGet packages.** Every change uses APIs already available in the referenced versions —
verified: `SKCodec.GetScaledDimensions(float)`, `SKBitmap.Decode(SKCodec, SKImageInfo)`,
`SKBitmap.GetPixelSpan()`, `SKCodec.EncodedOrigin` and `SKSamplingOptions(SKFilterMode, SKMipmapMode)`
all exist in SkiaSharp 4.150.1. This is deliberate: adding a dependency to a project that forbids
ImageSharp on licence grounds and ships a self-contained 130 MB exe deserves its own decision.

**External inputs required:**

| Need | Status | Blocks |
| ---- | ------ | ------ |
| Windows x64 test hardware | **Confirmed available** | Tasks 1, 8, 28; all performance ACs |
| A real photo library (≥20k photos, mixed formats, some portrait-tagged) | Required — confirm before starting Task 1 | Tasks 1, 21, 28; ACs 14–20 |
| A real `catalog.db` copy | Required — confirm before starting Task 1 | Tasks 1, 21; AC 6 |
| Fixture images for all eight `SKEncodedOrigin` values | Must be authored (none exist; `fixtures/` holds one `exif_sample.jpg`) | Task 14; AC 5 |
| Labelled NSFW fixtures + `PC_NSFW_MODEL` | Unconfirmed | Task 27 only — deferred, not shipped unverified (TD-1) |

**Internal ordering — no phase except B is independent:**

- Task 1 → everything (golden values must predate any change).
- Tasks 9 → 10 → 12 → **13** within Phase C. **Task 13 is not optional and Phase C must not ship
  without it**, because Tasks 9 and 12 change signature derivation.
- Task 14 → 15 → 16 within Phase D; Task 15 depends on the mechanism Task 13 builds.
- **Phase C → Phase D → Phase F.** Both C and D invalidate every signature; running F earlier means
  matching against hashes that are about to be discarded.
- Task 17 → 18 → {19, 20, 25}.
- Task 16 depends on Task 2 (the `immutable` cache header is what makes thumbnail regeneration need
  a URL change).
- Task 26 depends on Phase D (preview orientation must agree with the grid).
- Task 21 must retain the brute-force path as callable code — ACs 2, 3 and 4 all depend on it.

### Testing Strategy

**Unit — extends existing patterns**

- **Band prefilter parity** (new `BandPrefilterTests`): run the retained brute-force path and the
  prefilter over the same signature set **in one process**, assert set equality of pairs. Parameterise
  over thresholds 4 / 12 / 20 / 32 / 45 / 60 and assert *which path executed* at each, so a threshold
  that silently fell back is not counted as prefilter coverage (AC 2, AC 3, AC 4). Follow
  `GrouperTests`' style — constructed signatures, no images, no database. Also run once against
  signatures loaded from the real `catalog.db` copy, since uniform-random hashes are the prefilter's
  best case.
- **Orientation** (`AnalysisTests` / `PerceptualHashTests`): author one fixture per `SKEncodedOrigin`
  value — all eight, including the four mirrored forms, which none of the existing fixtures cover —
  and assert every one produces the *same* perceptual hash as the untagged upright original, and that
  reported width/height swap for the 90°/270° cases (AC 5). Fixtures can be generated by injecting an
  EXIF APP1 segment into a plain JPEG; `fixtures/exif_sample.jpg` is the only existing example and
  covers none of these.
- **Golden-value regression** (`GoldenValueTests`, authored in Task 1): SHA-256 (AC 9), NSFW tensor
  contents (AC 10) and blur scores (AC 12), all captured *before* any code change. These three are
  the spec's main defence against a silent behavioural change dressed as an optimisation — AC 12 in
  particular is what catches the `w * h` denominator regression Task 12 warns about.
- **`ByIds` contract** (`CatalogScopeTests`): caller-supplied ordering preserved across chunk
  boundaries, unknown ids dropped, a repeated id, empty input, and an input exceeding 500 entries
  (AC 8).
- **Scan failure attribution** (`ScannerTests`): unreadable / permission-denied /
  non-image-with-image-extension keep their existing `ScanFailure` reasons after the decode rewrite
  (AC 11).

**Integration**

- **Recipe invalidation** (`CatalogScopeTests` style, temp-dir catalogue): open a pre-Phase-C
  `catalog.db`, assert the invalidation runs once, `phash` clears, `dupe_checked_*` clears
  catalogue-wide, a backup file exists, a second open is a no-op, and **no image is decoded during
  open** (AC 6a). Repeat for the Phase D bump.
- **One backfill across two bumps** (AC 6b): apply the Phase C bump, then the Phase D bump with no
  detection run in between, then invoke detection and assert each image was hashed exactly once.
  Instrument the backfill with a counter rather than inferring it from timing.
- **Backfill cancellation and resume** (AC 6c): cancel mid-backfill, assert the catalogue is
  consistent (rehashed rows valid, remainder still `NULL`) and that a subsequent run resumes rather
  than restarting — `NeedingPhash` already gives this for free by selecting `phash IS NULL`.
- **Thumbnail cache busting** (AC 13): confirm a browser holding a Phase-C thumbnail under the
  one-year `immutable` header fetches the regenerated Phase-D image. This is the interaction most
  likely to be missed — Task 2 and Task 16 are in different phases and pull in opposite directions.
- **Concurrency** (AC 7b): scan a large library while a loop issues `/api/thumb` and `/api/full`
  requests and a second circuit calls `LoadAsync`. **Expected to be flaky-by-design before Task 18**
  — if it never fails on current code it is not exercising the race, and a green run proves nothing.
  AC 7a (the structural check that no connection is reachable from two threads) is the criterion that
  can actually fail; treat AC 7b as corroboration only.
- **End-to-end unchanged behaviour**: full `dotnet test` after every phase (AC 1).

**Manual**

- Portrait-tagged photo: grid thumbnail vs preview modal, before and after Phase D (AC 5, AC 13).
  They are expected to *disagree* beforehand.
- Grid scroll with browser devtools network panel: confirm thumbnails stop refetching (AC 18).
- Widen `VariantMaxBits` to 60 in Setup and re-run detection: confirm the fallback engages and the
  run does not become slower than baseline (AC 4).
- Windows-only paths from `docs/WINDOWS-VERIFY.md` — recycle bin, shell restore, hardlinks — re-run
  after Phase E, since connection ownership changes touch delete and export.
- Cold-start timing of the published `.exe` before and after Task 8 (AC 20).

### Notes

Source analysis: `docs/PERFORMANCE.md` (28 KB, written 2026-07-26). All measurements in it were
taken on Apple M1 Pro / 10 cores / 32 GB / .NET 10.0.302 against this repo's pinned dependency
versions. The Windows shipping target is x64 with different JPEG-decode and storage characteristics
— treat multiples as direction and magnitude, not promises. Ratios should hold better than absolute
times.

#### Pre-mortem — highest-risk items, in order

1. **Signature invalidation is the failure that leaves no trace (Tasks 9, 12, 13, 14).** Anything
   changing signature derivation makes every stored `phash` obsolete, and the tier-1 probe guarantees
   stale signatures are never recomputed on their own. Old-recipe and new-recipe hashes then sit in
   one catalogue being compared against each other: matching quietly degrades, nothing throws, no
   count looks wrong. Task 13's recipe-version mechanism exists so this cannot depend on someone
   remembering. **The residual risk is a future change that alters derivation without bumping the
   constant** — its doc-comment must enumerate what counts (decode scale, resampling filter,
   orientation, grid size) and state that extending that list is mandatory.
2. **The band prefilter degenerates at high thresholds (Task 21).** `VariantMaxBits` is a
   user-facing slider running to 60. At that setting pigeonhole needs 61 bands over 272 bits —
   ~5 bits each, 32 distinct values per band — so nearly everything collides and the "prefilter"
   becomes brute force plus indexing cost. A user who widens the slider experiences the optimisation
   as a regression. The measured fallback is not a nicety; without it this ships a setting that makes
   the app slower.
3. **The backfill drifts back to eager (Task 13).** The eager-invalidate / lazy-backfill split is
   the only thing preventing a multi-minute freeze inside a constructor on app launch, and the only
   thing making two consecutive recipe bumps cost one backfill instead of two. It is also the kind of
   structure a later refactor "simplifies" by moving the recompute next to the invalidation, where it
   reads more naturally. AC 6a (no decoding during open) and AC 6b (two bumps, one backfill) are what
   catch that; the constant's doc-comment should say why the split exists.
4. **The concurrency fix is hard to prove (Task 18).** The `SqliteConnection` race is narrow and was
   never reproduced — found by reading ownership, not by observing a failure. A green suite after
   Task 18 is weak evidence because the suite was green before. AC 7a (structural: no connection
   reachable from two threads) is the criterion that can actually fail; AC 7b is corroboration only.
5. **`Forget()` and `Dispose()` under multiple connections (Task 18).** `CatalogService.Forget`
   deletes all rows and `ImageRepository` vacuums; with connections no longer singular, both can run
   while others hold reads. The existing `catch (SqliteException)` around VACUUM already hints this
   was fragile even with one connection.
6. **Blur-score scale is user-visible state (Task 12).** `Variance` divides by total pixel count
   including the zeroed border. It looks like a bug and is load-bearing: the threshold is a slider
   users have already calibrated against these values. The fused loop naturally yields the *interior*
   count, so writing the obvious code silently re-scales every score. Task 12 states the required
   denominator explicitly and AC 12 checks it.
7. **Cache headers and thumbnail regeneration pull in opposite directions (Tasks 2 and 16).** A
   one-year `immutable` header on a URL keyed by content hash — which does not change when
   orientation handling does — means browsers serve sideways thumbnails indefinitely after Phase D.
   The two tasks sit in different phases and it is easy to ship one without the other.

#### Known limitations

- Scaled decode is worth only ~1.5× on its own (26.2 → 17.5 ms measured); libjpeg-turbo still
  entropy-decodes the whole stream. The 3.2× comes mostly from decoding **once** rather than twice.
  Expect the split accordingly and do not attribute the win to the wrong half.
- `GetScaledDimensions` returns codec-supported sizes, not requested ones (512/4000 → 500×375), so
  the greyscale buffer will not be exactly 512 on its long edge. `PerceptualHash.FromGray` box-samples
  to a fixed 17×17 grid and is dimension-independent, so the *code* is safe — but the resulting bits
  differ from today's even before orientation is considered. This is precisely why Task 13 exists and
  why Phase C cannot ship without it: "dimension-independent" means the algorithm tolerates any input
  size, not that two different input sizes yield the same hash.
- Dedup speedups were measured on uniformly random hashes, which is the prefilter's best case: real
  libraries cluster, so buckets skew and both sides slow down. The candidate-count reduction is
  structural and survives; the multiplier will not match.

#### Future considerations (explicitly out of scope)

- GPU acceleration via DirectML — `IInferenceProvider` already declares the seam; `PERFORMANCE.md §8`
  holds the analysis and the two verified packaging constraints (the DirectML package ships its own
  `onnxruntime.dll` so it conflicts with the CPU package, and it lags at 1.24.4 vs 1.27.1).
- int8 NSFW model — 328 MB → ~86 MB sidecar and 2–3× on CPU, gated on accepting a score change.
- Fixing nearest-neighbour resampling in the NSFW preprocessing path — currently the model scores
  aliased input, which is off-distribution from its training. This is a model-accuracy question
  requiring labelled-fixture evidence, deliberately excluded here (TD-1).
- Defaulting `VariantRotations` off — only becomes reasonable once Phase D makes rotation matching a
  backstop rather than the primary mechanism. `PERFORMANCE.md §6.2` has the measured argument.
