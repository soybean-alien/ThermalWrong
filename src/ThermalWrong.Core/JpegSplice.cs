// ThermalWrong by soybean_alien
// https://github.com/soybean-alien/ThermalWrong · MIT License
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace ThermalWrong.Core;

/// <summary>
/// Where every MCU of a cached baseline 4:2:0 frame starts in its entropy-coded data. With it the
/// overlay re-encodes only the MCUs under the text and copies the rest of the frame bit for bit.
/// </summary>
public sealed class JpegLayout
{
    internal ClipTables Tables { get; init; } = null!;
    /// <summary>Quantizer step per component, in natural order, and its reciprocal.</summary>
    internal float[][] Dequant { get; init; } = null!;
    internal float[][] Reciprocal { get; init; } = null!;
    /// <summary>Offset of the entropy-coded data from the start of the JPEG.</summary>
    internal int EntropyStart { get; init; }
    internal int McusX { get; init; }
    internal int McusY { get; init; }
    /// <summary>Per MCU (plus one past the end): the stuffed byte its first bit is in, relative to <see cref="EntropyStart"/>.</summary>
    internal int[] McuByte { get; init; } = null!;
    /// <summary>Per MCU (plus one past the end): its first bit, counting data bits only (no stuffed zeros).</summary>
    internal int[] McuBit { get; init; } = null!;
    /// <summary>Per MCU (plus one past the end): the Y, Cb and Cr DC predictors going in.</summary>
    internal short[] McuPred { get; init; } = null!;
    public int McuCount => McusX * McusY;
}

/// <summary>Huffman tables shared by every frame of a clip: 0 DC luma, 1 AC luma, 2 DC chroma, 3 AC chroma.</summary>
internal sealed class ClipTables(HuffSpec[] specs)
{
    public readonly HuffSpec[] Specs = specs;
    public readonly HuffEncoder[] Enc = specs.Select(s => new HuffEncoder(s)).ToArray();
    public readonly HuffDecoder[] Dec = specs.Select(s => new HuffDecoder(s)).ToArray();
}

internal sealed record HuffSpec(byte[] Bits, byte[] Vals); // Bits[1..16] = codes of each length

