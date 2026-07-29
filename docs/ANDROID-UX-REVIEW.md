# Android UX review — 2026-07-29 (post-1.3.0)

Written after driving every screen on an emulator, and checked against the code rather than
impressions. Ordered by user impact, not by effort. Each item says what was observed, why it
matters, and what was done.

The mobile handoff (`assets/SnapZap UI redesign-handoff mobile.zip`) is the reference for anything
described as "the design says".

> **Status: all nine are fixed**, plus the animated swipe gestures in §10 and three defects the work
> turned up on the way (§11). Each item keeps its original finding text — the record of what was
> wrong is the useful part — with a **Fixed** note saying where it landed. Verified on an API 36
> emulator against an eleven-photo fixture library; the real-device pass is still outstanding.

---

## 1. The app promises a Stop it does not have · **High**

The Scanning screen's own copy reads *"Stopping keeps what is analysed — re-scanning resumes."*
There is no Stop control, and no `CancellationToken` is threaded from the UI into `ScanAsync` at
all. The design draws a **Stop** button in the header of screen 2; it was not built.

Why it matters more than it looks: a first scan now defaults to the whole of internal storage (see
§2), so the first thing a real user does is start a scan that runs for minutes over folders they
never meant to include — and the app tells them they can stop it, and they cannot. The only exits
are backgrounding it (where the foreground service keeps it running, correctly) or force-quitting.

**Fixed** — `MainActivity._scanCts` threads a token through both `ScanAsync` and `DetectAsync`, and
Stop sits in the Scanning header per the design. Cancellation is caught as an outcome rather than a
failure: the partial results are already committed, so the catalogue is reloaded and the screen says
how many photos were kept. `_lastScan` is cleared so a partial run's totals are never shown as the
folder's totals.

## 2. The default scan target is the entire phone · **High**

First run defaults to `/storage/emulated/0` — everything, including `Android/data`, app caches,
WhatsApp media and downloads. On a real device that is tens of thousands of files, most of which
are not the user's photo library, and it is the path of least resistance because it is pre-filled.

The consequence is not just a slow first scan: the library then contains junk, duplicate groups
are drawn from it, and the whole review queue is polluted before the user has made a single choice.

**Fixed** — in `DirectoryRoots`, so both heads share it. `DefaultStart` resolves Android to the
first existing of `DCIM/Camera` → `DCIM` → `Pictures` → `Download`, falling back to primary storage;
`PhotoFolders` feeds first-run chips on Android and quick-jumps in the desktop picker. Desktop's
default start deliberately did not move — a home folder is not "the entire drive", and relocating
where an existing install's picker opens is a change nobody asked for. Pinned by six new
`DirectoryRootsTests`, which need the injectable existence probe: none of these folders exist on a
Mac, so without it every Android assertion silently tested the fallback.

## 3. The review footer contradicts what the screen does · **High**

The footer reads *"Nothing is deleted here — this only decides which copy survives an export or
delete."* Swiping down on that same screen deletes the photo immediately (reversibly, via
`DeleteService`, but deleted nonetheless).

Both halves were deliberate — the sentence comes from the design, the gesture was an explicit
request — but shipped together they make the app tell the user something untrue about a
destructive action. That is the one place copy must not be wrong.

**Fixed** — the gesture stays and the sentence now describes it. The footer reads "Swipe left to
keep and advance · right to go back · down to move this photo to the trash, restorable from
History", and scopes the button row to what it actually governs.

## 4. Nothing is announced to a screen reader · **High (accessibility)**

`ContentDescription` appears **zero** times across the whole Android head. Every icon control is a
bare glyph — `‹` for back, `✕` for close and clear-scope, `📁` in folder rows, `›` chevrons — so a
TalkBack user hears the character, or nothing. The grid's photo tiles are unlabelled `ImageView`s.
Selection state is conveyed by an accent outline and a check glyph with no accessible equivalent.

The design's own brief commits to 44dp targets and calls out accessibility; targets were honoured
(verified: breadcrumb segments measure exactly 44dp) but labelling was not.

**Fixed**, mostly in `Design` rather than per screen. `IconButton` takes the description as a
**required** parameter, so the next icon button cannot be added without one. `ListRow` and `KeyValue`
build their own from the text they are given — `KeyValue` from the caller's casing, because the
visible label is upper-cased with wide tracking that some readers spell out letter by letter. Rules,
gaps, leading glyphs and the plan strip remove themselves from the tree. Grid cells announce filename
plus selection state; the review card names all three of its gestures, which is the only way a
screen-reader user could discover them; the scan counter and selection count are polite live regions.

