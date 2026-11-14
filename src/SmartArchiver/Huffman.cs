namespace SmartArchiver;

/// <summary>Канонічний Гаффман з обмеженням довжини 15 (package-merge).</summary>
public static class Huffman
{
    public const int MaxLen = 15;
    private sealed record Item(long W, List<int> Syms);

    public static int[] BuildLengths(long[] freq, int maxLen = MaxLen)
    {
        var len = new int[freq.Length];
        var syms = Enumerable.Range(0, freq.Length).Where(i => freq[i] > 0)
                             .OrderBy(i => freq[i]).ThenBy(i => i).ToArray();
        if (syms.Length == 0) return len;
        if (syms.Length == 1) { len[syms[0]] = 1; return len; }   // вироджений алфавіт: довжина 1, код 0
        if (syms.Length > (1 << maxLen)) throw new ArchiveException(E.Code, "alphabet too large");

        var leaves = syms.Select(s => new Item(freq[s], new List<int> { s })).ToList();
        var prev = leaves;
        for (int l = 1; l < maxLen; l++)
        {
            var packages = new List<Item>();
            for (int i = 0; i + 1 < prev.Count; i += 2)
            {
                var m = new List<int>(prev[i].Syms.Count + prev[i + 1].Syms.Count);
                m.AddRange(prev[i].Syms); m.AddRange(prev[i + 1].Syms);
                packages.Add(new Item(prev[i].W + prev[i + 1].W, m));
            }
            prev = Merge(leaves, packages);
        }
        int take = 2 * syms.Length - 2;
        for (int i = 0; i < take; i++) foreach (var s in prev[i].Syms) len[s]++;
        return len;
    }

    private static List<Item> Merge(List<Item> a, List<Item> b)
    {
        var r = new List<Item>(a.Count + b.Count);
        int i = 0, j = 0;
        while (i < a.Count || j < b.Count)
        {
            if (j >= b.Count || (i < a.Count && a[i].W <= b[j].W)) r.Add(a[i++]);
            else r.Add(b[j++]);
        }
        return r;
    }

    /// <summary>Парсер не «виправляє» таблицю: некоректні довжини дають E_CODE.</summary>
    public static void Validate(int[] lengths)
    {
        int n = 0; long kraft = 0; int single = 0;
        foreach (var l in lengths)
        {
            if (l < 0 || l > MaxLen) throw new ArchiveException(E.Code, "code length out of range");
            if (l > 0) { n++; kraft += 1L << (MaxLen - l); single = l; }
        }
        if (n == 0) return;
        if (n == 1) { if (single != 1) throw new ArchiveException(E.Code, "single symbol must have length 1"); return; }
        if (kraft != 1L << MaxLen) throw new ArchiveException(E.Code, "Kraft equality violated");
    }

    public static uint[] CanonicalCodes(int[] lengths)
    {
        var cnt = new int[MaxLen + 2];
        foreach (var l in lengths) if (l > 0) cnt[l]++;
        var next = new uint[MaxLen + 2];
        uint code = 0;
        for (int l = 1; l <= MaxLen; l++) { code = (code + (uint)cnt[l - 1]) << 1; next[l] = code; }
        var codes = new uint[lengths.Length];
        for (int s = 0; s < lengths.Length; s++) { int l = lengths[s]; if (l > 0) codes[s] = next[l]++; }
        return codes;
    }

    public sealed class Decoder
    {
        private readonly int[] _count = new int[MaxLen + 1];
        private readonly int[] _symbols;

        public Decoder(int[] lengths)
        {
            Validate(lengths);
            foreach (var l in lengths) if (l > 0) _count[l]++;
            var off = new int[MaxLen + 2];
            for (int l = 1; l <= MaxLen; l++) off[l + 1] = off[l] + _count[l];
            _symbols = new int[off[MaxLen + 1]];
            for (int s = 0; s < lengths.Length; s++) { int l = lengths[s]; if (l > 0) _symbols[off[l]++] = s; }
        }

        public int Decode(BitReader br)
        {
            int code = 0, first = 0, index = 0;
            for (int len = 1; len <= MaxLen; len++)
            {
                code |= br.ReadBit();
                int c = _count[len];
                if (code - c < first) return _symbols[index + (code - first)];
                index += c; first += c; first <<= 1; code <<= 1;
            }
            throw new ArchiveException(E.Code, "invalid Huffman code");
        }
    }
}