public static class JpegSplice
{
    /// <summary>Natural (row-major) index of each zigzag position.</summary>
    internal static readonly byte[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    /// <summary>
    /// Re-codes every frame (losslessly: same coefficients, so the same picture) with Huffman tables
    /// built for the whole clip that can code any coefficient, and records where each MCU starts.
    /// Frames that aren't baseline 4:2:0 are left alone and get no layout.
    /// </summary>
    public static JpegLayout?[] Repack(List<byte[]> packets, int width, int height)
    {
        var frames = new Frame?[packets.Count];
        Parallel.For(0, packets.Count, i => frames[i] = Frame.Parse(packets[i].AsSpan(LcdProtocol.HeaderSize), width, height));

        // Pass 1: symbol statistics for the whole clip.
        var freq = new long[4][];
        for (int t = 0; t < 4; t++) freq[t] = new long[256];
        Parallel.For(0, packets.Count, () => new long[4 * 256], (i, _, local) =>
        {
            if (frames[i] is { } f) f.Count(packets[i].AsSpan(LcdProtocol.HeaderSize), local);
            return local;
        }, local =>
        {
            lock (freq)
                for (int t = 0; t < 4; t++)
                    for (int s = 0; s < 256; s++) freq[t][s] += local[t * 256 + s];
        });
        if (frames.All(f => f == null)) return new JpegLayout?[packets.Count];

        // Every legal symbol gets a code, so re-encoded MCUs can always be written.
        for (int t = 0; t < 4; t++)
            for (int s = 0; s < 256; s++)
                if (t % 2 == 0 ? s <= 11 : s is 0x00 or 0xF0 || (s & 15) is >= 1 and <= 10) freq[t][s]++;
        var tables = new ClipTables(freq.Select(BuildTable).ToArray());

        // Pass 2: re-encode.
        var layouts = new JpegLayout?[packets.Count];
        int scratchSize = packets.Max(p => p.Length) * 2 + 65536;
        Parallel.For(0, packets.Count, () => new byte[scratchSize], (i, _, scratch) =>
        {
            if (frames[i] is { } f) (packets[i], layouts[i]) = f.Reencode(packets[i].AsSpan(LcdProtocol.HeaderSize), tables, width, height, scratch);
            return scratch;
        }, _ => { });
        return layouts;
    }

    /// <summary>Builds a length-limited Huffman table from symbol counts (JPEG Annex K.2, as libjpeg does).</summary>
    static HuffSpec BuildTable(long[] counts)
    {
        var freq = new long[257];
        Array.Copy(counts, freq, 256);
        freq[256] = 1; // reserved so no code is all ones
        var size = new int[257];
        var others = new int[257];
        Array.Fill(others, -1);
        while (true)
        {
            int c1 = -1, c2 = -1;
            long v = long.MaxValue;
            for (int i = 0; i <= 256; i++)
                if (freq[i] != 0 && freq[i] <= v) { v = freq[i]; c1 = i; }
            v = long.MaxValue;
            for (int i = 0; i <= 256; i++)
                if (freq[i] != 0 && freq[i] <= v && i != c1) { v = freq[i]; c2 = i; }
            if (c2 < 0) break;
            freq[c1] += freq[c2];
            freq[c2] = 0;
            size[c1]++;
            while (others[c1] >= 0) { c1 = others[c1]; size[c1]++; }
            others[c1] = c2;
            size[c2]++;
            while (others[c2] >= 0) { c2 = others[c2]; size[c2]++; }
        }
        int maxLen = size.Max();
        var bits = new int[Math.Max(maxLen, 16) + 1];
        for (int i = 0; i <= 256; i++)
            if (size[i] > 0) bits[size[i]]++;
        for (int i = maxLen; i > 16; i--)
            while (bits[i] > 0)
            {
                int j = i - 2;
                while (bits[j] == 0) j--;
                bits[i] -= 2;
                bits[i - 1]++;
                bits[j + 1] += 2;
                bits[j]--;
            }
        int last = 16;
        while (bits[last] == 0) last--;
        bits[last]--; // drop the reserved symbol
        var vals = new List<byte>();
        for (int len = 1; len <= maxLen; len++)
            for (int s = 0; s < 256; s++)
                if (size[s] == len) vals.Add((byte)s);
        var b = new byte[17];
        for (int i = 1; i <= 16; i++) b[i] = (byte)bits[i];
        return new HuffSpec(b, vals.ToArray());
    }

    /// <summary>A parsed frame: the headers to keep and the tables to read its entropy data with.</summary>
    sealed class Frame
    {
        readonly List<(int Start, int Length)> _keep = [];
        readonly float[][] _dequant = new float[3][], _reciprocal = new float[3][];
        readonly HuffDecoder[] _dec = new HuffDecoder[4]; // as in ClipTables
        readonly byte[] _ids = new byte[3];
        int _entropy, _mcusX, _mcusY;

        public static Frame? Parse(ReadOnlySpan<byte> d, int width, int height)
        {
            if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return null;
            var f = new Frame { _mcusX = (width + 15) / 16, _mcusY = (height + 15) / 16 };
            var dqt = new ushort[4][];
            var dht = new HuffSpec?[8]; // class * 4 + id
            var qsel = new int[3];
            int p = 2;
            while (p + 4 <= d.Length)
            {
                if (d[p] != 0xFF) return null;
                byte marker = d[p + 1];
                if (marker == 0xFF) { p++; continue; }
                int len = BinaryPrimitives.ReadUInt16BigEndian(d[(p + 2)..]);
                var seg = d.Slice(p + 4, len - 2);
                switch (marker)
                {
                    case 0xDB:
                        for (int q = 0; q < seg.Length;)
                        {
                            int pq = seg[q] >> 4, tq = seg[q] & 15;
                            if (tq > 3) return null;
                            var t = new ushort[64];
                            for (int k = 0; k < 64; k++)
                                t[k] = pq == 0 ? seg[q + 1 + k] : BinaryPrimitives.ReadUInt16BigEndian(seg[(q + 1 + 2 * k)..]);
                            dqt[tq] = t;
                            q += 1 + (pq == 0 ? 64 : 128);
                        }
                        f._keep.Add((p, len + 2));
                        break;
                    case 0xC0 or 0xC1:
                        if (seg[0] != 8 || BinaryPrimitives.ReadUInt16BigEndian(seg[1..]) != height ||
                            BinaryPrimitives.ReadUInt16BigEndian(seg[3..]) != width || seg[5] != 3) return null;
                        for (int c = 0; c < 3; c++)
                        {
                            f._ids[c] = seg[6 + 3 * c];
                            if (seg[7 + 3 * c] != (c == 0 ? 0x22 : 0x11)) return null;
                            qsel[c] = seg[8 + 3 * c] & 3;
                        }
                        f._keep.Add((p, len + 2));
                        break;
                    case 0xC4:
                        for (int q = 0; q < seg.Length;)
                        {
                            int tc = seg[q] >> 4, th = seg[q] & 15;
                            if (tc > 1 || th > 3) return null;
                            var bits = new byte[17];
                            int n = 0;
                            for (int i = 1; i <= 16; i++) n += bits[i] = seg[q + i];
                            dht[tc * 4 + th] = new HuffSpec(bits, seg.Slice(q + 17, n).ToArray());
                            q += 17 + n;
                        }
                        break;
                    case 0xDD:
                        if (BinaryPrimitives.ReadUInt16BigEndian(seg) != 0) return null;
                        break;
                    case 0xDA:
                        if (seg[0] != 3 || seg[7] != 0 || seg[8] != 63 || seg[9] != 0) return null;
                        for (int c = 0; c < 3; c++)
                        {
                            if (seg[1 + 2 * c] != f._ids[c]) return null;
                            int td = seg[2 + 2 * c] >> 4, ta = seg[2 + 2 * c] & 15;
                            if (dht[td] is not { } dc || dht[4 + ta] is not { } ac) return null;
                            if (c < 2) { f._dec[c * 2] = new HuffDecoder(dc); f._dec[c * 2 + 1] = new HuffDecoder(ac); }
                            else if (seg[6] != seg[4]) return null; // Cr must share Cb's tables
                        }
                        for (int c = 0; c < 3; c++)
                        {
                            if (dqt[qsel[c]] is not { } q || q.Contains((ushort)0)) return null;
                            f._dequant[c] = new float[64];
                            f._reciprocal[c] = new float[64];
                            for (int k = 0; k < 64; k++)
                            {
                                f._dequant[c][ZigZag[k]] = q[k];
                                f._reciprocal[c][ZigZag[k]] = 1f / q[k];
                            }
                        }
                        f._entropy = p + 2 + len;
                        return f;
                    case >= 0xC2 and <= 0xCF:
                        return null; // progressive, lossless or arithmetic
                    default:
                        f._keep.Add((p, len + 2));
                        break;
                }
                p += 2 + len;
            }
            return null;
        }

        public void Count(ReadOnlySpan<byte> jpeg, long[] freq)
        {
            var r = new BitReader(jpeg[_entropy..], 0, 0);
            Span<short> zz = stackalloc short[64];
            Span<int> pred = stackalloc int[3];
            Span<int> cpred = stackalloc int[3];
            for (int m = 0; m < _mcusX * _mcusY; m++)
                for (int b = 0; b < 6; b++)
                {
                    int c = b < 4 ? 0 : b - 3, t = c == 0 ? 0 : 2;
                    DecodeBlock(ref r, zz, ref pred[c], _dec[t], _dec[t + 1]);
                    CountBlock(zz, ref cpred[c], freq.AsSpan(t * 256, 256), freq.AsSpan((t + 1) * 256, 256));
                }
        }

        /// <summary>Re-codes the frame with the clip's tables; scratch must hold twice the JPEG plus 64 KB.</summary>
        public (byte[], JpegLayout) Reencode(ReadOnlySpan<byte> jpeg, ClipTables tables, int width, int height, byte[] scratch)
        {
            int mcus = _mcusX * _mcusY;
            // Headers: everything kept, then our tables and a scan header that points at them.
            var head = new MemoryStream();
            head.Write([0xFF, 0xD8]);
            foreach (var (start, length) in _keep) head.Write(jpeg.Slice(start, length));
            var dht = new MemoryStream();
            for (int t = 0; t < 4; t++)
            {
                var s = tables.Specs[t];
                dht.WriteByte((byte)((t % 2) << 4 | t / 2));
                dht.Write(s.Bits, 1, 16);
                dht.Write(s.Vals);
            }
            head.Write([0xFF, 0xC4, (byte)((dht.Length + 2) >> 8), (byte)(dht.Length + 2)]);
            dht.WriteTo(head);
            head.Write([0xFF, 0xDA, 0, 12, 3, _ids[0], 0x00, _ids[1], 0x11, _ids[2], 0x11, 0, 63, 0]);
            int entropyStart = (int)head.Length;

            var w = new BitWriter(scratch);
            var r = new BitReader(jpeg[_entropy..], 0, 0);
            var mcuByte = new int[mcus + 1];
            var mcuBit = new int[mcus + 1];
            var mcuPred = new short[(mcus + 1) * 3];
            Span<short> zz = stackalloc short[64];
            Span<int> pred = stackalloc int[3];
            Span<int> wpred = stackalloc int[3];
            for (int m = 0; m < mcus; m++)
            {
                mcuByte[m] = w.BytePosition();
                mcuBit[m] = (int)w.Bits;
                for (int c = 0; c < 3; c++) mcuPred[m * 3 + c] = (short)wpred[c];
                for (int b = 0; b < 6; b++)
                {
                    int c = b < 4 ? 0 : b - 3, t = c == 0 ? 0 : 2;
                    DecodeBlock(ref r, zz, ref pred[c], _dec[t], _dec[t + 1]);
                    EncodeBlock(ref w, zz, ref wpred[c], tables.Enc[t], tables.Enc[t + 1]);
                }
            }
            mcuByte[mcus] = w.BytePosition();
            mcuBit[mcus] = (int)w.Bits;
            for (int c = 0; c < 3; c++) mcuPred[mcus * 3 + c] = (short)wpred[c];
            w.Flush();
            int entropyLength = w.BytePosition();

            var packet = new byte[LcdProtocol.HeaderSize + entropyStart + entropyLength + 2];
            int jpegLength = packet.Length - LcdProtocol.HeaderSize;
            LcdProtocol.WritePictureHeader(packet, jpegLength, width, height);
            head.GetBuffer().AsSpan(0, entropyStart).CopyTo(packet.AsSpan(LcdProtocol.HeaderSize));
            scratch.AsSpan(0, entropyLength).CopyTo(packet.AsSpan(LcdProtocol.HeaderSize + entropyStart));
            packet[^2] = 0xFF;
            packet[^1] = 0xD9;
            return (packet, new JpegLayout
            {
                Tables = tables,
                Dequant = _dequant,
                Reciprocal = _reciprocal,
                EntropyStart = entropyStart,
                McusX = _mcusX,
                McusY = _mcusY,
                McuByte = mcuByte,
                McuBit = mcuBit,
                McuPred = mcuPred,
            });
        }
    }

    internal static void DecodeBlock(ref BitReader r, scoped Span<short> zz, ref int pred, HuffDecoder dc, HuffDecoder ac)
    {
        zz.Clear();
        int s = dc.Decode(ref r);
        if (s > 0) pred += Extend(r.GetBits(s), s);
        zz[0] = (short)pred;
        for (int k = 1; k < 64;)
        {
            int rs = ac.Decode(ref r), run = rs >> 4;
            s = rs & 15;
            if (s == 0)
            {
                if (run != 15) break; // end of block
                k += 16;
                continue;
            }
            k += run;
            if (k > 63) throw new InvalidDataException("Bad JPEG block.");
            zz[k++] = (short)Extend(r.GetBits(s), s);
        }
    }

    static int Extend(int v, int s) => v < 1 << (s - 1) ? v - (1 << s) + 1 : v;

    static int Category(int v) => v == 0 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)Math.Abs(v));

    internal static void EncodeBlock(ref BitWriter w, scoped ReadOnlySpan<short> zz, ref int pred, HuffEncoder dc, HuffEncoder ac)
    {
        int diff = zz[0] - pred;
        pred = zz[0];
        int s = Category(diff);
        w.Put(dc.Code[s] << s | (uint)(diff < 0 ? diff - 1 : diff) & ((1u << s) - 1), dc.Length[s] + s);
        int run = 0;
        for (int k = 1; k < 64; k++)
        {
            int v = zz[k];
            if (v == 0) { run++; continue; }
            while (run > 15)
            {
                w.Put(ac.Code[0xF0], ac.Length[0xF0]);
                run -= 16;
            }
            s = Category(v);
            int sym = run << 4 | s;
            w.Put(ac.Code[sym] << s | (uint)(v < 0 ? v - 1 : v) & ((1u << s) - 1), ac.Length[sym] + s);
            run = 0;
        }
        if (run > 0) w.Put(ac.Code[0], ac.Length[0]);
    }

    static void CountBlock(ReadOnlySpan<short> zz, ref int pred, Span<long> dc, Span<long> ac)
    {
        dc[Category(zz[0] - pred)]++;
        pred = zz[0];
        int run = 0;
        for (int k = 1; k < 64; k++)
        {
            int v = zz[k];
            if (v == 0) { run++; continue; }
            while (run > 15) { ac[0xF0]++; run -= 16; }
            ac[run << 4 | Category(v)]++;
            run = 0;
        }
        if (run > 0) ac[0]++;
    }

    // Orthonormal 8-point DCT-II matrix: F = D f Dᵀ, f = Dᵀ F D.
    static readonly float[] D = MakeDct(false), DT = MakeDct(true);

    static float[] MakeDct(bool transpose)
    {
        var m = new float[64];
        for (int u = 0; u < 8; u++)
            for (int x = 0; x < 8; x++)
                m[transpose ? x * 8 + u : u * 8 + x] = (float)((u == 0 ? Math.Sqrt(0.125) : 0.5) * Math.Cos((2 * x + 1) * u * Math.PI / 16));
        return m;
    }

    /// <summary>
    /// Decodes a block to pixels, keeps <paramref name="keep"/> of each and adds <paramref name="add"/>
    /// (level-shifted samples, natural order), and quantizes it again.
    /// </summary>
    internal static void BlendBlock(Span<short> zz, float[] dequant, float[] reciprocal, ReadOnlySpan<float> keep, ReadOnlySpan<float> add)
    {
        Span<float> a = stackalloc float[64], b = stackalloc float[64];
        for (int k = 0; k < 64; k++)
        {
            int n = ZigZag[k];
            a[n] = zz[k] * dequant[n];
        }
        MulLeft(DT, a, b); // f = Dᵀ F D
        MulRight(b, D, a);
        ref float pa = ref MemoryMarshal.GetReference(a);
        ref float pk = ref MemoryMarshal.GetReference(keep);
        ref float pd = ref MemoryMarshal.GetReference(add);
        var lo = Vector256.Create(-128f);
        var hi = Vector256.Create(127f);
        for (nuint o = 0; o < 64; o += 8)
        {
            var v = Vector256.Min(Vector256.Max(Vector256.LoadUnsafe(ref pa, o), lo), hi);
            (v * Vector256.LoadUnsafe(ref pk, o) + Vector256.LoadUnsafe(ref pd, o)).StoreUnsafe(ref pa, o);
        }
        MulLeft(D, a, b); // F = D f Dᵀ
        MulRight(b, DT, a);
        zz[0] = (short)Math.Clamp((int)MathF.Round(a[0] * reciprocal[0]), -2047, 2047);
        for (int k = 1; k < 64; k++)
        {
            int n = ZigZag[k];
            zz[k] = (short)Math.Clamp((int)MathF.Round(a[n] * reciprocal[n]), -1023, 1023);
        }
    }

    /// <summary>dst = m · src, with each row of the 8x8 block as a vector.</summary>
    static void MulLeft(float[] m, ReadOnlySpan<float> src, Span<float> dst)
    {
        ref float s = ref MemoryMarshal.GetReference(src);
        ref float d = ref MemoryMarshal.GetReference(dst);
        var r0 = Vector256.LoadUnsafe(ref s, 0); var r1 = Vector256.LoadUnsafe(ref s, 8);
        var r2 = Vector256.LoadUnsafe(ref s, 16); var r3 = Vector256.LoadUnsafe(ref s, 24);
        var r4 = Vector256.LoadUnsafe(ref s, 32); var r5 = Vector256.LoadUnsafe(ref s, 40);
        var r6 = Vector256.LoadUnsafe(ref s, 48); var r7 = Vector256.LoadUnsafe(ref s, 56);
        for (int i = 0; i < 8; i++)
        {
            int o = i * 8;
            var acc = r0 * m[o] + r1 * m[o + 1] + r2 * m[o + 2] + r3 * m[o + 3] + r4 * m[o + 4] + r5 * m[o + 5] + r6 * m[o + 6] + r7 * m[o + 7];
            acc.StoreUnsafe(ref d, (nuint)o);
        }
    }

    /// <summary>dst = src · m: each output row mixes the rows of m by that row of src.</summary>
    static void MulRight(ReadOnlySpan<float> src, float[] m, Span<float> dst)
    {
        ref float r = ref MemoryMarshal.GetArrayDataReference(m);
        ref float d = ref MemoryMarshal.GetReference(dst);
        var m0 = Vector256.LoadUnsafe(ref r, 0); var m1 = Vector256.LoadUnsafe(ref r, 8);
        var m2 = Vector256.LoadUnsafe(ref r, 16); var m3 = Vector256.LoadUnsafe(ref r, 24);
        var m4 = Vector256.LoadUnsafe(ref r, 32); var m5 = Vector256.LoadUnsafe(ref r, 40);
        var m6 = Vector256.LoadUnsafe(ref r, 48); var m7 = Vector256.LoadUnsafe(ref r, 56);
        for (int i = 0; i < 8; i++)
        {
            int o = i * 8;
            var acc = m0 * src[o] + m1 * src[o + 1] + m2 * src[o + 2] + m3 * src[o + 3] + m4 * src[o + 4] + m5 * src[o + 5] + m6 * src[o + 6] + m7 * src[o + 7];
            acc.StoreUnsafe(ref d, (nuint)o);
        }
    }
}

