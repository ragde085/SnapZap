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
}
