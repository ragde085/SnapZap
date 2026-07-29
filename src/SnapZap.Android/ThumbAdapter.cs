using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using SnapZap.Core;

namespace SnapZap.Droid;

/// <summary>
/// Grid adapter over scanned photos, reading the thumbnails <c>Scanner</c> already wrote — the
/// same content-addressed cache the desktop serves from <c>/api/thumb/{hash}</c>. Nothing here
/// decodes an original.
///
/// This is where the native head earns its keep over the WebView architecture: no HTTP, no
/// content-addressed URLs, no cache headers, no scroll-windowing maths in JavaScript. Android's
/// own view recycling does the windowing that <c>PhotoGrid</c>/<c>interop.js</c> had to implement
/// by hand because <c>&lt;Virtualize&gt;</c> could not resolve a viewport inside that flex layout
/// (CLAUDE.md). The single highest-risk item in the whole port plan — AC-6.5's scroll-windowing
/// regression across two OEM WebView builds — simply does not exist in this design.
/// </summary>
/// <param name="isSelected">
/// Optional selection test, for the handoff's selection mode (screen 9). Optional with a default
/// so callers that show a plain grid — the library before selection is entered, the Folders
/// screen — are unaffected and keep compiling unchanged.
/// </param>
public sealed class ThumbAdapter(Context context, IReadOnlyList<ImageRecord> items, int cellPx,
                                 string thumbDir, Func<long, bool>? isSelected = null)
    : BaseAdapter<ImageRecord>
{
    // Small LRU-ish cache. Bounded because a 40k-photo library's worth of decoded bitmaps is not
    // something to hold: at ~256px ARGB_8888 that is ~256 KB each.
    readonly Dictionary<string, Bitmap?> _cache = [];
    readonly Queue<string> _order = new();
    const int MaxCached = 240;

    public override int Count => items.Count;
    public override ImageRecord this[int position] => items[position];
    public override long GetItemId(int position) => items[position].Id;

    public override View GetView(int position, View? convertView, ViewGroup? parent)
    {
        // A FrameLayout rather than a bare ImageView, so a selection badge can sit over the
        // corner. Recycled views are reused whole; the badge is added once and toggled.
        var cell = convertView as FrameLayout ?? BuildCell();
        var img = (ImageView)cell.GetChildAt(0)!;
        var badge = cell.GetChildAt(1)!;

        var rec = items[position];
        img.SetImageBitmap(Load(ThumbFor(rec)));

        if (isSelected is null) badge.Visibility = ViewStates.Gone;
        else
        {
            var on = isSelected(rec.Id);
            badge.Visibility = ViewStates.Visible;
            badge.Background = on
                ? Design.Fill(Design.Accent)
                : Design.Outline(Color.Argb(90, 0, 0, 0), Color.White, Design.Dp(context, 2));
            ((TextView)badge).Text = on ? "✓" : "";

            // The handoff outlines the whole tile when it is selected (.sel), not just the badge —
            // at three-up the badge alone is too small to read as state at a glance.
            img.SetPadding(on ? Design.Dp(context, 3) : 2, on ? Design.Dp(context, 3) : 2,
                           on ? Design.Dp(context, 3) : 2, on ? Design.Dp(context, 3) : 2);
            cell.SetBackgroundColor(on ? Design.Accent : Color.Transparent);
        }

        return cell;
    }

    FrameLayout BuildCell()
    {
        var f = new FrameLayout(context)
        {
            LayoutParameters = new AbsListView.LayoutParams(cellPx, cellPx),
        };

        var img = new ImageView(context);
        img.SetScaleType(ImageView.ScaleType.CenterCrop);
        img.SetPadding(2, 2, 2, 2);
        img.LayoutParameters = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);
        f.AddView(img);

        var badge = Design.TileBadge(context, "", false);
        badge.LayoutParameters = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Right,
            TopMargin = Design.Dp(context, 5),
            RightMargin = Design.Dp(context, 5),
        };
        f.AddView(badge);
        return f;
    }

    /// <summary>
    /// Content-addressed thumbnail path — <c>&lt;thumbs&gt;/&lt;hash[..2]&gt;/&lt;hash&gt;.jpg</c>, the
    /// same layout the desktop's <c>/api/thumb/{hash}</c> endpoint serves from. Derived from the
    /// hash rather than read from <see cref="ImageRecord.ThumbPath"/> so a row whose stored path
    /// is stale or null still renders, and so two files with identical bytes share one thumbnail
    /// (which is exactly what an exact-duplicate pair is).
    /// </summary>
    string? ThumbFor(ImageRecord rec)
    {
        if (!string.IsNullOrEmpty(rec.ThumbPath) && File.Exists(rec.ThumbPath)) return rec.ThumbPath;
        var h = rec.ContentHash;
        // System.IO.Path spelled out: `using Android.Graphics` puts Android.Graphics.Path in
        // scope, and the bare name is ambiguous.
        return h.Length < 2 ? null : System.IO.Path.Combine(thumbDir, h[..2], h + ".jpg");
    }

    Bitmap? Load(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_cache.TryGetValue(path, out var cached)) return cached;

        Bitmap? bmp = null;
        try { if (File.Exists(path)) bmp = BitmapFactory.DecodeFile(path); }
        catch (Exception) { /* a thumbnail that will not decode is a blank cell, not a crash */ }

        _cache[path] = bmp;
        _order.Enqueue(path);
        while (_order.Count > MaxCached)
        {
            var evict = _order.Dequeue();
            if (_cache.Remove(evict, out var old) && old is not null && !old.IsRecycled) old.Recycle();
        }
        return bmp;
    }
}