internal sealed class HuffEncoder
{
    public readonly uint[] Code = new uint[256];
    public readonly int[] Length = new int[256];

    public HuffEncoder(HuffSpec spec)
    {
        uint code = 0;
        int k = 0;
        for (int len = 1; len <= 16; len++)
        {
            for (int i = 0; i < spec.Bits[len]; i++, k++, code++)
            {
                Code[spec.Vals[k]] = code;
                Length[spec.Vals[k]] = len;
            }
            code <<= 1;
        }
    }
}

internal sealed class HuffDecoder
{
    const int FastBits = 9;
    readonly ushort[] _fast = new ushort[1 << FastBits]; // length << 8 | symbol, 0 = longer code
    readonly int[] _maxCode = new int[17];
    readonly int[] _valPtr = new int[17];
    readonly byte[] _vals;

    public HuffDecoder(HuffSpec spec)
    {
        _vals = spec.Vals;
        int code = 0, k = 0;
        for (int len = 1; len <= 16; len++)
        {
            _valPtr[len] = k - code;
            for (int i = 0; i < spec.Bits[len]; i++, k++, code++)
                if (len <= FastBits)
                {
                    int shift = FastBits - len;
                    for (int j = 0; j < 1 << shift; j++) _fast[code << shift | j] = (ushort)(len << 8 | spec.Vals[k]);
                }
            _maxCode[len] = spec.Bits[len] > 0 ? code - 1 : -1;
            code <<= 1;
        }
    }

