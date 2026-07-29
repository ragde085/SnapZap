using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace SnapZap.Droid;

/// <summary>
/// The "Modernist" design system from the mobile handoff, translated once.
/// </summary>
/// <remarks>
/// <para>Source: <c>assets/SnapZap UI redesign-handoff mobile.zip</c> →
/// <c>_ds/modernist-.../styles.css</c> and <c>SnapZap Mobile.dc.html</c>. Those are prototypes in
/// HTML/CSS; the job is to match the visual output, not to mirror their markup. Every value here
/// is lifted verbatim from a CSS custom property or an inline style in the prototype, so there is
/// one place to check a colour against the source rather than a hex literal scattered per screen.</para>
///
/// <para>Two characteristics of this system are easy to lose and carry most of its personality:
/// <b>corners are square</b> (<c>--radius-*: 0px</c>, deliberately) and <b>dividers are heavy</b> —
/// 2px between sections, 1px within a list. Rounding a corner or thinning a rule makes it look like
/// a different product.</para>
///
/// <para>Type is Archivo at 400/600/800. Android has no Archivo, and the handoff's fallback chain
/// is <c>system-ui, sans-serif</c> — so the system font is the correct substitute rather than a
/// compromise. Weight is what carries the hierarchy here, not the face.</para>
///
/// <para><b>Accessible names live here, not at the call sites.</b> This system draws its controls
/// as bare glyphs — "‹", "✕", "›", "📁" — which a screen reader either reads literally or skips.
/// Every component below therefore either takes a description (<see cref="IconButton"/> requires
/// one) or builds its own from the text it was given (<see cref="ListRow"/>), and the purely
/// decorative parts — rules, gaps, leading glyphs — remove themselves from the tree. Putting this
/// in the shared components rather than per screen is the only version that stays true: the first
/// pass over twelve screens labelled nothing at all.</para>
/// </remarks>
public static class Design
{
    // ---- palette (styles.css :root) -------------------------------------------------------
    public static readonly Color Bg = Color.ParseColor("#f3f2f2");
    public static readonly Color Surface = Color.ParseColor("#eae9e9");
    public static readonly Color Text = Color.ParseColor("#201e1d");
    public static readonly Color Accent = Color.ParseColor("#ec3013");

    /// <summary>40% of ink, per <c>--color-divider</c>'s color-mix.</summary>
    public static readonly Color Divider = Color.Argb(102, 0x20, 0x1e, 0x1d);

    public static readonly Color Neutral300 = Color.ParseColor("#d7d3d3");
    public static readonly Color Neutral400 = Color.ParseColor("#bab6b6");
    public static readonly Color Neutral500 = Color.ParseColor("#9b9797");
    public static readonly Color Neutral600 = Color.ParseColor("#7d7979");
    public static readonly Color Neutral700 = Color.ParseColor("#605d5d");

    public static readonly Color Accent100 = Color.ParseColor("#fff2ef");
    public static readonly Color Accent600 = Color.ParseColor("#dd2b0f");
    public static readonly Color Accent700 = Color.ParseColor("#ae1800");

    /// <summary>Ink used as a background — the selection-mode header and the undo toast.</summary>
    public static readonly Color Ink = Color.ParseColor("#201e1d");

    // ---- metrics ---------------------------------------------------------------------------

    /// <summary>
    /// The prototype is drawn at 390×844 CSS px — an iPhone-class logical viewport, which is also
    /// what Android reports as density-independent pixels on a comparable phone. So a CSS px in the
    /// mock maps to a dp here 1:1, and every measurement below is the mock's own number.
    /// </summary>
    public static int Dp(Context c, float cssPx) =>
        (int)Math.Round(cssPx * c.Resources!.DisplayMetrics!.Density);

    /// <summary>Minimum touch target the handoff commits to: "Every target is at least 44px."</summary>
    public const float MinTarget = 44f;

    // ---- text ------------------------------------------------------------------------------

