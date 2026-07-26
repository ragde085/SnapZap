# Duplicate detection v2 — in-house perceptual hashing

**Status:** design agreed, implementation in progress
**Supersedes:** the `czkawka_cli` sidecar, which is removed entirely
**Related:** [DESIGN.md](DESIGN.md) §2 (architecture), §7 (safety invariants), [ROADMAP.md](ROADMAP.md)

---

## 1. Why change anything

Three separate problems, only one of which is about speed.

**It was hanging, not merely slow.** `CzkawkaFinder` redirected the subprocess's stdout and
stderr and then awaited `WaitForExitAsync` without reading either. The moment czkawka wrote more
than the OS pipe buffer (~4 KB) it blocked on write and never exited, and we blocked on an exit
that could not come. On a library large enough to produce that much progress output, "Find
duplicates" was a deadlock wearing a spinner. Fixed ahead of this work; noted here because it is
the reason the feature *felt* like it needed re-architecting.

**It decoded the whole library twice.** SnapZap already decodes every image during the scan — for
the thumbnail, and again for the Laplacian blur score. czkawka then walks the same tree and
decodes every image a third time, in a separate process, to compute its perceptual hashes. The
single largest win available here is not a faster hash; it is *not decoding again*.

**Nothing was tracked per folder.** `DedupAsync` ran over the whole scan root, wrote no record of
having done so, and the folder tree could not distinguish "no duplicates here" from "nobody has
looked". Addressed by `images.dupe_checked_at` (shipped ahead of this work) and extended below.

### What we are not fixing: per-folder parallelism

The original instinct was to run detection in parallel per folder. That is wrong on correctness,
not just on cost:

- Exact matching filters **both sides** of the hash comparison to the same root
  (`ExactDuplicateFinder`). Split the run per folder and two identical photos in sibling folders
  are never paired — losing precisely the duplicates a user most wants found.
- `Database` owns a single `SqliteConnection`. Every finder calls `BeginTransaction()` on it.
  Concurrent runs throw immediately.
- `DupeRepository.ClearKind` deletes groups lying entirely under a root. Concurrent runs on a
  folder and its own subfolder race, one wiping the other's just-written groups.

Parallelism belongs **inside** one run, across images, not across folders. That is what this
design does.

---

## 2. Is replicating czkawka realistic?

Honestly assessed, because the answer is "yes, but not for the reason it looks like".

**The hash is trivial.** czkawka's default (`--hash-alg gradient`) *is* dHash: downsample to a
fixed grid, compare adjacent pixels, pack the bits. Roughly 40 lines.

**The matching is trivial at our scale.** 50k photos is 1.25 × 10⁹ pairs. With early exit on the
first 64-bit word — unrelated pairs differ in ~half their bits and blow the threshold immediately
— the vast majority of pairs cost one XOR and one `PopCount`. Parallelised, that is well under a
second. No BK-tree, no multi-index hashing.

**The grouping is the whole difficulty, and it is destructive when wrong.** Perceptual similarity
is *not transitive*: A~B at distance 8 and B~C at distance 8 does not give A~C at ≤ 8. Feed those
pairs to a naive union-find and a real library collapses into one group of several thousand
photos, at which point the keeper heuristic nominates all but one of them for deletion. czkawka
avoids this deliberately. So must we — see §6.

**We lose no format coverage.** `CzkawkaFinder.StoreGroups` maps czkawka's paths through
`ImageRepository.PathIndex()` and silently drops anything not in the catalog. The catalog only
holds what SkiaSharp decoded (`Scanner.UnsupportedExtensions` counts the rest). So czkawka's
HEIC/RAW/JXL groups are *already* being discarded today. Removing it costs nothing there. If
SnapZap ever gains HEIC decoding, both paths gain it together.

**Net accounting.** Removed: a 45 MB sidecar, subprocess management, the tolerant JSON walker,
`Canonical()`'s symlink resolution, a full-catalog `PathIndex()` load per run, a
`DependencyChecker` entry, half of `install-deps`, and an entire decode pass over the library.
Added: ~190 lines across four small classes. Roughly a wash in volume, and the failure modes move
out of an opaque subprocess and into code that can be unit-tested.

---

## 3. Kinds, and why the split is the safety-critical part

