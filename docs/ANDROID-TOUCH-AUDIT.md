# Android touch audit

Companion to [ANDROID-PORT-ACS.md](ANDROID-PORT-ACS.md) AC-6.1/AC-6.2 and
[ANDROID-PORT-PLAN.md](ANDROID-PORT-PLAN.md) §4 task 9. The plan named one instance
(`.undo-thumb-restore`) and said "audit the rest, before calling this done." This is that audit.

**Note on file names:** `ANDROID-PORT-ACS.md` and `CLAUDE.md` refer to `FilterBar.razor` and
`Toolbar.razor`. Neither exists in this tree — that functionality lives inline in
`Components/Pages/Home.razor` (the `mhead`/`topbar` markup) and `Components/Rail.razor` (the
Filters tab). This audit covers the actual files: `app.css`, `Card.razor`, `Home.razor`,
`Rail.razor`, `FolderTreeView.razor`, `PhotoGrid.razor`, `UndoDialog.razor`, `SelectionBar.razor`,
`PreviewModal.razor`, `DupeReview.razor`, `JumpSearch.razor`, `HelpDialog.razor`, and
`interop.js`. CLAUDE.md's own `Components/` listing should be corrected to match at some point;
not done here since this is a docs-only change and that correction is outside AC-6.1/6.2.

---

## 1. Method

`app.css` has 27 `:hover` rules (confirmed by direct count, not the plan's estimate). Every one
was read in its declaration context, cross-referenced against the Razor markup it applies to, and
classified as one of:

- **hover-reveal** — the affordance is invisible, non-interactive, or effectively unreachable
  until `:hover` (e.g. `opacity: 0` lifted by a hover selector). Breaks on touch: there is no
  hover event, so the affordance never appears. **Needs a fix.**
- **hover-styling** — the element is already visible and already tappable; `:hover` only changes
  its background/color/text-decoration as a pointer-proximity cue. Touch has no equivalent
  sensation, but nothing is lost functionally — a tap still fires `@onclick` whether or not the
  element ever showed its hover color. **Needs no fix.**

## 2. Hover-reveal affordances (need a touch fix)

Exactly **one** rule in `app.css` hides an interactive element until `:hover` (or `:focus-visible`,
or a `.restoring` state class). Every other rule in the file is hover-styling.

| Selector | File:line | What it reveals | Keyboard path exists? | Proposed touch treatment | Desktop regression risk |
|---|---|---|---|---|---|
| `.undo-thumb-wrap:hover .undo-thumb-restore` | `app.css:975-986`; markup at `UndoDialog.razor:48-69` | A small circular "restore this one photo" button overlaid on a 36×36px thumbnail in the History dialog's per-batch thumbnail strip. Base state is `opacity: 0`; `:hover`, `:focus-visible`, or the `.restoring` class (set while a restore is in flight) raise it to `opacity: 1`. | Yes — `.undo-thumb-restore:focus-visible { opacity: 1 }` (`app.css:983`) already pairs with the button being a real, tabbable `<button>` in the DOM (it's never `display:none`, just `opacity:0` — so Tab reaches it and screen readers see it regardless of hover state). Keyboard/AT users are unaffected today. | **Always-shown on touch**, not tap-to-reveal. A `36px` thumbnail with a genuinely tap-to-reveal control fails the AC-6.4 48dp minimum target *and* a hidden-then-revealed-by-tap control adds an extra tap before the "real" action, which is the wrong trade for a destructive-adjacent-but-actually-helpful action (single-item restore). Practical implementation: add a media query or a `pointer: coarse` check (`@media (hover: none)`) that forces `opacity: 1` unconditionally, or promote the always-shown state to whatever `IAppHost`/UA string the Android WebView reports. `(hover: none)` is the standard, UA-agnostic way to detect "no hover-capable pointer" and needs no platform branching in Razor. | Low. The CSS-media-query approach only changes behavior on `hover: none` devices; desktop mouse/trackpad behavior (opacity 0 until hover/focus) is untouched. The existing `:focus-visible` rule must stay exactly as-is — don't fold it into the new rule in a way that could regress keyboard access while "simplifying." |

