namespace SmartArchiver;

public sealed record CodecParams(
    int Window = 32768, int MinMatch = 3, double NoCompThreshold = 1.0,
    ParseMode Mode = ParseMode.Lazy, int MaxChain = 64, bool ForceRaw = false);

/// <summary>
/// Block encoding verified against the instructor reference archives:
/// three sparse canonical-Huffman tables (count u16, then symbol u16 + length u8),
/// token_count u32, bitstream_size u32, and an MSB-first token bitstream.
/// </summary>
public static class BlockCodec
{
    public const byte FlagNoCompression = 1;
    public const int LitSize = 257, LenSize = 256, DistSize = 16;
    public const int MaxRawLen = 1 << 26;

    public static (byte Flags, byte[] Data) Encode(byte[] raw, CodecParams p)
    {
        if (p.ForceRaw || raw.Length == 0) return (FlagNoCompression, raw);
        var toks = Lz77.Parse(raw, p.Mode, p.Window, p.MinMatch, p.MaxChain);
        var fl = new long[LitSize]; var fn = new long[LenSize]; var fd = new long[DistSize];
        foreach (var t in toks)
        {
            if (!t.IsMatch) fl[t.Lit]++;
            else { fl[256]++; fn[t.Len - 3]++; fd[Lz77.BitLength(t.Dist - 1)]++; }
        }
        var ll = Huffman.BuildLengths(fl); var nl = Huffman.BuildLengths(fn); var dl = Huffman.BuildLengths(fd);
        var lc = Huffman.CanonicalCodes(ll); var nc = Huffman.CanonicalCodes(nl); var dc = Huffman.CanonicalCodes(dl);

        var prefix = new List<byte>();
        void U16(int value) { prefix.Add((byte)(value >> 8)); prefix.Add((byte)value); }
        void U32(uint value)
        {
            prefix.Add((byte)(value >> 24)); prefix.Add((byte)(value >> 16));
            prefix.Add((byte)(value >> 8)); prefix.Add((byte)value);
        }
        void WriteTable(int[] lengths)
        {
            int count = lengths.Count(length => length > 0);
            U16(count);
            for (int symbol = 0; symbol < lengths.Length; symbol++)
            {
                if (lengths[symbol] == 0) continue;
                U16(symbol);
                prefix.Add((byte)lengths[symbol]);
            }
        }

        WriteTable(ll);
        WriteTable(nl);
        WriteTable(dl);

        var bw = new BitWriter();
        foreach (var t in toks)
        {
            if (!t.IsMatch) { bw.Write(lc[t.Lit], ll[t.Lit]); continue; }
            bw.Write(lc[256], ll[256]);
            bw.Write(nc[t.Len - 3], nl[t.Len - 3]);
            int v = t.Dist - 1, code = Lz77.BitLength(v);
            bw.Write(dc[code], dl[code]);
            if (code >= 2) bw.Write((uint)(v - (1 << (code - 1))), code - 1);
        }

        var bitstream = bw.ToArray();
        U32((uint)toks.Count);
        U32((uint)bitstream.Length);
        prefix.AddRange(bitstream);
        var coded = prefix.ToArray();

        double thr = Math.Min(1.0, p.NoCompThreshold);
        if (coded.Length >= (int)(raw.Length * thr)) return (FlagNoCompression, raw);
        return (0, coded);
    }

    public static byte[] Decode(byte flags, byte[] coded, int rawLen)
    {
        if (rawLen < 0 || rawLen > MaxRawLen) throw new ArchiveException(E.Payload, "bad raw_len");
        if ((flags & FlagNoCompression) != 0)
        {
            if ((flags & ~FlagNoCompression) != 0) throw new ArchiveException(E.Payload, "unknown block flags");
            if (coded.Length != rawLen) throw new ArchiveException(E.Payload, "raw block length mismatch");
            return coded;
        }
        if (flags != 0) throw new ArchiveException(E.Payload, "unknown block flags");

        int pos = 0;
        int ReadU16()
        {
            if (pos + 2 > coded.Length) throw new ArchiveException(E.Payload, "truncated Huffman table");
            int value = (coded[pos] << 8) | coded[pos + 1];
            pos += 2;
            return value;
        }
        uint ReadU32()
        {
            if (pos + 4 > coded.Length) throw new ArchiveException(E.Payload, "truncated block header");
            uint value = ((uint)coded[pos] << 24) | ((uint)coded[pos + 1] << 16) |
                         ((uint)coded[pos + 2] << 8) | coded[pos + 3];
            pos += 4;
            return value;
        }
        int[] ReadTable(int alphabetSize)
        {
            int count = ReadU16();
            if (count > alphabetSize) throw new ArchiveException(E.Code, "Huffman table has too many symbols");
            var lengths = new int[alphabetSize];
            for (int i = 0; i < count; i++)
            {
                int symbol = ReadU16();
                if (symbol >= alphabetSize || lengths[symbol] != 0)
                    throw new ArchiveException(E.Code, "invalid or duplicate Huffman symbol");
                int length = pos < coded.Length ? coded[pos++] : throw new ArchiveException(E.Payload, "truncated Huffman table");
                if (length < 1 || length > Huffman.MaxLen)
                    throw new ArchiveException(E.Code, "Huffman code length out of range");
                lengths[symbol] = length;
            }
            return lengths;
        }

        var lit = new Huffman.Decoder(ReadTable(LitSize));
        var len = new Huffman.Decoder(ReadTable(LenSize));
        var dst = new Huffman.Decoder(ReadTable(DistSize));
        uint tokenCount = ReadU32();
        uint bitstreamLength = ReadU32();
        if (tokenCount == 0 || tokenCount > rawLen || bitstreamLength > int.MaxValue || bitstreamLength != coded.Length - pos)
            throw new ArchiveException(E.Payload, "invalid token count or bitstream length");

        var bitstream = coded.AsSpan(pos, (int)bitstreamLength).ToArray();
        var br = new BitReader(bitstream);
        var output = new byte[rawLen];
        int written = 0;
        for (uint token = 0; token < tokenCount; token++)
        {
            int symbol = lit.Decode(br);
            if (symbol < 256)
            {
                if (written >= rawLen) throw new ArchiveException(E.Payload, "literal exceeds raw_len");
                output[written++] = (byte)symbol;
                continue;
            }

            int matchLength = len.Decode(br) + 3;
            int code = dst.Decode(br);
            int value = code < 2 ? code : (1 << (code - 1)) | (int)br.ReadBits(code - 1);
            int distance = value + 1;
            if (distance > written || distance > Lz77.MaxWindow)
                throw new ArchiveException(E.Payload, "distance out of range");
            if (written + matchLength > rawLen)
                throw new ArchiveException(E.Payload, "match exceeds block");
            for (int k = 0; k < matchLength; k++)
            {
                output[written] = output[written - distance];
                written++;
            }
        }
        if (written != rawLen) throw new ArchiveException(E.Payload, "decoded block length does not match raw_len");
        return output;
    }
}
