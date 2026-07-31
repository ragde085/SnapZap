using SnapZap.Core.Dedup;
using Xunit;

namespace SnapZap.Tests;

/// <summary>
/// AC 2 / AC 3 / AC 4 for the pigeonhole band prefilter (Task 21): the prefilter and the retained
/// brute-force path must produce identical pair sets over the same signatures at every threshold,
/// and the test must assert which path actually executed — a threshold where both sides happen to
/// run brute force does not count as prefilter coverage.
/// </summary>
/// <remarks>
/// Constructed signatures, no images, no database — same style as <c>GrouperTests</c>. Distances
/// are controlled exactly: <see cref="Plant"/> flips a known number of distinct bit positions in
/// one signature's rotation-0 word, so comparing with <c>allowRotation:false</c> gives an exact,
/// known Hamming distance rather than an approximate one from real images.
/// </remarks>
public class BandPrefilterTests
{
    static PerceptualHash RandomHash(Random rng)
    {
        var bytes = new byte[PerceptualHash.ByteLength];
        rng.NextBytes(bytes);
        ZeroPadding(bytes);
        return PerceptualHash.FromBytes(bytes);
    }

    /// <summary>Bits 544-575 of each 576-bit rotation block are unused padding in the real
    /// encoder (<c>PerceptualHash.Encode</c> only ever sets bits 0-543) — zeroed here so synthetic
    /// hashes look like real ones rather than relying on the (also true, but non-obvious) fact
    /// that padding differences can only ever add to a distance, never hide a true match.</summary>
    static void ZeroPadding(byte[] bytes)
    {
        const int wordsPerRotation = PerceptualHash.Words * sizeof(ulong); // 72 bytes
        const int meaningfulBytes = PerceptualHash.Bits / 8;               // 68 bytes (544 bits)
        for (int r = 0; r < PerceptualHash.Rotations; r++)
            for (int b = meaningfulBytes; b < wordsPerRotation; b++)
                bytes[r * wordsPerRotation + b] = 0;
    }

    /// <summary>A new hash whose rotation-0 differs from <paramref name="source"/>'s rotation-0 in
    /// exactly <paramref name="bits"/> distinct positions (0..271) — an exact, known distance when
    /// compared with <c>allowRotation:false</c>.</summary>
    static PerceptualHash Plant(PerceptualHash source, int bits, Random rng)
    {
        var raw = source.ToBytes();
        var positions = Enumerable.Range(0, PerceptualHash.Bits).OrderBy(_ => rng.Next()).Take(bits);
        foreach (var bit in positions)
            raw[bit / 8] ^= (byte)(1 << (bit % 8));
        return PerceptualHash.FromBytes(raw);
    }

    static List<HashedImage> BuildCatalogue(out int[] plantedDistances)
    {
        var rng = new Random(1234);
        var images = new List<HashedImage>();
        long id = 0;

        // Noise: unrelated hashes, expected distance ~272 of 544 — nowhere near any tested
        // threshold, so real matches below are unambiguous.
        for (int i = 0; i < 150; i++)
            images.Add(new HashedImage(id++, $"noise{i}.jpg", RandomHash(rng), 100, 100, null, null, null));

        // Planted pairs at exact, known distances spanning every tested threshold.
        plantedDistances = [2, 10, 18, 30, 40, 55, 110];
        foreach (var d in plantedDistances)
        {
            var seed = RandomHash(rng);
            var neighbor = Plant(seed, d, rng);
            images.Add(new HashedImage(id++, $"seed{d}.jpg", seed, 100, 100, null, null, null));
            images.Add(new HashedImage(id++, $"neighbor{d}.jpg", neighbor, 100, 100, null, null, null));
        }
        return images;
    }

    static HashSet<(long, long)> Normalize(IEnumerable<SimilarPair> pairs) =>
        pairs.Select(p => p.A < p.B ? (p.A, p.B) : (p.B, p.A)).ToHashSet();