`DupeKind` was `{ Exact, Similar }`. Everything not byte-identical landed in `Similar`, and the
bulk "select duplicate extras" command will happily select every non-keeper in every group.

That is safe for byte-identical files. It is **not** safe for a burst of five frames of the same
scene, where the "duplicates" are five different photographs and only one of them has the kid
smiling. Widening detection without splitting the kind turns a triage tool into a shredder.

```csharp
public enum DupeKind
{
    Exact,    // byte-identical (SHA-256). Auto-select safe.
    Variant,  // same framing: resized, re-encoded, reformatted, rotated. Auto-select safe.
    Burst,    // same scene seconds apart. Review-only — NEVER auto-selected.
}
```

`Variant` is a rename of the old `Similar`; existing rows migrate in place (§8). `Reframe` was
considered and dropped (§9).

**The invariant this buys:** every bulk action filters to `Exact | Variant`. `Burst` groups are
shown, navigable and individually selectable, and can never be swept up by a "select all extras"
action. This is a safety invariant in the DESIGN §7 sense — it belongs in the same list as
"nothing is hard-deleted until hash-verified".

The rule sits on `AppState.InScope(SelectionScope.DuplicateExtras, …)` — the *predicate* the
selection layer reads, not the command — so the count on the Extras button, the reclaimable bytes
beside it and what pressing it actually selects all derive from one place and cannot drift apart.
`ReclaimableBytes` applies the same `DupeKindExtensions.IsBulkSelectable()` test for the same
reason. `SelectionCommandTests` locks both in.

Note `SelectionScope.DuplicateKeepers` is deliberately *not* filtered by kind: selecting keepers
is how the survivors get exported, and a burst's keeper is as much a survivor as any other.

### 3.1 The kinds overlap, so one group per relationship is enforced after detection

The three thresholds are nested rather than disjoint — Exact is a byte match, Variant accepts
everything within `VariantMaxBits`, Burst accepts everything within `BurstMaxBits` that also falls
in an EXIF time window. A set of byte-identical copies satisfies all three and was written three
times. Measured on a 38,668-photo library: **19 groups over 25 distinct photos**, every Exact group
shadowed by a Variant group with an identical member set, and the review flow showing the same pair
of photos as group 1 and again as group 12.

`GroupReconciler` runs after the detectors and drops any group whose members are *all* covered by a
single stronger group. Precedence is **Exact → Burst → Variant**, which is not most-to-least strict
but most-to-least trustworthy about what the photos are:

- **Exact wins outright.** Byte-identical files are copies and nothing outweighs that. Critically, a
  copy inherits the original's EXIF, so copies look exactly like frames captured at one instant and
  were being swept into Burst groups and withheld from bulk selection — the tool refusing to reclaim
  the most certain duplicates it can find.
- **Burst beats Variant**, which is the non-obvious direction and the safety-critical one.

**Why pixel distance cannot decide this.** Measured on real files with the production decode path:

| Relationship | Distance |
|---|---|
| One photo vs its 50% resize | 16 bits |
| One photo vs its PNG re-encode | 14 bits |
| One photo vs a q35 re-encode | 8 bits |
| **Two different frames of one burst** | **9 bits** |
| Unrelated photos (control) | 98–119 bits |

A *different photograph* sits closer (9) than the same photo resized (16). No threshold separates
them, which is why the disjoint-bands idea was rejected and why capture time is the only usable
discriminator. When both detectors claim the same photos, the one that consulted the clock is the
one to believe — and believing it errs toward review rather than toward Delete.

Only exact cover is dropped. Groups that merely overlap are two different claims about two
different sets and both survive; collapsing those would merge photographs the complete-linkage
grouper deliberately kept apart.

### 3.2 Known cost, and the clean fix

`DateTimeOriginal` has one-second resolution, so this rule cannot distinguish *re-encodes sharing a
capture second* from *burst frames shot within one second*. It resolves that ambiguity
conservatively: both are treated as a burst, so a resized copy whose timestamp matches its original
is labelled Burst and withheld from bulk selection. It stays in the review flow and stays
individually selectable — it is simply not swept.

