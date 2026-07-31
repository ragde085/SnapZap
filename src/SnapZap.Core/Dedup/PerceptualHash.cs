using System.Numerics;

namespace SnapZap.Core.Dedup;

/// <summary>
/// A 544-bit gradient perceptual signature, stored for all four 90-degree rotations.
/// </summary>
/// <remarks>
/// <para><b>Grid: 17 x 17.</b> Square because a rotation has to map the grid onto itself, which a
/// 9x8 grid cannot do.</para>
///
/// <para><b>Both axes are encoded, and the horizontal-only version was a real defect.</b> Each
/// rotation records 17 x 16 = 272 horizontal comparisons <em>and</em> 16 x 17 = 272 vertical ones,
/// for <see cref="Bits"/> = 544 (nine 64-bit words, 32 unused in the last). The original encoded
/// horizontal comparisons only, which meant the entire signature of a photo was its left-to-right
/// luminance profile. Any image whose brightness rises to the middle of the frame and falls
/// afterwards produced the same 272 bits — eight set bits per row, on all seventeen rows —
/// regardless of subject. Measured on a real 2,218-photo library: four "Black Matter" desktop
/// wallpapers (a grey texture with a centre glow) and two sunset photographs (a bright sun over a
/// dark foreground) formed one six-member Variant group with every pairwise distance between 0 and
/// 16 bits, against a threshold of 20. They are not the same photograph and no threshold on that
/// encoding could separate them. Adding the vertical axis moves those same pairs to 41-87 bits
/// while a genuine re-encode/resize stays at a p99 of 41 out of 544 — the wallpapers separate from
/// the sunsets and stay grouped with each other, which is the correct answer.
/// <b>Do not drop the vertical half to halve the storage.</b></para>
///
/// <para><b>Aspect ratio is destroyed on purpose.</b> Squashing any shape onto a fixed square grid
/// is what makes the hash resolution-invariant — a 4000x3000 original and its 800x600 export land
/// on the same grid and produce the same bits. Every hash in this family does this.</para>
///
/// <para><b>All four rotations are stored, never min-canonicalised.</b> The tempting shortcut is to
/// keep only the numerically smallest of the four so rotated copies collide on equality. It is
/// broken: canonical form plus fuzzy matching fails because image noise can flip which rotation
/// wins the minimum, so two near-identical photos canonicalise to different orbit members and then
/// never match at all. Storing all four costs 288 bytes per image — 14 MB across a 50k library —
/// and removes the failure mode entirely.</para>
///
/// <para><b>What this cannot do.</b> Crops and reframes. Every grid hash assumes the frame is the
/// same picture; crop an edge and every cell shifts. That gap is documented and accepted in
/// docs/DEDUP-V2.md §9, not an oversight.</para>
/// </remarks>
public readonly struct PerceptualHash : IEquatable<PerceptualHash>
{
    /// <summary>Edge length of the square sampling grid.</summary>
    public const int Grid = 17;

    /// <summary>One bit per horizontally adjacent cell pair.</summary>
    public const int HorizontalBits = Grid * (Grid - 1);   // 272

    /// <summary>One bit per vertically adjacent cell pair. See the type remarks on why this half
    /// exists and must not be removed.</summary>
    public const int VerticalBits = (Grid - 1) * Grid;     // 272

    /// <summary>Bits per rotation.</summary>
    public const int Bits = HorizontalBits + VerticalBits; // 544

    /// <summary>64-bit words needed to hold <see cref="Bits"/>.</summary>
    public const int Words = (Bits + 63) / 64;   // 9

    public const int Rotations = 4;

    /// <summary>Serialized size of a full signature.</summary>
    public const int ByteLength = Rotations * Words * sizeof(ulong);   // 288

    /// <summary>
    /// Version of the signature-derivation recipe: decode scale, resampling filter, orientation
    /// handling, grid size and which comparisons are encoded. Bump this whenever any of those
    /// change — see <see cref="Data.Database.RecipeMigration"/>, which invalidates every stored
    /// <c>phash</c> the moment this constant no longer matches what a catalogue was written under.
    /// The tier-1 probe never re-analyses an unchanged file, so a signature that isn't invalidated
    /// here is never recomputed on its own; extending the list above is mandatory, not optional,
    /// when touching any of it.
    /// </summary>
    /// <remarks>
    /// 1 = original: 512px nearest-neighbour decode, no orientation handling.
    /// 2 = scaled decode via <c>SKCodec.GetScaledDimensions</c> + linear/mipmap resampling
    ///     (tech-spec Phase C, Tasks 9-12).
    /// 3 = EXIF orientation normalisation via <c>codec.EncodedOrigin</c> (tech-spec Phase D,
    ///     Task 14) — hashing now describes the photo as displayed, not as the sensor stored it.
    /// 4 = vertical comparisons added alongside the horizontal ones (272 -> 544 bits). See the
    ///     type remarks: horizontal-only could not distinguish a centre-glow wallpaper from a
    ///     sunset photograph.
    /// </remarks>
    public const int PhashRecipeVersion = 4;

    // Rotation r occupies _w[r * Words .. r * Words + Words]. Never null on a constructed value;
    // default(PerceptualHash) is the "absent" sentinel and IsEmpty reports it.
    readonly ulong[]? _w;

    PerceptualHash(ulong[] words) => _w = words;

    /// <summary>True for <c>default</c> — a row that has never been hashed.</summary>
    public bool IsEmpty => _w is null;

    /// <summary>
    /// True when every bit of every rotation is clear, which happens only when no cell in the grid
    /// is brighter than the neighbour it is compared against — a frame of one flat colour.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this needs its own guard rather than a general low-contrast threshold.</b> A
    /// featureless-but-not-flat photo (an overcast sky, a blurred wall) has its bits decided by
    /// sensor and JPEG noise, so they come out effectively random — and random bits do not collide.
    /// Measured over 179,675 genuinely-unrelated pairs from a real library, imposing a minimum
    /// contrast of anywhere from 2 to 8 grey levels changed the number of pairs falling under every
    /// tested threshold by exactly zero, while discarding up to 8% of the library from perceptual
    /// matching. A general contrast gate is pure recall cost.</para>
    ///
    /// <para>A <em>perfectly</em> flat frame is the one case that genuinely does collide: every
    /// comparison is a tie, ties encode as 0, and so a solid black frame and a solid white frame
    /// produce byte-identical signatures and sit 0 bits apart. In a tool that offers duplicates for
    /// deletion that is not an acceptable answer, so such a signature matches nothing at all — the
    /// same treatment <see cref="IsEmpty"/> gets. <c>DegenerateSignaturesNeverMatch</c> pins it.</para>
    /// </remarks>
    public bool IsDegenerate
    {
        get
        {
            if (_w is null) return false;   // absent, not degenerate — IsEmpty is that question
            foreach (var w in _w) if (w != 0) return false;
            return true;
        }
    }

    /// <summary>True when this signature cannot take part in perceptual matching at all.</summary>
    public bool IsUnusable => IsEmpty || IsDegenerate;

    // ---- Construction ------------------------------------------------------

    /// <summary>
    /// Build a signature from an aspect-preserving greyscale buffer — the one
    /// <c>SkiaImageService.DecodeGray</c> already produces for the blur detector, so no extra
    /// decode is needed. Returns <c>default</c> when the buffer is too small to sample.
    /// </summary>
    public static PerceptualHash FromGray(ReadOnlySpan<float> gray, int width, int height)
    {
        if (width <= 0 || height <= 0 || gray.Length < width * height) return default;

        var cells = BoxSample(gray, width, height);

        var words = new ulong[Rotations * Words];
        var rotated = new float[Grid * Grid];
        for (int r = 0; r < Rotations; r++)
        {
            Rotate(cells, rotated, r);
            Encode(rotated, words.AsSpan(r * Words, Words));
        }
        return new PerceptualHash(words);
    }

    /// <summary>
    /// Average the source into a <see cref="Grid"/> x <see cref="Grid"/> cell grid.
    /// </summary>
    /// <remarks>
    /// Box-averaging rather than nearest-neighbour sampling. Point sampling a 512-pixel-wide
    /// buffer down to 17 cells throws away 96% of the pixels and makes the hash sensitive to
    /// exactly which pixels survive — so a resize and a re-encode of the same photo, which differ
    /// only in sub-pixel detail, can land on visibly different bits. Averaging is the low-pass
    /// filter that makes the signature stable across resampling, which is the entire point.
    /// </remarks>
    static float[] BoxSample(ReadOnlySpan<float> gray, int width, int height)
    {
        var cells = new float[Grid * Grid];
        for (int gy = 0; gy < Grid; gy++)
        {
            int y0 = (int)((long)gy * height / Grid);
            int y1 = (int)((long)(gy + 1) * height / Grid);
            if (y1 <= y0) y1 = Math.Min(height, y0 + 1);

            for (int gx = 0; gx < Grid; gx++)
            {
                int x0 = (int)((long)gx * width / Grid);
                int x1 = (int)((long)(gx + 1) * width / Grid);
                if (x1 <= x0) x1 = Math.Min(width, x0 + 1);

                double sum = 0;
                int n = 0;
                for (int y = y0; y < y1; y++)
                {
                    int row = y * width;
                    for (int x = x0; x < x1; x++) { sum += gray[row + x]; n++; }
                }
                cells[gy * Grid + gx] = n == 0 ? 0f : (float)(sum / n);
            }
        }
        return cells;
    }

    /// <summary>Rotate the square cell grid by <paramref name="quarterTurns"/> * 90 degrees, clockwise.</summary>
    static void Rotate(float[] src, float[] dst, int quarterTurns)
    {
        if (quarterTurns == 0) { src.AsSpan().CopyTo(dst); return; }

        for (int y = 0; y < Grid; y++)
            for (int x = 0; x < Grid; x++)
            {
                // Where (x,y) of the source lands after the turn.
                var (nx, ny) = quarterTurns switch
                {
                    1 => (Grid - 1 - y, x),
                    2 => (Grid - 1 - x, Grid - 1 - y),
                    _ => (y, Grid - 1 - x),
                };
                dst[ny * Grid + nx] = src[y * Grid + x];
            }
    }

    /// <summary>
    /// Bits 0..<see cref="HorizontalBits"/>-1: one per horizontally adjacent pair, set when the
    /// right cell is brighter. The remainder: one per vertically adjacent pair, set when the lower
    /// cell is brighter.
    /// </summary>
    /// <remarks>
    /// The two halves are contiguous rather than interleaved so that
    /// <c>VariantFinder.BandLayout</c>'s contiguous bands each cover a coherent run of comparisons.
    /// Ties encode as 0 in both halves, which is what makes a flat frame all-zero — see
    /// <see cref="IsDegenerate"/>.
    /// </remarks>
    static void Encode(float[] cells, Span<ulong> into)
    {
        into.Clear();
        int bit = 0;
        for (int y = 0; y < Grid; y++)
            for (int x = 0; x < Grid - 1; x++, bit++)
            {
                if (cells[y * Grid + x + 1] > cells[y * Grid + x])
                    into[bit >> 6] |= 1UL << (bit & 63);
            }
        for (int y = 0; y < Grid - 1; y++)
            for (int x = 0; x < Grid; x++, bit++)
            {
                if (cells[(y + 1) * Grid + x] > cells[y * Grid + x])
                    into[bit >> 6] |= 1UL << (bit & 63);
            }
    }

    // ---- Serialization -----------------------------------------------------

    public byte[] ToBytes()
    {
        if (_w is null) return [];
        var bytes = new byte[ByteLength];
        Buffer.BlockCopy(_w, 0, bytes, 0, ByteLength);
        return bytes;
    }

    /// <summary>
    /// Rehydrate a signature, or <c>default</c> when the blob is absent or the wrong length.
    /// </summary>
    /// <remarks>
    /// A wrong length means the row was written by a build with a different grid size or a
    /// different set of encoded comparisons. Treating that as "not hashed" makes an encoding change
    /// a re-scan rather than a corrupt comparison against bits that mean something else. It is the
    /// backstop behind <see cref="PhashRecipeVersion"/>, not a substitute for bumping it.
    /// </remarks>
    public static PerceptualHash FromBytes(byte[]? blob)
    {
        if (blob is null || blob.Length != ByteLength) return default;
        var words = new ulong[Rotations * Words];
        Buffer.BlockCopy(blob, 0, words, 0, ByteLength);
        return new PerceptualHash(words);
    }

    // ---- Comparison --------------------------------------------------------

    /// <summary>
    /// Smallest Hamming distance between the two signatures over every rotation, stopping early
    /// once <paramref name="ceiling"/> is exceeded. Symmetric: <c>a.DistanceTo(b)</c> and
    /// <c>b.DistanceTo(a)</c> always agree.
    /// </summary>
    /// <remarks>
    /// <para><b>Both directions are evaluated, and the one-sided version was a real defect.</b> The
    /// original comment here claimed that rotating only one side was sufficient — that "A is B
    /// turned by r" is fully covered by trying A's four orientations against B's first. That holds
    /// for an <em>exact</em> rotation, where the two signatures' four-rotation orbits coincide, and
    /// it does not hold for approximate matching: the rotations are computed by turning the cell
    /// grid and re-encoding, so a rotated signature is not a bit-permutation of the unrotated one
    /// and <c>min_r popcount(H_r(A) ^ H_0(B))</c> need not equal <c>min_r popcount(H_r(B) ^
    /// H_0(A))</c>. Measured on a real library: 55,269 of 79,800 sampled pairs disagreed, one of
    /// them by 247 bits (259 one way, 12 the other).</para>
    ///
    /// <para>That asymmetry was not cosmetic. <see cref="SimilarityGrouper"/> tests candidates in
    /// whichever order grouping happens to reach them, so its complete-linkage invariant — "a group
    /// is a clique" — was only ever enforced in one direction, and the same library contained a
    /// seven-member Variant group holding pairs 259 bits apart under a 20-bit threshold. Taking the
    /// minimum of both directions restores the invariant by construction, so no caller has to know
    /// which argument order is the safe one.</para>
    ///
    /// <para>The second pass is skipped entirely when <paramref name="allowRotation"/> is false,
    /// because comparing rotation 0 against rotation 0 is symmetric already.</para>
    ///
    /// <para>The ceiling is not an optimisation detail — it is what makes brute-force matching
    /// viable. Unrelated photos differ in roughly half their bits, so the very first word already
    /// blows any sane threshold and the pair costs one XOR and one PopCount instead of nine. A
    /// returned value above the ceiling is a lower bound, not an exact distance; pass
    /// <see cref="Bits"/> when the true distance is wanted.</para>
    /// </remarks>
    public int DistanceTo(in PerceptualHash other, int ceiling, bool allowRotation)
    {
        if (_w is null || other._w is null) return int.MaxValue;
        if (IsDegenerate || other.IsDegenerate) return int.MaxValue;

        int best = OneSided(_w, other._w, ceiling, allowRotation);
        if (!allowRotation || best == 0) return best;
        return Math.Min(best, OneSided(other._w, _w, ceiling, allowRotation));
    }

    /// <summary>Rotations of <paramref name="a"/> against <paramref name="b"/>'s unrotated form.
    /// Not symmetric on its own — see <see cref="DistanceTo"/>, the only caller.</summary>
    static int OneSided(ulong[] a, ulong[] b, int ceiling, bool allowRotation)
    {
        int best = int.MaxValue;
        int turns = allowRotation ? Rotations : 1;
        for (int r = 0; r < turns; r++)
        {
            int d = 0;
            int off = r * Words;
            for (int i = 0; i < Words; i++)
            {
                d += BitOperations.PopCount(a[off + i] ^ b[i]);
                if (d > ceiling) break;
            }
            if (d < best) best = d;
            if (best == 0) break;
        }
        return best;
    }

    /// <summary>
    /// Raw words for one rotation. Internal — the band-prefilter index (Task 21) needs direct bit
    /// access to build and probe band tables; every other caller should use <see cref="DistanceTo"/>.
    /// </summary>
    internal ReadOnlySpan<ulong> WordsFor(int rotation) =>
        _w is null ? default : _w.AsSpan(rotation * Words, Words);

    public bool Equals(PerceptualHash other) =>
        _w is null ? other._w is null : other._w is not null && _w.AsSpan().SequenceEqual(other._w);

    public override bool Equals(object? obj) => obj is PerceptualHash h && Equals(h);

    public override int GetHashCode() => _w is null ? 0 : HashCode.Combine(_w[0], _w[1], _w.Length);
}
