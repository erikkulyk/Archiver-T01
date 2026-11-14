using System.Text;

namespace SmartArchiver;

public sealed record FileEntry(string Name, long OrigSize, uint Crc, byte Type, byte Strategy);
public sealed record BlockRecord(int FileIndex, int Seq, int RawLen, int CodedLen, byte Flags);
public sealed record VolumeHeader(ushort Index, ushort Count, byte[] Salt, byte[] Nonce, uint CipherLen);

public sealed class VolumePayload
{
    public List<FileEntry> Files { get; } = new();
    public List<BlockRecord> Blocks { get; } = new();
    public List<byte[]> Data { get; } = new();
}

/// <summary>
/// Формат заголовка і payload звірено з еталонними архівами викладача.
/// Заголовок (41): magic[4] version[1] volume_index[2] volume_count[2] salt[16] nonce[12] cipher_len[4].
/// Payload: file_count[2] reserved[2]=0; file entries (name_len[2], UTF-8 name, orig_size[4], crc32[4], type[1], strategy[1]);
/// block_count[2]; block records (file_index[2], seq[4], raw_len[4], coded_len[4], flags[1]); coded block data.
/// </summary>
public static class Format
{
    public static readonly byte[] Magic = "SARC"u8.ToArray();
    public const byte Version = 1;
    public const int HeaderSize = 41, MacSize = 32, PayloadFixed = 6, BlockRecSize = 15, MaxNameBytes = 1024;

    public static int FileEntrySize(string name) => 2 + Encoding.UTF8.GetByteCount(name) + 4 + 4 + 1 + 1;

