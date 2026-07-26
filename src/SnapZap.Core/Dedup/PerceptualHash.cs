using System.Numerics;

namespace SnapZap.Core.Dedup;

/// <summary>
/// A 272-bit gradient ("dHash") perceptual signature, stored for all four 90-degree rotations.
/// </summary>
/// <remarks>
/// <para><b>Grid: 17 x 17.</b> Square because a rotation has to map the grid onto itself, which a
/// 9x8 grid cannot do. Seventeen columns give sixteen horizontal comparisons per row, over
/// seventeen rows: 17 x 16 = <see cref="Bits"/> = 272 bits, five 64-bit words with 48 unused in
/// the last.</para>
///
/// <para><b>Aspect ratio is destroyed on purpose.</b> Squashing any shape onto a fixed square grid
/// is what makes the hash resolution-invariant — a 4000x3000 original and its 800x600 export land
/// on the same grid and produce the same bits. Every hash in this family does this.</para>
///
/// <para><b>All four rotations are stored, never min-canonicalised.</b> The tempting shortcut is to
/// keep only the numerically smallest of the four so rotated copies collide on equality. It is
/// broken: canonical form plus fuzzy matching fails because image noise can flip which rotation
/// wins the minimum, so two near-identical photos canonicalise to different orbit members and then
/// never match at all. Storing all four costs 160 bytes per image — 8 MB across a 50k library — and
/// removes the failure mode entirely.</para>
///
/// <para><b>What this cannot do.</b> Crops and reframes. Every grid hash assumes the frame is the
/// same picture; crop an edge and every cell shifts. That gap is documented and accepted in
/// docs/DEDUP-V2.md §9, not an oversight.</para>
/// </remarks>
public readonly struct PerceptualHash : IEquatable<PerceptualHash>
{
    /// <summary>Edge length of the square sampling grid.</summary>
    public const int Grid = 17;

    /// <summary>Bits per rotation: one per horizontal adjacent-pixel comparison.</summary>
    public const int Bits = Grid * (Grid - 1);   // 272

    /// <summary>64-bit words needed to hold <see cref="Bits"/>.</summary>
    public const int Words = (Bits + 63) / 64;   // 5

    public const int Rotations = 4;

    /// <summary>Serialized size of a full signature.</summary>
    public const int ByteLength = Rotations * Words * sizeof(ulong);   // 160

    /// <summary>
    /// Version of the signature-derivation recipe: decode scale, resampling filter, orientation
    /// handling and grid size. Bump this whenever any of those change — see
    /// <see cref="Data.Database.RecipeMigration"/>, which invalidates every stored <c>phash</c> the
    /// moment this constant no longer matches what a catalogue was written under. The tier-1 probe
    /// never re-analyses an unchanged file, so a signature that isn't invalidated here is never
    /// recomputed on its own; extending the list above is mandatory, not optional, when touching
    /// any of it.
    /// </summary>
    /// <remarks>
    /// 1 = original: 512px nearest-neighbour decode, no orientation handling.
    /// 2 = scaled decode via <c>SKCodec.GetScaledDimensions</c> + linear/mipmap resampling
    ///     (tech-spec Phase C, Tasks 9-12).
    /// 3 = EXIF orientation normalisation via <c>codec.EncodedOrigin</c> (tech-spec Phase D,
    ///     Task 14) — hashing now describes the photo as displayed, not as the sensor stored it.
    /// </remarks>
    public const int PhashRecipeVersion = 3;

    // Rotation r occupies _w[r * Words .. r * Words + Words]. Never null on a constructed value;
    // default(PerceptualHash) is the "absent" sentinel and IsEmpty reports it.
    readonly ulong[]? _w;

    PerceptualHash(ulong[] words) => _w = words;

    /// <summary>True for <c>default</c> — a row that has never been hashed.</summary>
    public bool IsEmpty => _w is null;

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

    /// <summary>Set one bit per horizontally adjacent pair: 1 when the right cell is brighter.</summary>
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
    /// A wrong length means the row was written by a build with a different grid size. Treating
    /// that as "not hashed" makes a grid change a re-scan rather than a corrupt comparison against
    /// bits that mean something else.
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
    /// Smallest Hamming distance between any rotation of this signature and the unrotated form of
    /// <paramref name="other"/>, stopping early once <paramref name="ceiling"/> is exceeded.
    /// </summary>
    /// <remarks>
    /// Only one side is rotated: "A is B turned by r" is fully covered by trying A's four
    /// orientations against B's first, and rotating both would just find each answer four times.
    ///
    /// The ceiling is not an optimisation detail — it is what makes brute-force matching viable.
    /// Unrelated photos differ in roughly half their bits, so the very first word already blows
    /// any sane threshold and the pair costs one XOR and one PopCount instead of five.
    /// </remarks>
    public int DistanceTo(in PerceptualHash other, int ceiling, bool allowRotation)
    {
        if (_w is null || other._w is null) return int.MaxValue;

        int best = int.MaxValue;
        int turns = allowRotation ? Rotations : 1;
        for (int r = 0; r < turns; r++)
        {
            int d = 0;
            int off = r * Words;
            for (int i = 0; i < Words; i++)
            {
                d += BitOperations.PopCount(_w[off + i] ^ other._w[i]);
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
