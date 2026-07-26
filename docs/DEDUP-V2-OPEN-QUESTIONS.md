# Dedup v2 — decisions taken, and what is still open

Found by QA against 38,668 real photos (`imagenet-mini`) plus a synthetic library built to hit
every detector path.

---

## Decided and implemented

The three kinds were correct in isolation and wrong in combination: their thresholds are nested,
not disjoint, so one photo could satisfy all three at once. That was the common case, not the
corner case.

| Was | Now |
|---|---|
| 19 groups over 25 distinct photos; every Exact group shadowed by a Variant group | 12 groups, **0 images in more than one kind** |
| "7 identical, 12 same shot groups" for 12 real findings | "7 identical, 5 same shot groups" |
| Review flow showed the same pair as group 1 *and* group 12 | each finding appears once |
| Burst detection off by default ⇒ burst frames grouped as Variant ⇒ bulk-selectable | burst detection unconditional; frames withheld |
| Copies inherit EXIF ⇒ swept into Burst ⇒ identical files *not* selectable | Exact outranks Burst; identical files always selectable |
| Reclaimable on the real library | unchanged at 914 KB — nothing lost |

Mechanism: `GroupReconciler` drops any group whose members are all covered by a single stronger
group, precedence **Exact → Burst → Variant**. Rationale and the measurements behind it are in
[DEDUP-V2.md §3.1](DEDUP-V2.md).

The precedence is deliberately *not* Exact → Variant → Burst, which is what it looks like it
should be. Measured with the production decode path:

| Relationship | Distance |
|---|---|
| One photo vs its 50% resize | 16 bits |
| **Two different frames of one burst** | **9 bits** |

A different photograph sits closer than the same photo resized, so no threshold separates them and
capture time is the only usable discriminator. That also rules out the "make the bands disjoint"
option entirely.

---

## Still open

### 1. Sub-second capture times *(the clean fix for a known cost)*

`DateTimeOriginal` has one-second resolution, so re-encodes sharing a capture second are
indistinguishable from burst frames shot within one second. The rule resolves that conservatively —
both are treated as a burst — so a resized copy whose timestamp matches its original is labelled
`Burst` and withheld from bulk selection. It stays in the review flow and stays individually
selectable, but the headline reclaimable figure is smaller than it could be and one photo at two
sizes is described as "the same scene, seconds apart".

`SubSecTimeOriginal` separates the two exactly. Needs `ExifExtractor` to read the tag, a schema
column, and `BurstFinder` to prefer it when present.

### 2. Bursts as a first-class concept rather than a duplicate kind

A burst is not a duplicate. Modelling it as a *series* in its own table would let a photo be an
exact duplicate *and* part of a burst without the two claims competing, and would remove the need
for precedence at all. Largest change of the set: new table, new review surface, migration.
Probably the right destination.

### 3. A burst with no EXIF is never protected

`BurstFinder` skips rows without a capture time, correctly — mtime is not capture time, and a bulk
copy would present a whole library as one burst. The consequence is that burst frames from a camera
that lost EXIF, or exported as PNG, match as Variants and stay bulk-selectable. Confirmed in the
fixture library (`noexif_0.png` / `noexif_1.png`). Documented behaviour rather than a bug, but it
is currently only documented in `docs/` — it belongs somewhere the user can see it.

---

## Smaller findings, unrelated to the above

| | Finding | Suggestion |
|---|---|---|
| **i** | No way to point the app at a scratch catalogue. `CatalogService` takes an `appDataDir`, but production always passes null, so any dev or QA run writes into the user's real catalog. | Honour a `PC_APP_DATA` env var, as `PC_NSFW_MODEL` already is. |
| **ii** | Scan progress shows a count with no total ("16,592"), so a multi-minute scan gives no sense of position. `BusyFraction` is null whenever `BusyTotal` is 0. | Enumeration is fast and already happens first — publish the file count as the total before analysis begins. |
| **iii** | `scripts/models/` exists as an empty untracked directory. | Remove, or add to `.gitignore` if it is a build artifact. |