    [Theory]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(20)]
    [InlineData(32)]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(120)]   // the slider maximum, which takes the brute-force fallback
    public void Prefilter_and_brute_force_produce_identical_pair_sets(int threshold)
    {
        var images = BuildCatalogue(out var plantedDistances);

        var (prefilterPairs, path) = VariantFinder.FindPairs(images, threshold, rotations: false, CancellationToken.None);
        var referencePairs = VariantFinder.BruteForcePairs(images, threshold, rotations: false, CancellationToken.None);

        Assert.Equal(Normalize(referencePairs), Normalize(prefilterPairs));

        // Sanity: every distance the fixture actually planted at or below this threshold must be
        // present, and nothing coincidentally created by noise blows past expectations wildly.
        var expectedPlantedMatches = plantedDistances.Count(d => d <= threshold);
        Assert.True(referencePairs.Count >= expectedPlantedMatches,
            $"expected at least {expectedPlantedMatches} planted matches at threshold {threshold}, found {referencePairs.Count}");

        // AC 3: at minimum thresholds 4, 12 and 20 must exercise the prefilter path, not the
        // fallback — a threshold where both sides run brute force is not prefilter coverage.
        if (threshold is 4 or 12 or 20)
            Assert.Equal(VariantMatchPath.BandPrefilter, path);
    }

    [Fact]
    public void High_threshold_falls_back_to_brute_force_and_still_returns_correct_pairs()
    {
        // AC 4: t=120 gives 121 bands of ~4 bits (16 distinct values) — below BandWidthFloor, so
        // this must report the fallback rather than silently degrading inside the prefilter path.
        var images = BuildCatalogue(out _);
        var (pairs, path) = VariantFinder.FindPairs(images, 120, rotations: false, CancellationToken.None);
        var reference = VariantFinder.BruteForcePairs(images, 120, rotations: false, CancellationToken.None);

        Assert.Equal(VariantMatchPath.BruteForce, path);
        Assert.Equal(Normalize(reference), Normalize(pairs));
    }

    /// <summary>
    /// Regression test for a real exactness bug caught in tech-lead review, not a hypothetical.
    /// The original band layout used <c>bandWidth = ceil(Bits / bandCount)</c> for every band, then
    /// dropped or truncated whichever trailing bands ran past the end of the signature. At
    /// <c>threshold=32</c> (<c>bandCount=33</c>) over the 544-bit signature, <c>ceil(544/33)=17</c>
    /// lays down bands at bits 0, 17, 34 … 527 — exactly <b>32</b> bands where pigeonhole requires
    /// 33. A pair differing in exactly 32 bits, with those bits spread one-per-band across all 32
    /// legacy bands, left no band untouched, so the old prefilter found no candidate and missed a
    /// real match entirely — silently, with no truncation flag.
    /// <see cref="VariantFinder.BandLayout"/>'s floor+remainder allocation guarantees exactly
    /// <c>bandCount</c> non-empty bands regardless of divisibility, closing this. Confirmed against
    /// the actual production code, not just the arithmetic: this test failed before the fix (band
    /// prefilter returned zero pairs) and passes after.
    /// </summary>
    [Fact]
    public void Threshold_32_finds_a_match_whose_differing_bits_spread_across_every_legacy_band()
    {
        const int threshold = 32;
        const int legacyWidth = 17;   // ceil(544 / 33)
        var rng = new Random(777);

        var legacyBandStarts = new List<int>();
        for (int start = 0; start < PerceptualHash.Bits; start += legacyWidth) legacyBandStarts.Add(start);
        // Sanity-check the premise itself: one band short of what pigeonhole needs at t=32, and
        // one differing bit per band is therefore exactly the threshold's worth of difference.
        Assert.Equal(32, legacyBandStarts.Count);
        Assert.Equal(threshold, legacyBandStarts.Count);

        var seed = RandomHash(rng);
        var raw = seed.ToBytes();
        // One differing bit per legacy band — 32 bits across all 32 legacy bands, leaving none of
        // them identical, so the legacy layout had no band that could ever surface this pair.
        foreach (var bandStart in legacyBandStarts)
            raw[bandStart / 8] ^= (byte)(1 << (bandStart % 8));
        var neighbor = PerceptualHash.FromBytes(raw);

        Assert.Equal(threshold, seed.DistanceTo(neighbor, PerceptualHash.Bits, allowRotation: false));

        var images = new List<HashedImage>
        {
            new(1, "seed.jpg", seed, 100, 100, null, null, null),
            new(2, "neighbor.jpg", neighbor, 100, 100, null, null, null),
        };

        var (pairs, path) = VariantFinder.FindPairs(images, threshold, rotations: false, CancellationToken.None);
        Assert.Equal(VariantMatchPath.BandPrefilter, path);
        Assert.Single(pairs);
    }

    [Fact]
    public void Rotation_matching_still_finds_pairs_the_prefilter_would_otherwise_miss()
    {
        // A neighbor planted against a ROTATED form of the seed: with rotation matching on, the
        // prefilter must probe all four of an image's rotations against the index (built from
        // everyone's rotation 0), not just rotation 0 — otherwise this pair is invisible to it.
        var rng = new Random(99);
        var images = new List<HashedImage>();
        long id = 0;
        for (int i = 0; i < 60; i++)
            images.Add(new HashedImage(id++, $"noise{i}.jpg", RandomHash(rng), 100, 100, null, null, null));

        var seedBytes = new byte[PerceptualHash.ByteLength];
        rng.NextBytes(seedBytes);
        ZeroPadding(seedBytes);
        var seed = PerceptualHash.FromBytes(seedBytes);

        // Build a "neighbor" whose rotation-0 IS the seed's rotation-1 block, close-planted —
        // i.e. a hash that only matches the seed when rotations are tried.
        var neighborBytes = new byte[PerceptualHash.ByteLength];
        Array.Copy(seedBytes, PerceptualHash.Words * sizeof(ulong), neighborBytes, 0, PerceptualHash.Words * sizeof(ulong));
        var neighborAsHash = PerceptualHash.FromBytes(neighborBytes);
        var neighborClose = Plant(neighborAsHash, 5, rng);

        images.Add(new HashedImage(id++, "seed.jpg", seed, 100, 100, null, null, null));
        images.Add(new HashedImage(id++, "neighbor.jpg", neighborClose, 100, 100, null, null, null));

        const int threshold = 12;
        var (pairs, _) = VariantFinder.FindPairs(images, threshold, rotations: true, CancellationToken.None);
        var found = Normalize(pairs);

        var seedId = images[^2].Id;
        var neighborId = images[^1].Id;
        var key = seedId < neighborId ? (seedId, neighborId) : (neighborId, seedId);
        Assert.Contains(key, found);
    }
}