## 5. No sort, and the "waiting" callout cannot be dismissed · **Medium**

The design's Library has a **Date taken** sort chip beside Filters, and an **✕** on the
duplicate-groups callout (it is a `sc-if` on `showNextStep` in the prototype, i.e. explicitly
dismissible). Neither was built. The grid is always in scan order, and the callout is permanent
until the last duplicate is resolved — so a user who wants to browse rather than triage has a
banner they cannot get rid of.

**Fixed** — a `SortKey` enum whose member names and nulls-last rule match the desktop's on purpose,
offering scan order, newest, oldest, name and largest. The desktop's `Blur` and `Nsfw` members are
left out: a phone chip row has no room for six, and NSFW is not scored on Android at all. The callout
takes an ✕ and hides for the session — the Review badge and Plan step 2 still carry the work.

## 6. There is no way to get the NSFW model onto the phone · **Medium**

The Plan tab's Content review step says *"Needs the NSFW model beside the app"* and offers no
action. On desktop the Setup panel downloads and checksums it. On Android the only route is `adb
push`, which no real user will do — so the step is permanently stuck at "not started" with no path
forward, which is worse than not listing it.

**Fixed** by taking the second option, deliberately. A download action would mean adding the
`INTERNET` permission to fetch a 328 MB model, and an app that reads an entire photo library and
*cannot* talk to the network is worth more than the feature. Step 3 now reads "Desktop only" with the
reason, marked with a dash instead of a number, and the header counts "of 3" rather than "of 5" — a
plan permanently 60% done by construction reads as a stalled app rather than a smaller feature set.

## 7. No settings at all · **Medium**

Android has no equivalent of the desktop's Setup: no dedup thresholds, no NSFW bands, no language
choice. The catalogue's `meta` settings are shared, so a folder deduplicated on the desktop with
custom thresholds behaves differently on the phone with no indication why.

**Fixed** — `SettingsActivity`. Shipped read-only first, then made editable on the same day after
the reviewer pushed back: a settings screen you cannot change is half an answer, and the re-detect
problem is solvable rather than a reason to defer. It is now deliberately the *same surface as the
desktop's `SetupDialog`* — control for control, same ranges, same step sizes — so neither head can
express a setting the other cannot. `BurstMaxBits` is exposed on neither, because a loose gate whose
only job is to stop a camera left running across two scenes from merging them is not a number anyone
can reason about. Changes save immediately (no Save button, same reasoning as the desktop: there is
nothing to cancel), and a callout tracks the fact that **these settings change what detection finds,
not how existing results are judged** — unlike the NSFW bands, nothing moves until detection runs
again, so the screen says so and offers to run it. Exact and Burst appear as "Always on" rather than
being omitted, since their absence
from a settings list reads as "not running". It also answers "how much space is SnapZap using",
which was unanswerable from inside an app whose data is invisible to the file manager.

## 8. Filtering to an empty result gives a blank grid · **Low**

Selecting a facet with no matches (e.g. Burst frames when there are none) shows an empty grid with
no message. The Filters sheet states the count before you commit, so this is recoverable, but the
resulting screen says nothing about why it is empty or how to get back.

**Fixed** — names the facet and the folder scope, and offers "Show everything".

## 9. Selection mode has no range gesture · **Low**

The design says *"Tap to add · hold and drag across for a range"*. Only tap-to-add exists; the
footer copy was changed to match reality rather than promise it. Fine as shipped, but selecting 200
extras one tap at a time is the kind of thing that makes people give up and use the desktop.

**Fixed** — hold, then keep dragging. This needed the grid to stop being rebuilt on every selection
change (see §11), because a `Render()` during the long-press destroyed the view the drag's touch
stream belonged to. The drag is add-only: one that toggled would flip photos back off as the finger
wandered, and that is not recoverable without watching every cell.

## 10. Swipe gestures gave no feedback at all · **High** (raised separately)

Not in the original nine — raised while the rest were being fixed, with a Bumble screenshot as the
reference. The gesture measured itself on `ACTION_UP` and did nothing in between: no movement, no
label, no way to tell whether you had passed the threshold or which of three actions you were about
to fire. On a screen whose down-swipe deletes a photo, a gesture with no preview and no abort is the
wrong shape.