**AC-6.3 disposition:** this is the affordance AC-6.3 names by hand (`.undo-thumb-restore` at
`app.css:977-984` in the plan's citation — the rule itself starts at `975` counting the comment,
`977` counting the first declaration; both point at the same block). The fix above is scoped
narrowly enough to be a `🟡 SDK` task once an Android head exists to test `hover: none` against a
real WebView — CSS media queries are the correct mechanism, but confirming Android's WebView
reports `(hover: none)` rather than `(hover: hover)` on a touchscreen is unverified until then.

## 3. Hover-styling only (no fix needed) — full accounting of the remaining 26

Listed for completeness, so a future reader doesn't have to re-derive that these are safe. All 26
either recolor/rebackground an already-visible, already-tappable element, or (for the three
`<details><summary>` rules) recolor text on a summary line that opens/closes on click/tap either
way — native `<details>` disclosure needs no hover at all.

| # | Selector | File:line | Element already visible/tappable without hover? |
|---|---|---|---|
| 1 | `.btn-primary:hover` | `app.css:253` | Yes — primary buttons throughout |
| 2 | `.btn-secondary:hover` | `app.css:257` | Yes |
| 3 | `.btn-ghost:hover` | `app.css:263` | Yes |
| 4 | `.btn-danger:hover` | `app.css:267` | Yes |
| 5 | `.input:hover` | `app.css:324` | Yes — text field border tint |
| 6 | `.radio:hover .dot` | `app.css:334` | Yes — radio dot outline tint |
| 7 | `.seg-opt:not(:has(input:checked)):hover` | `app.css:343` | Yes — segmented control option |
| 8 | `.table tbody tr:hover` | `app.css:357` | Yes — row background tint (History table rows) |
| 9 | `.jump-row:hover` | `app.css:429` | Yes — same tint `JumpSearch.razor` already applies via `@onmouseenter` for keyboard-arrow parity; tap fires `@onclick` regardless |
| 10 | `.icon-btn:hover` | `app.css:450` | Yes — topbar Extract/Help/Setup/lang/theme buttons, always visible |
| 11 | `.tab:hover` | `app.css:523` | Yes — Plan/Folders/Filters rail tabs |
| 12 | `.trow:hover` | `app.css:577` | Yes — folder tree row |
| 13 | `.trow-action:hover:not(:disabled)` | `app.css:601` | Yes — the per-folder rescan icon (`FolderTreeView.razor:214-218`) is unconditionally rendered, never `opacity:0`; it's `tabindex="-1"` by design (roving tabindex owns the row instead — see the comment at `FolderTreeView.razor:211-213`) but that only affects the Tab key, not touch/click, which reach it fine either way |
| 14 | `.facet-row:hover` | `app.css:623` | Yes — Filters tab option rows |
| 15 | `.grid.has-selection .card:not(.selected):hover` | `app.css:782` | Yes — dims non-selected cards to 0.4 opacity while a selection is active, hover lifts to 0.75; the card is fully visible and fully tappable at every opacity level, this is a preview-of-focus cue only |
| 16 | `.dir-row:hover` | `app.css:950` | Yes — `DirectoryPickerDialog` folder rows |
| 17 | `.review-card:hover` | `app.css:1022` | Yes — `DupeReview` candidate cards |
| 18 | `.modal .frame-select:hover` | `app.css:1128` | Yes — Preview modal's Select/Selected pill |
| 19 | `.modal .nav:hover:not(:disabled)` | `app.css:1148` | Yes — Preview modal's ‹/› buttons |
| 20 | `.issues-head:hover` | `app.css:1194` | Yes — Scan issues disclosure header |
| 21 | `.dep-copy:hover` | `app.css:1227` | Yes — "copy command" link text |
| 22 | `.dep-manual summary:hover` | `app.css:1230` | Yes — `<details>` summary, native disclosure |
| 23 | `.setup-rail-item:hover` | `app.css:1255` | Yes — Setup dialog's left rail items |
| 24 | `.setup-advanced summary:hover` | `app.css:1264` | Yes — `<details>` summary, native disclosure |
| 25 | `.help-nav button:hover` | `app.css:1291` | Yes — Help dialog's left nav |
| 26 | `.faq summary:hover` | `app.css:1333` | Yes — `<details>` summary, native disclosure |

None of these need touch work. Padding AC-6.1's table with all 26 would have obscured the one row
that matters, which is the mistake the ACS document explicitly warned against.

