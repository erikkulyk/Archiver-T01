using System.Security.Cryptography;

namespace SmartArchiver;

public sealed record InputFile(string Name, byte[] Data, byte Type = 0);
public sealed record OutputFile(string Name, byte[] Data);

public sealed class ArchiverOptions
{
    public CodecParams Codec { get; init; } = new();
    public int MaxVolume { get; init; } = 65536;
    public int BlockSize { get; init; } = 16384;
    public bool UseDpPacking { get; init; } = true;
    /// <summary>Пріоритет за класом файла (value блоку = пріоритет * raw_len).</summary>
    public Dictionary<byte, int> Priority { get; init; } = new() { [0] = 3, [1] = 3, [2] = 2, [3] = 2, [4] = 2, [5] = 1, [6] = 1 };
    /// <summary>Стратегія за класом: «вже стиснуте» → без стиснення; растр → ДП-розбір.</summary>
    public Func<byte, CodecParams> StrategyFor { get; init; } = null!;

    public CodecParams ForType(byte type)
    {
        if (StrategyFor != null) return StrategyFor(type);
        return type switch
        {
            5 => Codec with { ForceRaw = true },
            3 => Codec with { Mode = ParseMode.Dp },
            _ => Codec,
        };
    }
}

public static class ArchiveService
{
    private sealed record PackBlock(int FileIdx, int Seq, int RawLen, byte Flags, byte[] Data, long Value);

    public static byte[][] Pack(IReadOnlyList<InputFile> files, string password, ArchiverOptions o,
                                byte[]? salt = null, byte[]? noncePrefix = null)
    {
        if (o.BlockSize < 1 || o.BlockSize > BlockCodec.MaxRawLen)
            throw new ArchiveException(E.Capacity, "block_size is out of range");
        if (o.MaxVolume < Format.HeaderSize + Format.MacSize + Format.PayloadFixed)
            throw new ArchiveException(E.Capacity, "max_volume_bytes is too small");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            Format.ValidateName(f.Name);
            if (!names.Add(f.Name)) throw new ArchiveException(E.Payload, "duplicate file name");
        }
        var entries = new List<FileEntry>(); var blocks = new List<PackBlock>();
        for (int fi = 0; fi < files.Count; fi++)
        {
            var f = files[fi]; var cp = o.ForType(f.Type);
            entries.Add(new FileEntry(f.Name, f.Data.Length, Crc32.Compute(f.Data), f.Type, 1));
            int prio = o.Priority.TryGetValue(f.Type, out var pr) ? pr : 1;
            if (f.Data.Length == 0)
            {
                // The reference format represents an empty file with one zero-length RAW block.
                blocks.Add(new PackBlock(fi, 0, 0, BlockCodec.FlagNoCompression, Array.Empty<byte>(), 1));
                continue;
            }
            int seq = 0;
            for (int off = 0; off < f.Data.Length; off += o.BlockSize, seq++)
            {
                int l = Math.Min(o.BlockSize, f.Data.Length - off);
                var (fl, data) = BlockCodec.Encode(f.Data.AsSpan(off, l).ToArray(), cp);
                blocks.Add(new PackBlock(fi, seq, l, fl, data, (long)prio * l));
            }
        }

        var volumes = PackVolumes(entries, blocks, o);
        if (volumes.Count > ushort.MaxValue) throw new ArchiveException(E.Capacity, "too many volumes");

        salt ??= RandomNumberGenerator.GetBytes(16);
        noncePrefix ??= RandomNumberGenerator.GetBytes(8);
        var (kEnc, kMac) = Crypto.DeriveKeys(password, salt);        // PBKDF2 один раз на архів

