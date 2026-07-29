using Android.App;
using Android.Content;
using Android.Views;
using Android.Widget;

namespace SnapZap.Droid;

/// <summary>
/// Screen 7 of the mobile handoff — the Filters sheet.
/// </summary>
/// <remarks>
/// <para>A sheet rather than a column, which is the handoff's own framing: "Filters and Folders
/// are sheets, not columns." It keeps every facet count the desktop rail shows and ends in a
/// button that states the <i>result</i> — "Show 1,204 photos" — rather than a bare OK, so the
/// consequence of the choice is legible before it is committed.</para>
///
/// <para>Built as a <see cref="Dialog"/> anchored to the bottom rather than as an overlay inside
/// the activity: that gets the scrim, the outside-tap dismissal and correct back-button handling
/// from the platform instead of re-implementing three things that are easy to get subtly wrong.</para>
///
/// <para>A facet with nothing behind it is shown greyed with its reason beside it rather than
/// hidden — the desktop does the same. A filter that silently disappears reads as a missing
/// feature; one that says "not scored" tells you what to do about it.</para>
/// </remarks>
public sealed class FiltersSheet(Activity host, string current, Dictionary<string, int> counts,
                                 Action<string> onChosen)
{
    public void Show()
    {
        var dialog = new Dialog(host);
        dialog.RequestWindowFeature((int)WindowFeatures.NoTitle);

        var sheet = new LinearLayout(host) { Orientation = Orientation.Vertical };
        sheet.SetBackgroundColor(Design.Bg);

        // The grab handle and the 2px top rule are what make it read as a sheet rather than a
        // dialog that happens to be at the bottom.
        sheet.AddView(Design.Rule(host, 2f, Design.Text));
        var grab = new View(host);
        grab.SetBackgroundColor(Design.Neutral500);
        var glp = new LinearLayout.LayoutParams(Design.Dp(host, 40), Design.Dp(host, 4))
        { TopMargin = Design.Dp(host, 8), BottomMargin = Design.Dp(host, 8) };
        glp.Gravity = GravityFlags.CenterHorizontal;
        grab.LayoutParameters = glp;
        sheet.AddView(grab);

        // header
        var head = new LinearLayout(host) { Orientation = Orientation.Horizontal };
        head.SetGravity(GravityFlags.CenterVertical);
        head.SetPadding(Design.Dp(host, 16), 0, Design.Dp(host, 16), Design.Dp(host, 11));
        var title = Design.Title(host, "Filters", 20f);
        title.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        head.AddView(title);
        var clear = Design.Button(host, "Clear all", Design.Btn.Ghost, 40f);
        clear.SetTextSize(Android.Util.ComplexUnitType.Sp, 13f);
        clear.Click += (_, _) => { onChosen("Any"); dialog.Dismiss(); };
        head.AddView(clear);
        sheet.AddView(head);
        sheet.AddView(Design.Rule(host, 2f, Design.Divider));

        var body = new LinearLayout(host) { Orientation = Orientation.Vertical };
        body.SetPadding(Design.Dp(host, 16), Design.Dp(host, 12), Design.Dp(host, 16), Design.Dp(host, 12));

        body.AddView(Design.Eyebrow(host, "Duplicates"));
        body.AddView(Design.Gap(host, 6));

        var chosen = current;
        void Facet(string key, string label, string? note = null)
        {
            var on = chosen == key;
            var row = Design.ListRow(host, label, on ? note : null,
                                     counts.TryGetValue(key, out var n) ? n.ToString("N0") : "0",
                                     null, highlight: on);
            row.Click += (_, _) => { onChosen(key); dialog.Dismiss(); };
            body.AddView(row);
        }

        Facet("Any", "Any");
        Facet("ExtrasOnly", "Extra copies only", "Identical and same-shot");
        Facet("KeepersOnly", "Keepers only");
        Facet("BurstFrames", "Burst frames", "Separate photographs");

        body.AddView(Design.Gap(host, 5));
        body.AddView(Design.Note(host, "Burst frames are separate photographs — no bulk path picks them."));
        body.AddView(Design.Gap(host, 16));

        // Facets that exist but have nothing behind them yet. Shown greyed with the reason, per
        // the handoff: "greyed reasons next to their fix".
        body.AddView(Design.Eyebrow(host, "Content review"));
        body.AddView(Design.Gap(host, 6));
        var likely = Design.ListRow(host, "Likely explicit", null, "not scored");
        likely.Alpha = 0.45f;
        body.AddView(likely);
        var unsure = Design.ListRow(host, "Not sure — worth a look", null, "not scored");
        unsure.Alpha = 0.45f;
        body.AddView(unsure);
        body.AddView(Design.Gap(host, 9));
        body.AddView(Design.Note(host,
            "Content review needs the NSFW model beside the app. Everything else works without it."));

        var scroll = new ScrollView(host);
        scroll.AddView(body, new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        scroll.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);
        sheet.AddView(scroll);

        // The sheet ends in a button that states the result, not a bare OK.
        var footer = Design.ActionBar(host);
        var shown = counts.TryGetValue(chosen, out var c) ? c : counts.GetValueOrDefault("Any");
        var apply = Design.Button(host, $"Show {shown:N0} photos", Design.Btn.Primary, 52f);
        apply.LayoutParameters = Design.Wide();
        apply.Click += (_, _) => dialog.Dismiss();
        Design.ActionBarBody(footer).AddView(apply);
        sheet.AddView(footer);

        dialog.SetContentView(sheet);

        if (dialog.Window is { } w)
        {
            w.SetGravity(GravityFlags.Bottom);
            w.SetLayout(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            w.SetBackgroundDrawable(Design.Fill(Design.Bg));
        }
        dialog.Show();
    }
}
