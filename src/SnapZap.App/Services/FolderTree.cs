namespace SnapZap.App.Services;

/// <summary>
/// Per-folder counts for one analysis step. <see cref="Done"/> is out of <see cref="Total"/>,
/// so a folder can show which passes have actually reached it.
/// </summary>
public readonly record struct StepStat(int Done, int Total)
{
    public bool Complete => Total == 0 || Done >= Total;
    public bool Untouched => Done == 0 && Total > 0;
    public int Pending => Math.Max(0, Total - Done);
}

/// <summary>
/// One directory in the scanned tree. Counts are cumulative — a parent includes everything
/// beneath it — so collapsing a branch never hides work from the totals.
/// </summary>
public sealed class FolderNode
{
    /// <summary>Full absolute path, forward-slash normalised. This is what the filter matches on.</summary>
    public required string Path { get; init; }

    /// <summary>Just this segment, for display.</summary>
    public required string Name { get; init; }

    public List<FolderNode> Children { get; } = [];

    /// <summary>Photos sitting directly in this folder, not in a subfolder.</summary>
    public int DirectCount { get; set; }

    /// <summary>Photos here and anywhere beneath.</summary>
    public int TotalCount { get; set; }

    public StepStat Analyzed { get; set; }
    public StepStat Blur { get; set; }
    public StepStat Dated { get; set; }
    public StepStat Nsfw { get; set; }

    /// <summary>Photos in this subtree that belong to a duplicate group.</summary>
    public int InDupeGroup { get; set; }

    public int Depth { get; init; }
    public bool HasChildren => Children.Count > 0;

    /// <summary>Steps that have not finished for this subtree — drives the status dot.</summary>
    public IEnumerable<string> PendingSteps()
    {
        if (!Analyzed.Complete) yield return $"{Analyzed.Pending} not analysed";
        if (!Nsfw.Complete) yield return $"{Nsfw.Pending} not NSFW-scored";
        if (!Blur.Complete) yield return $"{Blur.Pending} without a blur score";
        if (!Dated.Complete) yield return $"{Dated.Pending} without an EXIF date";
    }
}

/// <summary>
/// Builds the folder hierarchy from the already-loaded catalog.
///
/// Deliberately in-memory rather than a SQL aggregate: <see cref="AppState.Images"/> already
/// holds every row with its folder and signals resolved, so this needs no query, no schema
/// change and no migration. It is a single O(n) pass over data that is in RAM regardless.
/// </summary>
public static class FolderTreeBuilder
{
    public static FolderNode? Build(IReadOnlyList<ImageView> images, IReadOnlyDictionary<long, DupeInfo> dupeOf)
    {
        if (images.Count == 0) return null;

        // Group once by folder, then fold each group into the tree.
        var byFolder = new Dictionary<string, List<ImageView>>(StringComparer.Ordinal);
        foreach (var img in images)
        {
            if (!byFolder.TryGetValue(img.Folder, out var list))
                byFolder[img.Folder] = list = [];
            list.Add(img);
        }

        var root = CommonPrefix(byFolder.Keys);
        var nodes = new Dictionary<string, FolderNode>(StringComparer.Ordinal);

        var rootNode = new FolderNode
        {
            Path = root,
            Name = root.Length == 0 ? "/" : root[(root.LastIndexOf('/') + 1)..] is { Length: > 0 } n ? n : root,
            Depth = 0,
        };
        nodes[root] = rootNode;

        foreach (var (folder, items) in byFolder)
        {
            var node = EnsureNode(nodes, rootNode, root, folder);
            node.DirectCount = items.Count;

            // Attribute each photo's signals to its own folder; parents pick them up in the roll-up.
            var analyzed = items.Count(i => i.Record.AnalyzedAt is not null);
            var blur = items.Count(i => i.BlurScore is not null);
            var dated = items.Count(i => i.ExifTaken is not null);
            var nsfw = items.Count(i => i.NsfwScore is not null);
            var dupes = items.Count(i => dupeOf.ContainsKey(i.Id));

            node.Analyzed = new StepStat(analyzed, items.Count);
            node.Blur = new StepStat(blur, items.Count);
            node.Dated = new StepStat(dated, items.Count);
            node.Nsfw = new StepStat(nsfw, items.Count);
            node.InDupeGroup = dupes;
        }

        RollUp(rootNode);
        Collapse(rootNode);
        return rootNode;
    }