    /// <summary>Section label: 10px, 800-ish, wide tracking, upper-cased. The system's signature.</summary>
    public static TextView Eyebrow(Context c, string s)
    {
        var t = new TextView(c) { Text = s.ToUpperInvariant() };
        t.SetTextSize(Android.Util.ComplexUnitType.Sp, 10f);
        t.SetTypeface(null, TypefaceStyle.Bold);
        t.LetterSpacing = 0.15f;                       // .eyeb letter-spacing:.15em
        t.SetTextColor(Neutral700);
        return t;
    }

    public static TextView Title(Context c, string s, float sp = 22f)
    {
        var t = new TextView(c) { Text = s };
        t.SetTextSize(Android.Util.ComplexUnitType.Sp, sp);
        t.SetTypeface(null, TypefaceStyle.Bold);
        t.LetterSpacing = -0.02f;                      // .ttl letter-spacing:-.02em
        t.SetTextColor(Text);
        return t;
    }

    public static TextView Body(Context c, string s, float sp = 14f, bool bold = false)
    {
        var t = new TextView(c) { Text = s };
        t.SetTextSize(Android.Util.ComplexUnitType.Sp, sp);
        if (bold) t.SetTypeface(null, TypefaceStyle.Bold);
        t.SetTextColor(Text);
        return t;
    }

    /// <summary>.note — 11.5px, dimmed, generous line height. Carries most of the explanatory copy.</summary>
    public static TextView Note(Context c, string s, Color? color = null)
    {
        var t = new TextView(c) { Text = s };
        t.SetTextSize(Android.Util.ComplexUnitType.Sp, 11.5f);
        t.SetTextColor(color ?? Neutral700);
        t.SetLineSpacing(0f, 1.5f);
        return t;
    }

    // ---- buttons ---------------------------------------------------------------------------

    public enum Btn { Primary, Secondary, Ghost }

    /// <summary>
    /// A .btn from the system. Square, Archivo-bold, and at least <see cref="MinTarget"/> tall.
    /// </summary>
    public static Button Button(Context c, string label, Btn kind = Btn.Secondary, float minHeight = MinTarget)
    {
        var b = new Button(c) { Text = label };
        b.SetTextSize(Android.Util.ComplexUnitType.Sp, 14f);
        b.SetTypeface(null, TypefaceStyle.Bold);
        b.SetAllCaps(false);                           // the system sets its own case; Android's
                                                       // default upper-casing fights the mock
        b.SetMinimumHeight(Dp(c, minHeight));
        b.SetPadding(Dp(c, 14), Dp(c, 8), Dp(c, 14), Dp(c, 8));
        b.StateListAnimator = null;                    // kills Material's elevation lift — this
                                                       // system has no shadows on controls

        switch (kind)
        {
            case Btn.Primary:
                b.Background = Fill(Accent);
                b.SetTextColor(Bg);
                break;
            case Btn.Secondary:
                b.Background = Outline(Bg, Divider, Dp(c, 1));
                b.SetTextColor(Text);
                break;
            case Btn.Ghost:
                b.Background = Fill(Color.Transparent);
                b.SetTextColor(Accent);
                break;
        }
        return b;
    }

    // ---- drawables -------------------------------------------------------------------------

    public static Drawable Fill(Color c)
    {
        var d = new GradientDrawable();
        d.SetColor(c);
        return d;   // no SetCornerRadius anywhere: --radius-* is 0 by design
    }

    public static Drawable Outline(Color fill, Color stroke, int strokePx)
    {
        var d = new GradientDrawable();
        d.SetColor(fill);
        d.SetStroke(strokePx, stroke);
        return d;
    }