    public int Decode(ref BitReader r)
    {
        r.Fill();
        int e = _fast[r.Peek(FastBits)];
        if (e != 0)
        {
            r.Skip(e >> 8);
            return e & 0xFF;
        }
        for (int len = FastBits + 1; len <= 16; len++)
        {
            int code = r.Peek(len);
            if (code <= _maxCode[len])
            {
                r.Skip(len);
                return _vals[_valPtr[len] + code];
            }
        }
        throw new InvalidDataException("Bad Huffman code.");
    }
}

/// <summary>Reads entropy-coded bits, dropping stuffed zeros; past a marker it reads zeros.</summary>
internal ref struct BitReader
{
    readonly ReadOnlySpan<byte> _d;
    int _pos, _n;
    ulong _acc; // next bit in the top position

    public BitReader(ReadOnlySpan<byte> data, int bytePos, int skipBits)
    {
        _d = data;
        _pos = bytePos;
        if (skipBits > 0) { Fill(); Skip(skipBits); }
    }

    public void Fill()
    {
        while (_n <= 56)
        {
            // Four bytes at a time while none of them is 0xFF (no stuffing or marker to handle).
            if (_n <= 32 && _pos + 4 <= _d.Length)
            {
                uint x = BinaryPrimitives.ReadUInt32BigEndian(_d[_pos..]);
                if (!BitWriter.HasFF(x))
                {
                    _acc |= (ulong)x << (32 - _n);
                    _n += 32;
                    _pos += 4;
                    continue;
                }
            }
            uint b = 0;
            if (_pos < _d.Length)
            {
                b = _d[_pos];
                if (b != 0xFF) _pos++;
                else if (_pos + 1 < _d.Length && _d[_pos + 1] == 0) _pos += 2;
                else b = 0; // a marker: stop here
            }
            _acc |= (ulong)b << (56 - _n);
            _n += 8;
        }
    }

    public int Peek(int n) => (int)(_acc >> (64 - n));

    public void Skip(int n)
    {
        _acc <<= n;
        _n -= n;
    }

    public int GetBits(int n)
    {
        if (_n < n) Fill();
        int v = Peek(n);
        Skip(n);
        return v;
    }
}

