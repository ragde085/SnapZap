using System.Collections.Concurrent;
using System.Numerics;
using SnapZap.Core.Data;

namespace SnapZap.Core.Dedup;

/// <summary>Which candidate-generation path a <see cref="VariantFinder"/> run actually took.</summary>
public enum VariantMatchPath { BruteForce, BandPrefilter }

/// <summary>
/// Finds photos that are the same shot at a different resolution, encoding, format or
/// orientation — everything czkawka's similar-image pass used to cover, computed in-process from
/// the signatures the scan already produced.
/// </summary>
/// <remarks>
/// <para><b>Pigeonhole band prefilter, with brute force retained as a fallback.</b> Split each
/// signature into <c>B = VariantMaxBits + 1</c> contiguous bands. Two signatures within the
/// threshold differ in at most <c>VariantMaxBits</c> bits total, so with <c>B</c> bands they cannot
/// possibly differ in all of them — at least one band must be bit-for-bit identical (pigeonhole).
/// Indexing rotation 0 of every signature by (band index, band value) and probing with every
/// rotation of each image therefore cannot miss a true match; band collisions can only ever add
/// false-positive <em>candidates</em>, which the existing exact <see cref="PerceptualHash.DistanceTo"/>
/// check filters back out. Measured at the shipped default (t=20, 21 bands × ~13 bits): 12× at 50k
/// signatures, 20× at 100k — on uniformly random hashes, which is the prefilter's best case; real
/// libraries cluster, so re-measure per-catalogue (docs/DEDUP-V2.md).</para>
///
/// <para><b>Degenerates at high thresholds.</b> <c>VariantMaxBits</c> is a user-facing slider
/// running to 60. At t=60, 61 bands over 272 bits leave ~5 bits per band — 32 distinct values —
/// so most images collide in most bands and the prefilter becomes brute force plus indexing
/// overhead. Below <see cref="BandWidthFloor"/> this falls back to the brute-force sweep, which
/// remains callable both for that fallback and so tests can compare the two paths in-process
/// (AC 2, AC 3) — a threshold where both sides run brute force does not count as prefilter
/// coverage. <see cref="BandWidthFloor"/> is a starting point pending the real-catalogue
/// crossover measurement Task 21 calls for; this dev sandbox has no such library to measure
/// against (see docs/PERFORMANCE.md's caveats).</para>
///
/// <para>Grouping is <see cref="SimilarityGrouper"/>'s problem, not this class's. All this does is
/// enumerate pairs — as dense indices into the caller's list, not image ids, so the grouper's
/// <c>within</c> predicate is a direct array lookup rather than a dictionary lookup per comparison
/// (Task 23).</para>
/// </remarks>
public sealed class VariantFinder(Database db)
{
    /// <summary>Bands narrower than this (bits) are judged too collision-prone to pay for the
    /// index; falls back to brute force. See the class remarks on why this needs a real-catalogue
    /// re-measurement rather than a synthetic-fixture one.</summary>
    public const int BandWidthFloor = 6;

    /// <summary>Which path <see cref="FindAndStore"/> most recently took — exposed so tests (and
    /// eventually the UI) can assert on it rather than infer it from timing (AC 3, AC 4).</summary>
    public VariantMatchPath LastPathExecuted { get; private set; }

    public DetectorResult FindAndStore(
        IReadOnlyList<HashedImage> images, DedupSettings settings, string? root, CancellationToken ct = default)
    {
        var repo = new DupeRepository(db);
        repo.ClearKind(DupeKind.Variant, root);

        var usable = images.Where(i => !i.Hash.IsEmpty).ToList();
        if (usable.Count < 2) return DetectorResult.None;

        int threshold = settings.VariantMaxBits;
        bool rotations = settings.VariantRotations;

        var (indexPairs, path) = FindPairs(usable, threshold, rotations, ct);
        LastPathExecuted = path;

        // `within` indexes `usable` directly by the dense position FindPairs used — no id lookup,
        // no dictionary, in what is otherwise the grouper's hottest inner loop (Task 23).
        var result = SimilarityGrouper.Group(
            indexPairs,
            (x, y) => usable[(int)x].Hash.DistanceTo(usable[(int)y].Hash, threshold, rotations) <= threshold);

        var byId = usable.ToDictionary(i => i.Id);
        var idGroups = result.Groups
            .Select(g => (IReadOnlyList<long>)g.Select(idx => usable[(int)idx].Id).ToList())
            .ToList();

        StoreGroups.Write(db, DupeKind.Variant, $"<={threshold} of {PerceptualHash.Bits} bits",
                          idGroups, byId, KeeperRule.HighestResolution);

        return new DetectorResult(idGroups.Count, result.Truncated);
    }