    /// <summary>A horizontal rule. 2px separates sections, 1px separates rows — see remarks.</summary>
    public static View Rule(Context c, float px = 1f, Color? color = null)
    {
        var v = new View(c);
        v.SetBackgroundColor(color ?? Divider);
        v.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Math.Max(1, Dp(c, px)));
        v.ImportantForAccessibility = ImportantForAccessibility.No;   // decoration, not content
        return v;
    }

    /// <summary>The accent-tinted callout: pale fill with a 4px accent bar down the left.</summary>
    public static LinearLayout Callout(Context c)
    {
        var l = new LinearLayout(c) { Orientation = Orientation.Vertical };
        l.SetBackgroundColor(Accent100);
        l.SetPadding(Dp(c, 12), Dp(c, 11), Dp(c, 12), Dp(c, 11));

        // The left bar is drawn as an inset layer rather than a nested view, so the callout stays
        // one view and its padding maths does not have to account for a sibling.
        var bar = new GradientDrawable();
        bar.SetColor(Accent);
        var fill = new GradientDrawable();
        fill.SetColor(Accent100);
        var layers = new LayerDrawable([bar, fill]);
        layers.SetLayerInset(1, Dp(c, 4), 0, 0, 0);
        l.Background = layers;
        return l;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════
    //  Shared components
    //
    //  Everything the handoff's twelve screens build from. Kept here rather than per-screen so
    //  a tag on the review queue and a tag on the preview are the same object — the system's
    //  whole premise is that these are shared parts, and a screen that rolls its own is how a
    //  design system quietly stops being one.
    // ══════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// .tag — a small square label. Accent for state the app chose ("KEEPING THIS ONE"), neutral
    /// for state that is simply a fact ("BURST").
    /// </summary>
    public static TextView Tag(Context c, string text, bool accent = false)
    {
        var t = new TextView(c) { Text = text.ToUpperInvariant() };
        t.SetTextSize(Android.Util.ComplexUnitType.Sp, 10f);
        t.SetTypeface(null, TypefaceStyle.Bold);
        t.LetterSpacing = 0.08f;
        t.SetPadding(Dp(c, 7), Dp(c, 3), Dp(c, 7), Dp(c, 3));
        if (accent) { t.Background = Fill(Accent); t.SetTextColor(Bg); }
        else { t.Background = Outline(Color.Transparent, Divider, Dp(c, 1)); t.SetTextColor(Neutral700); }
        return t;
    }

    /// <summary>.kv — an eyebrow label on the left, the value bold on the right, hairline beneath.</summary>
    public static View KeyValue(Context c, string label, string value)
    {
        var wrap = new LinearLayout(c) { Orientation = Orientation.Vertical };
        var row = new LinearLayout(c) { Orientation = Orientation.Horizontal };
        row.SetPadding(0, Dp(c, 7), 0, Dp(c, 7));

        var l = Eyebrow(c, label);
        l.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        row.AddView(l);

        var v = Body(c, value, 13f, bold: true);
        v.Gravity = GravityFlags.Right;
        row.AddView(v);

        // The visible label is upper-cased with 0.15em tracking, which some screen readers spell
        // out letter by letter. Describe the pair from the caller's own casing instead.
        wrap.ContentDescription = $"{label}: {value}";

        wrap.AddView(row);
        wrap.AddView(Rule(c, 1f, Divider));
        return wrap;
    }

    /// <summary>
    /// .row — a 44dp-minimum list row: optional leading glyph, a title with an optional note
    /// under it, an optional trailing value, hairline beneath.
    /// </summary>
    public static LinearLayout ListRow(Context c, string title, string? note = null,
                                       string? trailing = null, string? glyph = null,
                                       bool highlight = false)
    {
        var wrap = new LinearLayout(c) { Orientation = Orientation.Vertical };

        var row = new LinearLayout(c) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetMinimumHeight(Dp(c, MinTarget));
        row.SetPadding(Dp(c, highlight ? 12 : 16), Dp(c, 13), Dp(c, 16), Dp(c, 13));
        if (highlight)
        {
            var bar = new GradientDrawable(); bar.SetColor(Accent);
            var fill = new GradientDrawable(); fill.SetColor(Accent100);
            var layers = new LayerDrawable([bar, fill]);
            layers.SetLayerInset(1, Dp(c, 4), 0, 0, 0);
            row.Background = layers;
        }

        if (glyph is not null)
        {
            var g = Body(c, glyph, 16f);
            g.SetPadding(0, 0, Dp(c, 10), 0);
            // "📁" and "›" are pictures of a thing, not the thing. The row's own description says
            // what it is; leaving these in the tree makes TalkBack read the emoji's Unicode name.
            g.ImportantForAccessibility = ImportantForAccessibility.No;
            row.AddView(g);
        }

        var col = new LinearLayout(c) { Orientation = Orientation.Vertical };
        col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        col.AddView(Body(c, title, 14f, bold: highlight));
        if (note is not null) col.AddView(Note(c, note, highlight ? Accent700 : null));
        row.AddView(col);

        if (trailing is not null)
        {
            var t = Body(c, trailing, 13f);
            t.SetTextColor(highlight ? Text : Neutral700);
            row.AddView(t);
        }

        // One node, read in the order it is laid out. Without this the row is either three separate
        // stops or — because the wrap is what callers hang Click on — a focusable node with no name.
        wrap.ContentDescription = string.Join(". ",
            new[] { title, note, trailing }.Where(s => !string.IsNullOrWhiteSpace(s)));

        wrap.AddView(row);
        wrap.AddView(Rule(c, 1f, Divider));
        return wrap;
    }

    /// <summary>
    /// A square icon-sized button. The handoff draws Lucide SVGs; there are no vector assets in
    /// this bundle, so a glyph stands in — a deliberate, visible deviation from pixel-parity
    /// rather than a silent one. Swap for real drawables when the icon set is exported.
    /// </summary>
    /// <param name="description">
    /// What a screen reader announces. <b>Required, and deliberately not optional</b>: the label is
    /// the entire accessible name of this control, because the visible text is a glyph — TalkBack
    /// otherwise reads "‹" or "✕" or nothing at all. Making it a parameter with no default is what
    /// stops the next icon button being added without one, which is exactly how every button in
    /// this app came to be unlabelled.
    /// </param>
    public static Button IconButton(Context c, string glyph, string description)
    {
        var b = Button(c, glyph, Btn.Secondary, 40f);
        b.SetTextSize(Android.Util.ComplexUnitType.Sp, 16f);
        b.SetPadding(0, 0, 0, 0);
        b.SetMinimumWidth(Dp(c, 40));
        b.LayoutParameters = new LinearLayout.LayoutParams(Dp(c, 40), Dp(c, 40));
        b.ContentDescription = description;
        return b;
    }

    /// <summary>A thin determinate bar. 4dp in the review header, 6dp on the scan screen.</summary>
    public static (View Track, View Fill) ProgressBar(Context c, float heightDp = 4f)
    {
        var track = new FrameLayout(c);
        track.SetBackgroundColor(Neutral300);
        track.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(c, heightDp));
        var fill = new View(c);
        fill.SetBackgroundColor(Accent);
        fill.LayoutParameters = new FrameLayout.LayoutParams(0, ViewGroup.LayoutParams.MatchParent);
        track.AddView(fill);
        return (track, fill);
    }

    /// <summary>.input — surface fill, hairline border, square.</summary>
    public static EditText Field(Context c, string value, float minHeight = 46f)
    {
        var e = new EditText(c) { Text = value };
        e.SetTextSize(Android.Util.ComplexUnitType.Sp, 14f);
        e.SetTextColor(Text);
        e.SetMinimumHeight(Dp(c, minHeight));
        e.Background = Outline(Surface, Divider, Dp(c, 1));
        e.SetPadding(Dp(c, 10), Dp(c, 6), Dp(c, 10), Dp(c, 6));
        e.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        return e;
    }

    /// <summary>
    /// The bottom action bar every full-screen task ends in: a 2px rule, surface fill, and the
    /// primary action at thumb height. Kept identical across review, selection and export so the
    /// commit button never moves between screens.
    /// </summary>
    public static LinearLayout ActionBar(Context c)
    {
        var wrap = new LinearLayout(c) { Orientation = Orientation.Vertical };
        wrap.SetBackgroundColor(Surface);
        wrap.AddView(Rule(c, 2f, Divider));
        var inner = new LinearLayout(c) { Orientation = Orientation.Vertical };
        inner.SetPadding(Dp(c, 16), Dp(c, 12), Dp(c, 16), Dp(c, 12));
        wrap.AddView(inner);
        wrap.Tag = inner;                 // callers add to Tag; see ActionBarBody
        return wrap;
    }

    /// <summary>The content slot of an <see cref="ActionBar"/>.</summary>
    public static LinearLayout ActionBarBody(LinearLayout actionBar) => (LinearLayout)actionBar.Tag!;

    /// <summary>
    /// The ink bar used for undo — dark fill, light text, action on the right. The handoff floats
    /// it over the content rather than pushing layout, so it is meant to sit in a FrameLayout.
    /// </summary>
    public static LinearLayout InkBar(Context c, string message, string actionLabel, Action onAction)
    {
        var bar = new LinearLayout(c) { Orientation = Orientation.Horizontal };
        bar.SetGravity(GravityFlags.CenterVertical);
        bar.SetBackgroundColor(Ink);
        bar.SetPadding(Dp(c, 14), Dp(c, 12), Dp(c, 14), Dp(c, 12));

        var t = Body(c, message, 13.5f);
        t.SetTextColor(Bg);
        t.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        bar.AddView(t);

        var b = Button(c, actionLabel, Btn.Primary, 40f);
        b.Click += (_, _) => onAction();
        bar.AddView(b);
        return bar;
    }

    /// <summary>
    /// A header with a leading back/close control — the shape screens 5, 6, 8, 10, 11 and 12 all
    /// use. <paramref name="glyph"/> is "‹" for back and "✕" for close/dismiss, matching the
    /// handoff's chevron-left and X.
    /// </summary>
    /// <param name="backDescription">
    /// Overrides what the leading control announces. The default reads the glyph the way the
    /// handoff means it — "✕" closes, everything else goes back — which is right for every screen
    /// using this today; pass a string when a screen means something more specific.
    /// </param>
    public static LinearLayout HeaderBar(Context c, string glyph, Action onBack,
                                         string title, string? subtitle = null, View? trailing = null,
                                         string? backDescription = null)
    {
        var wrap = new LinearLayout(c) { Orientation = Orientation.Vertical };

        var row = new LinearLayout(c) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Dp(c, 16), Dp(c, 6), Dp(c, 16), Dp(c, 12));

        var back = IconButton(c, glyph, backDescription ?? (glyph == "✕" ? "Close" : "Back"));
        back.Click += (_, _) => onBack();
        var blp = new LinearLayout.LayoutParams(Dp(c, 40), Dp(c, 40)) { RightMargin = Dp(c, 10) };
        back.LayoutParameters = blp;
        row.AddView(back);

        var col = new LinearLayout(c) { Orientation = Orientation.Vertical };
        col.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        col.AddView(Title(c, title, 20f));
        if (subtitle is not null) col.AddView(Note(c, subtitle));
        row.AddView(col);

        if (trailing is not null) row.AddView(trailing);

        wrap.AddView(row);
        wrap.AddView(Rule(c, 2f, Divider));
        return wrap;
    }

    /// <summary>
    /// A tile badge — the small square marker in a grid cell's top-right corner. Accent when the
    /// tile is chosen, outlined when it is merely selectable.
    /// </summary>
    /// <param name="description">
    /// Null (the default) marks the badge decorative — correct when the tile it sits on already
    /// announces its own selection state, which is the common case. Pass a string only when the
    /// badge is the sole carrier of that state.
    /// </param>
    public static TextView TileBadge(Context c, string glyph, bool on, string? description = null)
    {
        var t = new TextView(c) { Text = glyph };
        if (description is null) t.ImportantForAccessibility = ImportantForAccessibility.No;
        else t.ContentDescription = description;
        t.SetTextSize(Android.Util.ComplexUnitType.Sp, 10f);
        t.SetTypeface(null, TypefaceStyle.Bold);
        t.Gravity = GravityFlags.Center;
        t.SetMinimumWidth(Dp(c, 18));
        t.SetMinimumHeight(Dp(c, 18));
        t.SetPadding(Dp(c, 4), 0, Dp(c, 4), 0);
        if (on) { t.Background = Fill(Accent); t.SetTextColor(Color.White); }
        else { t.Background = Outline(Color.Argb(90, 0, 0, 0), Color.White, Dp(c, 2)); t.SetTextColor(Color.White); }
        return t;
    }

    /// <summary>Standard vertical gap, in the mock's own px.</summary>
    public static View Gap(Context c, float dp)
    {
        var v = new View(c);
        v.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, Dp(c, dp));
        v.ImportantForAccessibility = ImportantForAccessibility.No;
        return v;
    }

    /// <summary>Full-width layout params, the default for every stacked control in this system.</summary>
    public static LinearLayout.LayoutParams Wide() =>
        new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
}