/// <summary>Writes entropy-coded bits, stuffing a zero after every 0xFF.</summary>
internal ref struct BitWriter(Span<byte> output)
{
    readonly Span<byte> _o = output;
    int _p, _n;
    ulong _acc; // pending bits in the top positions

    /// <summary>Data bits written so far (stuffing not counted).</summary>
    public long Bits { get; private set; }

    /// <summary>The byte the next bit goes into (writes out the whole bytes still pending).</summary>
    public int BytePosition()
    {
        while (_n >= 8) Emit8();
        return _p;
    }

    public void Put(uint v, int n) // n <= 32, v already masked to n bits
    {
        if (n == 0) return;
        _acc |= (ulong)v << (64 - _n - n);
        _n += n;
        Bits += n;
        if (_n < 32) return;
        uint x = (uint)(_acc >> 32);
        if (!HasFF(x))
        {
            BinaryPrimitives.WriteUInt32BigEndian(_o[_p..], x);
            _p += 4;
            _acc <<= 32;
            _n -= 32;
        }
        else
            while (_n >= 8) Emit8();
    }

    void Emit8()
    {
        byte b = (byte)(_acc >> 56);
        _o[_p++] = b;
        if (b == 0xFF) _o[_p++] = 0;
        _acc <<= 8;
        _n -= 8;
    }

    /// <summary>Pads the last byte with ones and writes everything out.</summary>
    public void Flush()
    {
        while (_n >= 8) Emit8();
        if (_n > 0) Put((1u << (8 - _n)) - 1, 8 - _n);
        while (_n >= 8) Emit8();
    }

    public static bool HasFF(uint x)
    {
        uint y = ~x; // a 0xFF byte in x is a zero byte in y
        return ((y - 0x01010101u) & ~y & 0x80808080u) != 0;
    }
}
