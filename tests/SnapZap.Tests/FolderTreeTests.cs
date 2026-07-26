using SnapZap.App.Services;
using SnapZap.Core;
using Xunit;

namespace SnapZap.Tests;

/// <summary>
/// The tree is the left pane's only content, so it is how a folder gets picked. Its whole
/// contract with the rest of the app is one thing: every node's Path must be a string the
/// grid's folder filter (<c>AppState.Matches</c>) will match against the photos underneath it.
/// A node whose path is off by a character still renders, still shows a count, and still
/// highlights when clicked — it just filters the grid down to nothing.
/// </summary>
public class FolderTreeTests
{
    static ImageView Img(long id, string path) =>
        new(new ImageRecord { Id = id, Path = path, ContentHash = "h" + id }, null);

    /// <summary>The check that matters: does selecting this node show the photos in it?</summary>
    static int Matched(FolderNode node, IEnumerable<ImageView> images) =>
        images.Count(i => i.Folder == node.Path ||
                          i.Folder.StartsWith(node.Path + "/", StringComparison.Ordinal));

    static IEnumerable<FolderNode> Walk(FolderNode n)
    {
        yield return n;
        foreach (var c in n.Children)
            foreach (var d in Walk(c))
                yield return d;
    }

    [Fact]
    public void Every_node_path_selects_the_photos_beneath_it()
    {
        List<ImageView> images =
        [
            Img(1, "/Users/me/pics/2024/a.jpg"),
            Img(2, "/Users/me/pics/2024/b.jpg"),
            Img(3, "/Users/me/pics/2025/c.jpg"),
        ];

        var root = FolderTreeBuilder.Build(images, new Dictionary<long, DupeInfo>())!;

        foreach (var node in Walk(root))
            Assert.True(Matched(node, images) == node.TotalCount,
                $"node '{node.Path}' claims {node.TotalCount} photos but its path matches {Matched(node, images)}");
    }

    /// <summary>
    /// Two libraries under different filesystem roots share no directory prefix, so the common
    /// prefix is the empty string. Node paths used to be rebuilt by joining segments onto that
    /// empty root, which dropped the leading "/" — "Users/me/pics" against photos reporting
    /// "/Users/me/pics" — and every folder in the tree selected zero photos.
    /// </summary>
    [Fact]
    public void Node_paths_keep_their_leading_separator_when_roots_differ()
    {
        List<ImageView> images =
        [
            Img(1, "/Users/me/pics/a.jpg"),
            Img(2, "/Volumes/backup/photos/b.jpg"),
            Img(3, "/Volumes/backup/photos/c.jpg"),
        ];

        var root = FolderTreeBuilder.Build(images, new Dictionary<long, DupeInfo>())!;
        var nodes = Walk(root).Where(n => n.Path.Length > 0).ToList();

        Assert.All(nodes, n => Assert.StartsWith("/", n.Path, StringComparison.Ordinal));
        foreach (var node in nodes)
            Assert.Equal(node.TotalCount, Matched(node, images));

        // And the folders are actually reachable, not merely well-formed.
        var backup = nodes.Single(n => n.Path == "/Volumes/backup/photos");
        Assert.Equal(2, Matched(backup, images));
    }

    /// <summary>
    /// A row is labelled with its own segment, not its whole path. This is the ordinary case —
    /// one folder scanned, so there is a real common root — and it is exactly the case a
    /// "does this path look absolute?" test gets wrong, because every scanned path does.
    /// Nothing about it breaks filtering, so only an assertion on Name catches it.
    /// </summary>
    [Fact]
    public void Rows_are_labelled_with_their_own_segment()
    {
        List<ImageView> images =
        [
            Img(1, "/Users/me/pics/2024/a.jpg"),
            Img(2, "/Users/me/pics/2025/b.jpg"),
        ];

        var root = FolderTreeBuilder.Build(images, new Dictionary<long, DupeInfo>())!;
        var names = root.Children.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal);

        Assert.Equal(["2024", "2025"], names);
    }

    /// <summary>The one case where the full path IS the label: with no common root a bare
    /// "Volumes" would read as a relative folder rather than the top of a filesystem.</summary>
    [Fact]
    public void Top_row_keeps_its_full_path_when_there_is_no_common_root()
    {
        List<ImageView> images =
        [
            Img(1, "/Users/me/a.jpg"),
            Img(2, "/Volumes/backup/b.jpg"),
        ];

        var root = FolderTreeBuilder.Build(images, new Dictionary<long, DupeInfo>())!;

        Assert.All(root.Children, c => Assert.StartsWith("/", c.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void Collapsed_chains_still_carry_a_selectable_path()
    {
        // pics/holiday is a passthrough directory, so the tree folds it into one row. The row
        // has to keep the deeper path, or clicking the folded row selects nothing.
        List<ImageView> images =
        [
            Img(1, "/Users/me/pics/holiday/day1/a.jpg"),
            Img(2, "/Users/me/pics/holiday/day1/b.jpg"),
        ];

        var root = FolderTreeBuilder.Build(images, new Dictionary<long, DupeInfo>())!;

        foreach (var node in Walk(root))
            Assert.Equal(node.TotalCount, Matched(node, images));
    }

    [Fact]
    public void Windows_paths_normalise_to_the_same_separator_the_filter_uses()
    {
        // ImageView.FolderOf rewrites "\" to "/", so the tree must be built and matched in
        // that same form — the shipping platform is Windows.
        List<ImageView> images =
        [
            Img(1, @"D:\Development\pics\a.jpg"),
            Img(2, @"D:\Development\pics\sub\b.jpg"),
        ];

        var root = FolderTreeBuilder.Build(images, new Dictionary<long, DupeInfo>())!;

        foreach (var node in Walk(root))
            Assert.Equal(node.TotalCount, Matched(node, images));
    }
}