That is the right way round for a tool that deletes things ("a miss costs less than a false
positive"), but it is a real cost: the headline reclaimable figure is smaller than it could be, and
one photo at two sizes is described as "the same scene, seconds apart".

The clean fix is `SubSecTimeOriginal`, which most cameras write alongside `DateTimeOriginal`.
Sub-second capture times separate the two cases exactly — identical instant means one photograph,
37 ms apart means two — and would let the conservative fallback shrink to the genuinely
undecidable case of photos with no sub-second data. Not done here; it needs `ExifExtractor` to read
the tag and a schema column to store it.

---

## 4. The hash

**Grid: 17 × 17, gradient (dHash), 272 bits.**

Square because rotation has to be well-defined; a 9×8 grid cannot be rotated 90° into itself.
17 wide gives 16 horizontal comparisons per row, 17 rows → 17 × 16 = **272 bits**, five `ulong`
words with 48 bits unused in the last.

Aspect ratio is deliberately destroyed by the downsample — every hash of this family does this,
and it is what makes the hash resolution-invariant. A 4000 × 3000 original and its 800 × 600
export land on the same grid and produce the same bits.

**Rotations: all four stored, never min-canonicalised.**

The tempting shortcut is to hash all four rotations and store the numerically smallest, so
rotated copies collide. It is broken. Canonical-form-plus-fuzzy-match fails because image noise
can flip which rotation wins the minimum: two near-identical photos canonicalise to *different*
orbit members and then never match at all. So all four are stored (4 × 5 words = 160 bytes/image,
8 MB at 50k) and matching takes the minimum distance over `A`'s four rotations against `B`'s
first. Detecting "A is B rotated by r" only needs one side rotated.

**Thresholds are expressed in bits out of 272 and are not portable.** The old
`--max-difference 10` was 10 bits out of czkawka's 256 at `--hash-size 16`. Any intuition
calibrated on that number has to be re-derived here against the fixture in §10. Defaults start
strict, because this feeds a tool that deletes things.

**Computed from the scan's existing decode.** `BlurDetector` already asks
`SkiaImageService.DecodeGray(path, 512)` for an aspect-preserving greyscale buffer. The 17 × 17
grid is box-averaged down from that same buffer. One decode, two signals, no new I/O — as against
czkawka's entire extra pass.

---

## 5. Detectors

| Kind | Signal | Cost | Auto-select |
|---|---|---|---|
| `Exact` | SHA-256 equality, pure SQL | negligible | yes |
| `Variant` | dHash distance ≤ `variant.maxbits`, over rotations | ~1 s at 50k | yes |
| `Burst` | same camera + EXIF timestamps within `burst.windowsec`, gated by dHash ≤ `burst.maxbits` | negligible | **no** |

`Burst` costs almost nothing because the EXIF window restricts candidate pairs to near-neighbours
in time before any hash is compared. It is off by default: bursts are not duplicates, and a user
who wants them should opt in.

---

## 6. Grouping — complete linkage, closest first

The core algorithm, and the one place a bug destroys photos.

A group is a **clique**: every member is within threshold of every other member. This is
complete-linkage clustering, and it makes chaining structurally impossible rather than merely
unlikely.

```
pairs = all (a, b, distance) with distance <= threshold
sort pairs by distance ascending, then by (a, b) for determinism

for each (a, b, d):
    ga, gb = group of a, group of b (or none)
    both unassigned      -> start a new group {a, b}
    one assigned         -> admit the other only if it is within threshold
                            of EVERY current member of that group
    both assigned, same  -> nothing to do
    both assigned, diff  -> merge only if every cross pair is within threshold
```

Sorting closest-first means the tightest matches claim each other before looser ones get a say,
so the resulting groups are the ones a human would draw. Determinism comes from the tiebreak on
ids: the same catalogue must produce the same groups on every run, or the keeper the user chose
last week lands on a different photo this week.

Group sizes are small in practice (2–10), so the "within threshold of every member" check is
cheap. It is O(group size) per admission, not O(n).

**Pair-count cap.** A pathological library (thousands of near-identical frames) could produce a
quadratic number of in-threshold pairs. The finder caps the pair list and, if the cap is hit,
**says so in the run's status message**. A silently truncated result reads as "we checked
everything" when we did not.

---

## 7. Settings

Per-detector configuration, so the user chooses what to spend time on.

Stored in the existing **`meta` table**, not in `settings.json`.

`settings.json` does exist (`DependencyChecker.StoredSettings`, holding
`SuppressDependencyPrompt`) and remains the right home for app-level preferences. Dedup settings
go in `meta` for a different reason: `images.dupe_checked_kinds` (§8) records which detectors
covered each row, and that record is only meaningful against the setting it was produced under.
Both must reset together when `catalog.db` is deleted, which is exactly the argument the `meta`
schema comment already makes for `scan_root`.

```
dedup.variant.enabled    bool   true
dedup.variant.maxbits    int    20      // of 272
dedup.variant.rotations  bool   true
dedup.burst.enabled      bool   false   // bursts are not duplicates — opt in
dedup.burst.windowsec    int    3
dedup.burst.maxbits      int    60      // of 272; deliberately loose
```

Exact detection has no toggle. It is free, it is exact, and a duplicate-finder that cannot find
identical files is not one.

---

## 8. Schema

```sql
-- new columns on images
phash              BLOB,     -- 160 bytes: 4 rotations x 5 ulong words. Null = not hashed yet.
dupe_checked_at    INTEGER,  -- (shipped earlier) unix utc of the last completed run
dupe_checked_kinds INTEGER   -- bitmask of DupeKind values that run actually covered
```

`dupe_checked_kinds` is what keeps the folder tree honest once detectors are configurable.
Without it, turning burst detection on tomorrow leaves every folder still claiming to be checked.
With it, `FolderNode.Deduped` counts a row as covered only when its mask contains every currently
enabled kind — so enabling a detector correctly re-flags every folder as pending.

`phash` is cleared by `ImageRepository.Upsert` on conflict, alongside `dupe_checked_at`: a file
whose bytes moved needs re-hashing, and a verdict computed against its previous content is not a
verdict about this file.

**Migration.** `Database.AddColumnIfMissing` handles the columns. Existing `dupe_groups` rows with
`kind='similar'` are rewritten to `kind='variant'` in the same migration — they were produced by
czkawka at a comparable strictness, and discarding them would throw away keeper decisions the user
already made.

---

## 9. Explicitly dropped

Recorded so the gaps are decisions rather than oversights.

| Dropped | What it costs us |
|---|---|
| CLIP/DINOv2 embeddings | **Aggressive crops and reframes are not detected.** No grid hash can find these. This is a real capability we are choosing not to have, to avoid a second ~300 MB model, ~12 min of one-time CPU inference over 50k photos, and an ANN index. |
| Tiled dHash (3×3 overlapping tiles) | Same gap, milder — would catch crops retaining ≳50% of the frame. Dropped from v1 as unvalidated: it needs its own column, threshold and toggle. Purely additive later. |
| `DupeKind.Reframe` | Follows from the two above: no detector, so no kind. |
| BK-tree / multi-index hashing | Nothing at ≤50k, where brute force wins on simplicity. Revisit past ~150k photos. |
| czkawka's reference-folder mode, size filters, alternate hash algorithms | Never used any of them. |
| HEIC / AVIF / JXL / RAW hashing | Already absent — SkiaSharp cannot decode them, so they were never in the catalog. Unchanged. |

---

## 10. Fixture and testing

The grouper and the thresholds cannot be tuned by inspection. `tests/SnapZap.Tests/fixtures`
gains a **generated** similarity fixture — generated rather than committed, so the repo stays
small and the images are reproducible:

- an original, plus a 4× downscale, a JPEG re-encode at low quality, and a format change → all
  must land in one `Variant` group
- the same original rotated 90/180/270 → must join it when `variant.rotations` is on, and not
  when it is off
- a near-miss chain (A~B~C where A≁C) → must **not** collapse into one group
- unrelated images → must not group at all

The chain case is the one that matters. It is the test that fails if someone replaces the
complete-linkage grouper with a union-find because it looked simpler.

---

## 11. Order of work

1. `DedupSettings` over `meta`
2. `DupeKind` split + schema + migration
3. `PerceptualHash` (272-bit, 4 rotations, distance)
4. Scanner integration — hash off the existing decode
5. `SimilarityGrouper` + its tests ← *the risky part, lands with tests around it*
6. `VariantFinder`, `BurstFinder`
7. `DuplicateService` rewrite; delete `CzkawkaFinder`
8. UI: settings editor, kind filters, the bulk-selection gate in `AppState.InScope`
9. Fixture + threshold tuning
10. Docs: DESIGN.md §2, CLAUDE.md, README, `install-deps`