    /// <summary>
    /// Chooses the band prefilter or the brute-force fallback and returns pairs as dense indices
    /// into <paramref name="usable"/>. Internal — exercised directly by <c>BandPrefilterTests</c>
    /// so AC 2/AC 3 can compare both paths in one process without going through the database.
    /// </summary>
    internal static (List<SimilarPair> Pairs, VariantMatchPath Path) FindPairs(
        IReadOnlyList<HashedImage> usable, int threshold, bool rotations, CancellationToken ct)
    {
        int bandCount = threshold + 1;
        // Narrowest band width, for the fallback decision only — not used to lay bands out (see
        // BandLayout, and the bug it fixes).
        int narrowestWidth = PerceptualHash.Bits / bandCount;

        if (narrowestWidth < BandWidthFloor)
            return (BruteForcePairs(usable, threshold, rotations, ct), VariantMatchPath.BruteForce);

        return (BandPrefilterPairs(usable, threshold, rotations, bandCount, ct), VariantMatchPath.BandPrefilter);
    }

    /// <summary>
    /// Exactly <paramref name="bandCount"/> contiguous, non-overlapping bands covering all 272 bits
    /// exactly once — the first <c>272 % bandCount</c> bands get one extra bit, the rest get
    /// <c>272 / bandCount</c>.
    /// </summary>
    /// <remarks>
    /// <b>This is the fix for a real exactness bug.</b> The original implementation used
    /// <c>bandWidth = ceil(272 / bandCount)</c> for every band, then truncated (or entirely
    /// dropped) whichever trailing bands ran past bit 272. At <c>threshold=32</c>
    /// (<c>bandCount=33</c>, <c>ceil(272/33)=9</c>), bands starting at bit 279 and 288 both fall
    /// outside the 272-bit signature and were silently skipped — leaving only <b>31</b> real
    /// bands where the pigeonhole argument requires 33. A signature pair differing in exactly 32
    /// bits can spread them one-or-two per band across those 31 bands with none left untouched,
    /// so no band matches and the pair is missed entirely — a silent false negative in exactly the
    /// matching path this class claims is "exact, not approximate". Verified: constructing two
    /// signatures at threshold 32 with their 32 differing bits spread one-per-band across all 31
    /// real bands under the old layout, <c>BandPrefilterPairs</c> returned zero pairs while
    /// <see cref="BruteForcePairs"/> correctly found the match. This layout instead guarantees
    /// exactly <paramref name="bandCount"/> non-empty bands for every valid <c>bandCount</c>
    /// (5..61), so the guarantee holds unconditionally rather than by luck of divisibility.
    /// </remarks>
    internal static (int Start, int Width)[] BandLayout(int bandCount)
    {
        int baseWidth = PerceptualHash.Bits / bandCount;
        int remainder = PerceptualHash.Bits % bandCount; // first `remainder` bands get +1 bit
        var layout = new (int Start, int Width)[bandCount];
        int pos = 0;
        for (int k = 0; k < bandCount; k++)
        {
            int width = baseWidth + (k < remainder ? 1 : 0);
            layout[k] = (pos, width);
            pos += width;
        }
        return layout;
    }

    /// <summary>
    /// The brute-force O(n²) sweep, retained as a callable path (high-threshold fallback, and
    /// AC 2/AC 3's in-process comparison). Balances the triangular workload by pairing row
    /// <c>i</c> with row <c>n-1-i</c> — <c>Parallel.For</c>'s contiguous chunking otherwise gives
    /// the thread handling <c>i=0</c> an <c>n</c>-length inner loop and the thread handling the
    /// last chunk almost none (Task 22). Copies signatures into one flat <c>ulong[]</c> for the
    /// sweep rather than dereferencing each image's own small array — better cache locality over
    /// the O(n²) comparisons; <see cref="PerceptualHash"/>'s public shape is untouched.
    /// </summary>
    internal static List<SimilarPair> BruteForcePairs(
        IReadOnlyList<HashedImage> usable, int threshold, bool rotations, CancellationToken ct)
    {
        int n = usable.Count;
        int words = PerceptualHash.Words, rots = PerceptualHash.Rotations;
        int stride = words * rots;
        var flat = new ulong[n * stride];
        for (int i = 0; i < n; i++)
            for (int r = 0; r < rots; r++)
                usable[i].Hash.WordsFor(r).CopyTo(flat.AsSpan(i * stride + r * words, words));

        var pairs = new ConcurrentBag<SimilarPair>();
        int half = (n + 1) / 2;

        Parallel.For(0, half,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
            () => new List<SimilarPair>(),
            (k, _, local) =>
            {
                SweepRow(k, local);
                int mirror = n - 1 - k;
                if (mirror != k) SweepRow(mirror, local);
                return local;

                void SweepRow(int i, List<SimilarPair> into)
                {
                    for (int j = i + 1; j < n; j++)
                    {
                        var d = FlatDistance(flat, i, j, words, rots, threshold, rotations);
                        if (d <= threshold) into.Add(new SimilarPair(i, j, d));
                    }
                }
            },
            local => { foreach (var p in local) pairs.Add(p); });

        ct.ThrowIfCancellationRequested();
        return pairs.ToList();
    }

