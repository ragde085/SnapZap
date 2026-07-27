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

    /// <summary>Bits 272-319 of each 320-bit rotation block are unused padding in the real
    /// encoder (<c>PerceptualHash.Encode</c> only ever sets bits 0-271) — zeroed here so synthetic
    /// hashes look like real ones rather than relying on the (also true, but non-obvious) fact
    /// that padding differences can only ever add to a distance, never hide a true match.</summary>
    static void ZeroPadding(byte[] bytes)
    {
        const int wordsPerRotation = PerceptualHash.Words * sizeof(ulong); // 40 bytes
        const int meaningfulBytes = PerceptualHash.Bits / 8;               // 34 bytes (272 bits)
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

        // Noise: unrelated hashes, expected distance ~136 of 272 — nowhere near any tested
        // threshold, so real matches below are unambiguous.
        for (int i = 0; i < 150; i++)
            images.Add(new HashedImage(id++, $"noise{i}.jpg", RandomHash(rng), 100, 100, null, null, null));

        // Planted pairs at exact, known distances spanning every tested threshold.
        plantedDistances = [2, 10, 18, 30, 40, 55];
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
        // AC 4: t=60 gives 61 bands of ~5 bits (32 distinct values) — below BandWidthFloor, so
        // this must report the fallback rather than silently degrading inside the prefilter path.
        var images = BuildCatalogue(out _);
        var (pairs, path) = VariantFinder.FindPairs(images, 60, rotations: false, CancellationToken.None);
        var reference = VariantFinder.BruteForcePairs(images, 60, rotations: false, CancellationToken.None);

        Assert.Equal(VariantMatchPath.BruteForce, path);
        Assert.Equal(Normalize(reference), Normalize(pairs));
    }

    /// <summary>
    /// Regression test for a real exactness bug caught in tech-lead review, not a hypothetical.
    /// The original band layout used <c>bandWidth = ceil(272 / bandCount)</c> for every band; at
    /// <c>threshold=32</c> (<c>bandCount=33</c>), <c>ceil(272/33)=9</c> produces bands starting at
    /// bit 279 and 288 — both past the 272-bit signature — which were silently skipped, leaving
    /// only 31 real bands where pigeonhole requires 33. A pair differing in exactly 32 bits, with
    /// those bits spread one-per-band across all 31 legacy bands, left no band untouched, so the
    /// old prefilter found no candidate and missed a real match entirely — silently, with no
    /// truncation flag. <see cref="VariantFinder.BandLayout"/>'s floor+remainder allocation
    /// guarantees exactly <c>bandCount</c> non-empty bands regardless of divisibility, closing
    /// this. Confirmed against the actual production code, not just the arithmetic: this test
    /// failed before the fix (band prefilter returned zero pairs) and passes after.
    /// </summary>
    [Fact]
    public void Threshold_32_finds_a_match_whose_differing_bits_spread_across_every_legacy_band()
    {
        const int threshold = 32;
        var rng = new Random(777);

        // The legacy (buggy) layout's real band boundaries: ceil(272/33) = 9, so bands are
        // [0,9),[9,18),...,[261,270) (30 bands of width 9) then [270,272) (1 band of width 2) —
        // 31 bands, not the 33 pigeonhole needs at this threshold.
        var legacyBandStarts = new List<int>();
        for (int start = 0; start < PerceptualHash.Bits; start += 9) legacyBandStarts.Add(start);
        Assert.Equal(31, legacyBandStarts.Count); // sanity-check the premise itself

        var seed = RandomHash(rng);
        var raw = seed.ToBytes();
        // One differing bit per legacy band (31 bits across 31 bands)...
        foreach (var bandStart in legacyBandStarts)
        {
            int bandWidth = Math.Min(9, PerceptualHash.Bits - bandStart);
            int bit = bandStart; // first bit of the band
            raw[bit / 8] ^= (byte)(1 << (bit % 8));
        }
        // ...plus one more (32nd) bit, in the same band as the first flip but a different position,
        // so every one of the 31 legacy bands still has at least one differing bit.
        raw[0] ^= 0b0100_0000; // bit 6 — inside legacy band 0 ([0,9)), distinct from bit 0 above.
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