    public static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || name == "." || name == ".."
            || name.IndexOfAny(new[] { '/', '\\', ':', '\0' }) >= 0
            || Encoding.UTF8.GetByteCount(name) > MaxNameBytes)
            throw new ArchiveException(E.Payload, "unsafe file name");
    }

    // ---------- заголовок ----------
    public static byte[] WriteHeader(ushort index, ushort count, byte[] salt, byte[] nonce, uint cipherLen)
    {
        var h = new byte[HeaderSize]; int p = 0;
        Magic.CopyTo(h, p); p += 4; h[p++] = Version;
        W16(h, ref p, index); W16(h, ref p, count);
        salt.CopyTo(h, p); p += 16; nonce.CopyTo(h, p); p += 12;
        W32(h, ref p, cipherLen);
        return h;
    }

    public static VolumeHeader ReadHeader(byte[] vol)
    {
        if (vol.Length >= 4 && !vol.AsSpan(0, 4).SequenceEqual(Magic)) throw new ArchiveException(E.Magic, "bad magic");
        if (vol.Length < HeaderSize + MacSize) throw new ArchiveException(E.Length, "volume too short");
        if (vol[4] != Version) throw new ArchiveException(E.Version, "unsupported version");
        var r = new Rd(vol, 5, E.Length);
        ushort idx = r.U16(), cnt = r.U16();
        var salt = r.Bytes(16); var nonce = r.Bytes(12); uint clen = r.U32();
        if ((long)HeaderSize + clen + MacSize != vol.Length) throw new ArchiveException(E.Length, "cipher_len mismatch");
        return new VolumeHeader(idx, cnt, salt, nonce, clen);
    }

    // ---------- payload ----------
    public static byte[] WritePayload(VolumePayload p)
    {
        if (p.Files.Count > ushort.MaxValue || p.Blocks.Count > ushort.MaxValue)
            throw new ArchiveException(E.Capacity, "too many files or blocks in one volume");
        if (p.Data.Count != p.Blocks.Count)
            throw new ArchiveException(E.Payload, "block records and block data counts differ");

        var b = new List<byte>();
        void U16(int v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
        void U32(uint v) { for (int s = 24; s >= 0; s -= 8) b.Add((byte)(v >> s)); }

        U16(p.Files.Count);
        U16(0); // reserved: must be zero
        foreach (var f in p.Files)
        {
            ValidateName(f.Name);
            var nb = Encoding.UTF8.GetBytes(f.Name);
            if (nb.Length > MaxNameBytes || f.OrigSize < 0 || f.OrigSize > uint.MaxValue)
                throw new ArchiveException(E.Payload, "file name or original size is out of range");
            U16(nb.Length);
            b.AddRange(nb);
            U32((uint)f.OrigSize);
            U32(f.Crc);
            b.Add(f.Type);
            b.Add(f.Strategy);
        }

        U16(p.Blocks.Count);
        for (int i = 0; i < p.Blocks.Count; i++)
        {
            var r = p.Blocks[i];
            if (r.FileIndex < 0 || r.FileIndex >= p.Files.Count || r.FileIndex > ushort.MaxValue ||
                r.Seq < 0 || r.RawLen < 0 || r.RawLen > BlockCodec.MaxRawLen || r.CodedLen < 0 ||
                p.Data[i].Length != r.CodedLen)
                throw new ArchiveException(E.Payload, "block field or data length is out of range");
            U16(r.FileIndex);
            U32((uint)r.Seq);
            U32((uint)r.RawLen);
            U32((uint)r.CodedLen);
            b.Add(r.Flags);
        }
        foreach (var d in p.Data) b.AddRange(d);
        return b.ToArray();
    }

    public static VolumePayload ReadPayload(byte[] pt)
    {
        var r = new Rd(pt, 0, E.Payload);
        int fileCount = r.U16();
        ushort reserved = r.U16();
        if (reserved != 0) throw new ArchiveException(E.Payload, "reserved field must be zero");

        var res = new VolumePayload();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var strict = new UTF8Encoding(false, true);
        for (int i = 0; i < fileCount; i++)
        {
            int nl = r.U16();
            if (nl > MaxNameBytes) throw new ArchiveException(E.Payload, "file name is too long");
            string name;
            try { name = strict.GetString(r.Bytes(nl)); }
            catch (DecoderFallbackException) { throw new ArchiveException(E.Payload, "file name is not valid UTF-8"); }
            ValidateName(name);
            if (!names.Add(name)) throw new ArchiveException(E.Payload, "duplicate file name");
            long size = r.U32();
            if (size > int.MaxValue) throw new ArchiveException(E.Payload, "file is too large for this implementation");
            uint crc = r.U32();
            byte type = r.Byte(), strat = r.Byte();
            res.Files.Add(new FileEntry(name, size, crc, type, strat));
        }

        int blockCount = r.U16();
        if ((long)blockCount * BlockRecSize > r.Remaining)
            throw new ArchiveException(E.Payload, "block table exceeds payload");
        long total = 0;
        for (int i = 0; i < blockCount; i++)
        {
            int fi = r.U16();
            uint seq = r.U32(), raw = r.U32(), coded = r.U32();
            byte fl = r.Byte();
            if (fi >= fileCount) throw new ArchiveException(E.Payload, "file_index out of range");
            if (seq > int.MaxValue || raw > BlockCodec.MaxRawLen || coded > int.MaxValue)
                throw new ArchiveException(E.Payload, "block field out of range");
            total += coded;
            if (total > int.MaxValue) throw new ArchiveException(E.Payload, "block data is too large");
            res.Blocks.Add(new BlockRecord(fi, (int)seq, (int)raw, (int)coded, fl));
        }
        if (total != r.Remaining) throw new ArchiveException(E.Payload, "block data length mismatch");
        foreach (var br in res.Blocks) res.Data.Add(r.Bytes(br.CodedLen));
        return res;
    }

    private static void W16(byte[] b, ref int p, int v) { b[p++] = (byte)(v >> 8); b[p++] = (byte)v; }
    private static void W32(byte[] b, ref int p, uint v) { for (int s = 24; s >= 0; s -= 8) b[p++] = (byte)(v >> s); }

    private sealed class Rd
    {
        private readonly byte[] _d; private int _p; private readonly string _code;
        public Rd(byte[] d, int start, string code) { _d = d; _p = start; _code = code; }
        public int Remaining => _d.Length - _p;
        private void Need(int n) { if (n < 0 || n > Remaining) throw new ArchiveException(_code, "truncated data"); }
        public byte Byte() { Need(1); return _d[_p++]; }
        public ushort U16() { Need(2); var v = (ushort)((_d[_p] << 8) | _d[_p + 1]); _p += 2; return v; }
        public uint U32() { Need(4); uint v = 0; for (int i = 0; i < 4; i++) v = (v << 8) | _d[_p + i]; _p += 4; return v; }
        public long I64() { Need(8); ulong v = 0; for (int i = 0; i < 8; i++) v = (v << 8) | _d[_p + i]; _p += 8; return unchecked((long)v); }
        public byte[] Bytes(int n) { Need(n); var r = _d.AsSpan(_p, n).ToArray(); _p += n; return r; }
    }
}