**The four things every card-swipe app does are worth copying**, and `SwipeCard` now does them: the
card tracks the finger 1:1, rotates so it reads as a physical object being thrown, fades in a
direction-keyed stamp proportional to the drag, and springs back below the threshold. Crossing the
threshold fires a haptic tick, so the gesture arms without your having to watch for it.

**The mapping is deliberately not copied**, and this is the part that mattered. A dating app's left
and right are a symmetric binary on one object — reject or accept, both consume the card, both move
forward. These three directions are not symmetric at all, and animating them alike would have made
the app state something false:

- **Left — keep and advance** is the only true card-swipe here. A decision is made and the card is
  consumed, so it gets the full treatment.
- **Right — go back** is navigation. In card-swipe grammar a card leaving the deck means "that one
  is settled", and this settles nothing, so right is rubber-banded at 45% of the finger with no
  rotation — the iOS back-swipe feel rather than the disposal feel.
- **Down — move to the trash** is the destructive one, so it sits behind a threshold nearly twice as
  long as the horizontal one. A delete must not fall out of a sloppy keep. This follows the same
  instinct dating apps have, where the rare vertical gesture is the consequential one — not the
  routine horizontal.

Two details carry more than they look like. Each stamp sits on the edge **opposite** its direction
of travel, because the leading edge of a dragged card is already off-screen — the first build pinned
them the other way and the keep stamp was clipped in half exactly when it mattered. And the stamps
name the *action* ("✓ KEEP · NEXT GROUP"), not the state: the card already carries a permanent tag
reading "Keeping this one", so a stamp repeating it would say nothing about what letting go does.

**An undo bar** now follows a swipe-delete. Every one of these apps puts undo within reach of the
gesture rather than in a history screen, for the obvious reason: the whole premise of a fast gesture
is that the mistake it makes can be taken back just as fast. Trash & history was always the safety
net — it was just too far from the moment.

## 11. Defects the work turned up

Not findings from the review; things that were wrong and were found while fixing it.

- **Selecting a photo scrolled the grid back to the top.** Every tap in selection mode called
  `Render()`, which rebuilt the whole screen including the `GridView`. Selecting anything past the
  first screenful was effectively impossible. Fixed by putting the header and action bar in slots so
  selection changes swap only those; the grid is now left alone.
- **The scan target reset to the platform default on every launch** while the library still held
  photos from elsewhere, so the Plan tab printed one folder directly above a count of photos from
  another, and "Re-scan" would have read the wrong folder. Android now persists `scan_root` in
  `meta`, the same key the desktop has always used.
- **Restoring from history left the foreground service running** if the restore threw —
  `WorkService.Stop` was not in a `finally`, and a duplicated pair of Start/Stop calls from a bad
  merge sat alongside it.

One regression was introduced and caught here too: `DirectoryPickerActivity` used `DefaultStart` as
its browsing *ceiling*, so the moment that method learned to prefer `DCIM/Camera`, the picker could
no longer navigate above DCIM and labelled it "Internal storage". Where a scan starts and how far a
browser may walk up are different questions.

---

## What is working well, and should not be traded away

- **The review queue is the strongest screen in the app** and the reason the port is worth having.
  Keeper pre-picked with its reason stated, extras as a tappable filmstrip, one primary action.
- **The safety story is legible on screen**, not just in code: "Bursts are held back", "Keepers and
  bursts are not in here", and the two-variant removal warnings that distinguish "delete for good"
  from "only the record goes".
- **No `INTERNET` permission.** An app that reads an entire photo library and cannot talk to the
  network is a genuinely strong position. Defend it.
- **Progress and background safety** now hold the process across scan, dedup, delete and restore.
- **Touch targets meet the 44dp commitment** where measured.

---

## Still outstanding

- **The real-device pass.** Everything above was verified on an API 36 emulator against an
  eleven-photo fixture library. Gesture feel in particular — thresholds, resistance, spring timing —
  is the kind of thing an emulator cannot honestly judge, and the two test phones have not run it.
- **TalkBack itself has not been driven.** The labels are in place and were checked in the view
  hierarchy; nobody has navigated the app with the screen reader actually on.
- **Performance at real-library scale** is still unmeasured, as it was before this round.