## 4. AC-6.2 — desktop-only interactions with no touch equivalent

### 4.1 Shift+click range select — real gap, no touch equivalent exists

`Card.razor:110` and `Card.razor:127` (`HandleClick`/`HandleKeyDown`) both check `e.ShiftKey`: if
held, the click/keypress extends the selection from `AppState.LastClickedId` to the clicked photo
via `AppState.SelectRange`. This is the only way today to select a contiguous run of photos
without tapping every one individually.

There is no touch gesture wired to it, and no touch-native equivalent (long-press-then-drag,
two-finger range, etc.) exists in `interop.js` or any component. On a 40k-photo library this is
the one interaction whose *absence* is user-visible pain, not just a missing nicety — selecting,
say, 200 duplicate extras one tap at a time is what the range-select shortcut exists to avoid on
desktop.

**Recommendation:** flag as a known v1 gap rather than block on it. Single-tap toggle-select still
works for every photo individually, and the bulk-selection commands already on screen
(`AppState.SelectBy(Scope.AllShown)` and the scope menu in `Home.razor:124-149`, "Select all N
shown" / flagged / not-sure / keepers) cover the most common bulk-selection needs without range
select at all. A touch range-select gesture (e.g. long-press a card to start a range, tap another
to close it) is a reasonable post-v1 addition, not a v1 blocker — see §5.

### 4.2 Keyboard shortcuts (`HelpDialog.razor:323-369`)

Grid navigation (arrows/Home/End/`X`/Space/Enter), Preview navigation (arrows/`X`/`I`/`+`/`-`/`0`/
Esc), duplicate-review picking (`1`-`9`/arrows/Esc), folder-tree navigation (arrows/Home/End), and
the global `Ctrl`/`Cmd`+`A` (select all shown) / `Ctrl`/`Cmd`+`D` (clear selection) pair
(`interop.js:108-129`).

Every one of these is an **accelerator for an action that already has an on-screen, tappable
control** — the zoom ladder buttons, the Details/Select/Close/‹/› buttons in Preview, the keeper
`@onclick` on each `DupeReview` card, the Select-all/Clear buttons in `SelectionBar.razor` and
`Home.razor`. None of them gate a capability that's otherwise unreachable by touch. **No touch
alternative needed for any of these** — they're simply absent on Android (no physical keyboard is
assumed), and that's fine because the tap path was never secondary to begin with.

`HelpDialog`'s shortcuts tab itself (`Keyboard` section, `HelpDialog.razor:323`) will show a list
of keys that mostly don't apply on a touch-only device. Not a functional bug — nothing breaks by
leaving it as-is — but worth a follow-up note: either hide/relabel that tab on Android, or leave it
(a keyboard shortcut list is harmless clutter, not a broken feature) — a product decision, not a
touch-audit finding, so left here as a note rather than a graded AC.

### 4.3 Right-click / context menus — not actually a gap