    /// <summary>Same semantics as <see cref="PerceptualHash.DistanceTo"/> — rotations of <c>i</c>
    /// against <c>j</c>'s unrotated form, ceiling-limited — over the flat sweep buffer.</summary>
    static int FlatDistance(ulong[] flat, int i, int j, int words, int rotationCount, int ceiling, bool allowRotation)
    {
        int stride = words * rotationCount;
        int jOff = j * stride; // b's rotation 0
        int turns = allowRotation ? rotationCount : 1;
        int best = int.MaxValue;
        for (int r = 0; r < turns; r++)
        {
            int iOff = i * stride + r * words;
            int d = 0;
            for (int w = 0; w < words; w++)
            {
                d += BitOperations.PopCount(flat[iOff + w] ^ flat[jOff + w]);
                if (d > ceiling) break;
            }
            if (d < best) best = d;
            if (best == 0) break;
        }
        return best;
    }

    /// <summary>
    /// Index rotation 0 of every signature into <paramref name="bandCount"/> band tables; for each
    /// image, probe every rotation it has (all four if <paramref name="rotations"/>) across those
    /// tables, and run the exact <see cref="PerceptualHash.DistanceTo"/> only on what comes back.
    /// </summary>
    internal static List<SimilarPair> BandPrefilterPairs(
        IReadOnlyList<HashedImage> usable, int threshold, bool rotations, int bandCount,
        CancellationToken ct)
    {
        int n = usable.Count;
        var layout = BandLayout(bandCount);
        var bandTables = new Dictionary<ulong, List<int>>[bandCount];
        for (int k = 0; k < bandCount; k++) bandTables[k] = new Dictionary<ulong, List<int>>();

        for (int idx = 0; idx < n; idx++)
        {
            var words0 = usable[idx].Hash.WordsFor(0);
            for (int k = 0; k < bandCount; k++)
            {
                var (start, width) = layout[k];
                var key = ExtractBits(words0, start, width);
                if (!bandTables[k].TryGetValue(key, out var list)) bandTables[k][key] = list = [];
                list.Add(idx);
            }
        }
        ct.ThrowIfCancellationRequested();

        int turns = rotations ? PerceptualHash.Rotations : 1;
        var pairs = new ConcurrentBag<SimilarPair>();

        Parallel.For(0, n,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
            idx =>
            {
                var a = usable[idx];
                HashSet<int>? candidates = null;
                for (int r = 0; r < turns; r++)
                {
                    var wordsR = a.Hash.WordsFor(r);
                    for (int k = 0; k < bandCount; k++)
                    {
                        var (start, width) = layout[k];
                        var key = ExtractBits(wordsR, start, width);
                        if (!bandTables[k].TryGetValue(key, out var list)) continue;
                        foreach (var j in list)
                            if (j > idx) (candidates ??= []).Add(j);
                    }
                }
                if (candidates is null) return;
                foreach (var j in candidates)
                {
                    var d = a.Hash.DistanceTo(usable[j].Hash, threshold, rotations);
                    if (d <= threshold) pairs.Add(new SimilarPair(idx, j, d));
                }
            });

        ct.ThrowIfCancellationRequested();
        return pairs.ToList();
    }

    /// <summary>Bit <c>start</c> is the LSB of word <c>start&gt;&gt;6</c> — the same convention
    /// <see cref="PerceptualHash"/>'s own <c>Encode</c> uses — so a band value here means the same
    /// bits <see cref="PerceptualHash.DistanceTo"/> would compare.</summary>
    static ulong ExtractBits(ReadOnlySpan<ulong> words, int start, int width)
    {
        ulong result = 0;
        for (int i = 0; i < width; i++)
        {
            int bit = start + i;
            if (((words[bit >> 6] >> (bit & 63)) & 1UL) != 0) result |= 1UL << i;
        }
        return result;
    }
}