        var result = new byte[volumes.Count][];
        for (int v = 0; v < volumes.Count; v++)
        {
            var order = volumes[v].Select(b => b.FileIdx).Distinct().OrderBy(x => x).ToList();
            var local = order.Select((fi, li) => (fi, li)).ToDictionary(t => t.fi, t => t.li);
            var p = new VolumePayload();
            foreach (var fi in order) p.Files.Add(entries[fi]);
            foreach (var b in volumes[v])
            {
                p.Blocks.Add(new BlockRecord(local[b.FileIdx], b.Seq, b.RawLen, b.Data.Length, b.Flags));
                p.Data.Add(b.Data);
            }
            result[v] = SealVolume(Format.WritePayload(p), (ushort)v, (ushort)volumes.Count, kEnc, kMac, salt, noncePrefix);
            if (result[v].Length > o.MaxVolume) throw new ArchiveException(E.Capacity, "volume exceeds limit");
        }
        return result;
    }

    public static byte[] SealVolume(byte[] payload, ushort index, ushort count, byte[] kEnc, byte[] kMac, byte[] salt, byte[] noncePrefix)
    {
        var nonce = new byte[12]; noncePrefix.AsSpan(0, 8).CopyTo(nonce);
        nonce[8] = 0; nonce[9] = 0; nonce[10] = (byte)(index >> 8); nonce[11] = (byte)index;   // різний nonce для кожного тому
        var ct = Crypto.AesCtr(kEnc, nonce, payload);
        var header = Format.WriteHeader(index, count, salt, nonce, (uint)ct.Length);
        var mac = Crypto.Mac(kMac, header, ct);
        var vol = new byte[header.Length + ct.Length + mac.Length];
        header.CopyTo(vol, 0); ct.CopyTo(vol, header.Length); mac.CopyTo(vol, header.Length + ct.Length);
        return vol;
    }

    private static List<List<PackBlock>> PackVolumes(List<FileEntry> entries, List<PackBlock> blocks, ArchiverOptions o)
    {
        var result = new List<List<PackBlock>>();
        var remaining = blocks.ToList();
        do
        {
            // Reserve the file table before solving the knapsack for this volume.
            var fileSet = new SortedSet<int>(remaining.Select(b => b.FileIdx));
            long table = fileSet.Sum(fi => Format.FileEntrySize(entries[fi].Name));
            long W = o.MaxVolume - Format.HeaderSize - Format.MacSize - Format.PayloadFixed - table;
            if (W < 0) throw new ArchiveException(E.Capacity, "file table does not fit into a volume");
            if (remaining.Count == 0) { result.Add(new List<PackBlock>()); break; }

            var weights = remaining.Select(b => b.Data.Length + Format.BlockRecSize).ToArray();
            var values = remaining.Select(b => b.Value).ToArray();
            var sel = o.UseDpPacking ? Knapsack.Dp(weights, values, W) : Knapsack.Greedy(weights, values, W);
            if (sel.Length == 0) throw new ArchiveException(E.Capacity, "block does not fit into an empty volume");
            var chosen = new HashSet<int>(sel);
            result.Add(sel.Select(i => remaining[i]).ToList());
            remaining = remaining.Where((_, i) => !chosen.Contains(i)).ToList();
        } while (remaining.Count > 0);
        return result;
    }

    public static List<OutputFile> Unpack(IReadOnlyList<byte[]> volumes, string password)
    {
        if (volumes.Count == 0) throw new ArchiveException(E.Volumes, "no volumes");
        var headers = volumes.Select(Format.ReadHeader).ToList();
        int count = headers[0].Count;
        if (headers.Any(h => h.Count != count)) throw new ArchiveException(E.Volumes, "volume_count differs");
        if (headers.Any(h => !h.Salt.AsSpan().SequenceEqual(headers[0].Salt))) throw new ArchiveException(E.Volumes, "salt differs");
        if (headers.Any(h => h.Index >= count)) throw new ArchiveException(E.Volumes, "volume_index out of range");
        if (headers.Select(h => h.Index).Distinct().Count() != headers.Count) throw new ArchiveException(E.Volumes, "duplicate volume");
        if (headers.Count != count) throw new ArchiveException(E.Volumes, "missing volumes");
        if (headers.Select(h => Convert.ToHexString(h.Nonce)).Distinct().Count() != headers.Count) throw new ArchiveException(E.Volumes, "nonce reuse");

        var (kEnc, kMac) = Crypto.DeriveKeys(password, headers[0].Salt);
        var order = Enumerable.Range(0, volumes.Count).OrderBy(i => headers[i].Index).ToList();   // порядок подання не важливий

        var meta = new Dictionary<string, FileEntry>();
        var parts = new Dictionary<string, SortedDictionary<int, (int Raw, byte Flags, byte[] Data)>>();
        var names = new List<string>();
        foreach (var vi in order)
        {
            var vol = volumes[vi]; var h = headers[vi];
            var ct = vol.AsSpan(Format.HeaderSize, (int)h.CipherLen).ToArray();
            var mac = vol.AsSpan(Format.HeaderSize + (int)h.CipherLen, Format.MacSize).ToArray();
            var expect = Crypto.Mac(kMac, vol.AsSpan(0, Format.HeaderSize).ToArray(), ct);
            if (!Crypto.Equal(mac, expect)) throw new ArchiveException(E.Hmac, "HMAC mismatch");   // до decrypt і parse
            var payload = Format.ReadPayload(Crypto.AesCtr(kEnc, h.Nonce, ct));

            foreach (var f in payload.Files)
            {
                if (meta.TryGetValue(f.Name, out var old)) { if (old != f) throw new ArchiveException(E.Payload, "file metadata differs between volumes"); }
                else { meta[f.Name] = f; parts[f.Name] = new(); names.Add(f.Name); }
            }
            for (int i = 0; i < payload.Blocks.Count; i++)
            {
                var b = payload.Blocks[i]; var name = payload.Files[b.FileIndex].Name;
                if (!parts[name].TryAdd(b.Seq, (b.RawLen, b.Flags, payload.Data[i]))) throw new ArchiveException(E.Payload, "duplicate block_seq");
            }
        }

        var res = new List<OutputFile>();
        foreach (var name in names)
        {
            var f = meta[name]; var p = parts[name];
            if (p.Count == 0) throw new ArchiveException(E.Payload, "file has no blocks");
            if (f.OrigSize == 0)
            {
                var emptyBlock = p.Values.First();
                if (p.Count != 1 || emptyBlock.Raw != 0 || emptyBlock.Flags != BlockCodec.FlagNoCompression || emptyBlock.Data.Length != 0)
                    throw new ArchiveException(E.Payload, "empty file must have one zero-length RAW block");
            }
            int expectSeq = 0; long sum = 0;
            foreach (var (seq, blk) in p) { if (seq != expectSeq++) throw new ArchiveException(E.Payload, "block_seq gap"); sum += blk.Raw; }
            if (sum != f.OrigSize) throw new ArchiveException(E.Payload, "sizes do not add up");
            var data = new byte[f.OrigSize]; int pos = 0;
            foreach (var blk in p.Values)
            {
                var raw = BlockCodec.Decode(blk.Flags, blk.Data, blk.Raw);
                raw.CopyTo(data, pos); pos += raw.Length;
            }
            if (Crc32.Compute(data) != f.Crc) throw new ArchiveException(E.Crc, "CRC32 mismatch");
            res.Add(new OutputFile(name, data));
        }
        return res;
    }
}