    /// <summary>Create every missing node between the root and <paramref name="folder"/>.</summary>
    /// <remarks>
    /// Each node's Path is taken as a literal prefix of <paramref name="folder"/> rather than
    /// rebuilt by joining segments. Rebuilding lost the leading separator whenever the library
    /// spanned two filesystem roots — scan <c>/Users/me/pics</c> and <c>/Volumes/backup</c> into
    /// one catalogue and the common prefix is "", so a node came out as <c>Users/me/pics</c>
    /// while every photo reported <c>/Users/me/pics</c>. AppState.Matches compares the two
    /// directly, so selecting any folder in the tree filtered the grid down to nothing.
    /// Slicing the real string cannot drift from what the photos report.
    /// </remarks>
    static FolderNode EnsureNode(
        Dictionary<string, FolderNode> nodes, FolderNode rootNode, string root, string folder)
    {
        if (nodes.TryGetValue(folder, out var existing)) return existing;

        var current = rootNode;
        var depth = 0;
        var i = root.Length;

        while (i < folder.Length)
        {
            // Step over the separator after the root, or after the previous segment.
            while (i < folder.Length && folder[i] == '/') i++;
            if (i >= folder.Length) break;

            var end = folder.IndexOf('/', i);
            if (end < 0) end = folder.Length;

            var path = folder[..end];
            depth++;
            if (!nodes.TryGetValue(path, out var child))
            {
                // With no common root the top row carries the whole path, and showing it bare
                // would read as relative — "/Volumes" not "Volumes". Keyed off the empty root
                // rather than "does this look absolute", which is true of every ordinary scan
                // and would label each child of the scanned folder with its full path.
                var name = depth == 1 && root.Length == 0 ? path : folder[i..end];
                child = new FolderNode { Path = path, Name = name, Depth = depth };
                nodes[path] = child;
                current.Children.Add(child);
            }
            current = child;
            i = end;
        }
        return current;
    }

    /// <summary>Sum children into parents, depth-first, and sort each level by name.</summary>
    static void RollUp(FolderNode node)
    {
        var total = node.DirectCount;
        var analyzed = node.Analyzed;
        var blur = node.Blur;
        var dated = node.Dated;
        var nsfw = node.Nsfw;
        var dupes = node.InDupeGroup;

        node.Children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var child in node.Children)
        {
            RollUp(child);
            total += child.TotalCount;
            analyzed = new StepStat(analyzed.Done + child.Analyzed.Done, analyzed.Total + child.Analyzed.Total);
            blur = new StepStat(blur.Done + child.Blur.Done, blur.Total + child.Blur.Total);
            dated = new StepStat(dated.Done + child.Dated.Done, dated.Total + child.Dated.Total);
            nsfw = new StepStat(nsfw.Done + child.Nsfw.Done, nsfw.Total + child.Nsfw.Total);
            dupes += child.InDupeGroup;
        }

        node.TotalCount = total;
        node.Analyzed = analyzed;
        node.Blur = blur;
        node.Dated = dated;
        node.Nsfw = nsfw;
        node.InDupeGroup = dupes;
    }

    /// <summary>
    /// Fold chains of single-child, photo-less folders into one row (<c>a/b/c</c> rather than
    /// three nested rows). Deep library paths are mostly passthrough directories and each
    /// wasted level costs indentation in a narrow panel.
    /// </summary>
    static void Collapse(FolderNode node)
    {
        foreach (var child in node.Children) Collapse(child);

        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            while (child.DirectCount == 0 && child.Children.Count == 1)
            {
                var only = child.Children[0];
                var merged = new FolderNode
                {
                    Path = only.Path,
                    Name = $"{child.Name}/{only.Name}",
                    Depth = child.Depth,
                    DirectCount = only.DirectCount,
                    TotalCount = only.TotalCount,
                    Analyzed = only.Analyzed,
                    Blur = only.Blur,
                    Dated = only.Dated,
                    Nsfw = only.Nsfw,
                    InDupeGroup = only.InDupeGroup,
                };
                merged.Children.AddRange(only.Children);
                node.Children[i] = merged;
                child = merged;
            }
        }
    }

    /// <summary>Longest shared directory prefix, so the tree is rooted at the library rather
    /// than at "/".</summary>
    internal static string CommonPrefix(IEnumerable<string> folders)
    {
        string? prefix = null;
        foreach (var folder in folders)
        {
            if (prefix is null) { prefix = folder; continue; }

            var a = prefix.Split('/');
            var b = folder.Split('/');
            var take = 0;
            while (take < a.Length && take < b.Length && a[take] == b[take]) take++;
            prefix = string.Join('/', a[..take]);
        }
        return prefix ?? "";
    }
}