Grepped the whole `App/` tree for `oncontextmenu`: zero matches. **The app has no in-application
right-click menu at all.** The only "right-click" reference anywhere is copy text in
`UndoDialog.razor:92` (`L["RightClickHint"]`, resolving to *"You can also restore from the Recycle
Bin's own right-click menu"* — `Resources/UndoDialogResources.resx:27`). That sentence describes
the **operating system's** Recycle Bin shell context menu (Windows Explorer), not anything this
codebase renders. On Android there is no system Recycle Bin — the trash is app-private
(`docs/ANDROID-PORT-PLAN.md` §2, `AndroidTrashService`) — so the hint is not merely
touch-unreachable, it's describing a UI surface that doesn't exist on this platform at all.

**Recommendation:** not a touch-fix, but flag as a copy correctness issue for whoever wires the
Android UI — that hint text should be platform-conditioned (hidden or reworded) on Android, the
same class of fix as any other "this string assumes Windows Explorer" string. Out of scope for
AC-6.1/6.2 to fix (this doc is audit-only), noted here so it isn't lost.

### 4.4 Drag-select — not implemented on any platform

Grepped `interop.js` and every `.razor` file for `mousedown`/`dragstart`/`dragover`/click-drag
selection logic: none exists. There is no rubber-band/marquee multi-select feature in this
codebase today, desktop or otherwise. **N/A** — nothing to port, nothing to replace.

### 4.5 Export, Hide, Extract — out of scope for v1, not audited for touch

Per `docs/ANDROID-PORT-PLAN.md` §1, `ExportDialog.razor`, `HideDialog.razor`, `ExtractDialog.razor`
and their toolbar entry points are **not wired into the Android UI at all** for v1. Whatever hover
or modifier-key behavior those dialogs contain is therefore irrelevant to this audit — they are not
reachable, so there is nothing to make touch-reachable. (For the record: a scan of `ExportDialog`/
`HideDialog`/`ExtractDialog` found no additional hover-reveal patterns beyond the styling-only
kind already covered in §3, so even if scope changed later the list above would not grow much —
but that's incidental, not a finding this audit is asked to produce.)

## 5. Prioritized list

**Must fix for v1 (blocks AC-6.3):**
1. `.undo-thumb-restore` touch reveal (§2) — always-shown via `@media (hover: none)`, preserving
   the existing `:focus-visible` path unchanged. This is the only AC-6.1 item with a hard gate
   (AC-6.3) attached to it.

**Should decide explicitly, not silently ship broken or silently ship absent:**
2. Shift+click range select (§4.1) — no code change required for v1 to function, but the product
   owner should consciously accept "no bulk contiguous-range select on Android v1" rather than
   have it discovered as a bug report. Cheapest mitigation if it's judged necessary: long-press a
   card to arm range mode, tap a second card to close the range — small, isolated addition to
   `Card.razor`'s touch handling, not needed for AC-6.1/6.2/6.3 to pass.
3. `RightClickHint` copy in `UndoDialog.razor` (§4.3) — platform-condition or reword before
   Android ships; cosmetic, not blocking, but currently promises a menu that doesn't exist on the
   platform.

**Can wait / non-issues:**
4. Keyboard shortcuts list in `HelpDialog` showing irrelevant keys (§4.2) — cosmetic only.
5. Everything in §3 (26 hover-styling rules) — no work needed, ever, for touch; listed only so the
   next person doesn't have to re-derive that.
6. Drag-select (§4.4) — doesn't exist, nothing to do.
7. Export/Hide/Extract hover behavior (§4.5) — out of scope for v1 by product decision, not a
   touch gap.

## 6. Bottom line on the plan's "3-5 day" touch-pass estimate

**The estimate is too high for what AC-6.1-6.3 actually require, and too low if §4.1's range-select
gap is judged a launch blocker.**

- The *audit* (this document, AC-6.1/6.2) took a systematic read of 27 CSS rules and ~10
  components — a few hours, not days, because the actual hover-reveal surface is one rule, not the
  27 the plan's own estimate was sized against. The plan already flagged this risk in its §1.4
  correction ("the audit's actual target... is a much shorter list") but didn't shrink the day
  estimate to match.
- The one confirmed *code fix* (AC-6.3, `.undo-thumb-restore`) is a single CSS media-query change
  plus an on-device check that Android's WebView actually reports `(hover: none)` — half a day
  once a device/emulator exists, not a multi-day phase.
- AC-6.4 (48dp tap targets) and AC-6.5 (scroll-windowing regression) are the parts of "task 9" with
  real, unpredictable device-testing time attached — but those were always 🔴 DEVICE work
  requiring both physical phones, not desk-bound audit-and-fix work, and AC-6.5 in particular
  (see `docs/ANDROID-VERIFY.md`) is exactly the kind of "looks fine, fails on a specific OEM
  WebView build under real momentum scrolling" risk that can eat unbounded time if it reproduces.
- Net: the 🟢 NOW/🟡 SDK slice of the touch pass (audit + the one fix) is closer to **1 day** than
  3-5. The 🔴 DEVICE slice (AC-6.4, AC-6.5, plus re-verifying AC-6.3 on real hardware) is
  open-ended in the way all first-time-on-real-OEM-WebView work is, and could easily consume the
  3-5 days the plan budgeted for the whole phase, especially if AC-6.5 reproduces on either device
  and needs a fix-and-reverify loop. The plan isn't wrong about total effort so much as
  mis-attributing where within the phase that effort goes — it's not the audit-and-fix work, it's
  the device verification.
