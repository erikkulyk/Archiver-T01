using SmartArchiver;
using Xunit;

public class CodecTests
{
    static byte[] Rand(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
    static byte[] Text(int n) { var s = string.Concat(Enumerable.Repeat("the quick brown fox jumps over the lazy dog; ", n / 45 + 1)); return System.Text.Encoding.ASCII.GetBytes(s)[..n]; }

    // ---------- Гаффман ----------
    [Fact] public void Huffman_SingleSymbol_Length1()
    {
        var l = Huffman.BuildLengths(new long[] { 0, 7, 0 });
        Assert.Equal(new[] { 0, 1, 0 }, l);
        Assert.Equal(0u, Huffman.CanonicalCodes(l)[1]);
    }

    [Fact] public void Huffman_LengthLimit15_AndKraft()
    {
        var f = new long[40]; long a = 1, b = 1;
        for (int i = 0; i < f.Length; i++) { f[i] = a; (a, b) = (b, a + b); }   // Фібоначчі дає глибоке дерево
        var l = Huffman.BuildLengths(f);
        Assert.True(l.Max() <= 15);
        Huffman.Validate(l);
    }

    [Fact] public void Huffman_BadKraft_Rejected()
    {
        var ex = Assert.Throws<ArchiveException>(() => Huffman.Validate(new[] { 1, 2, 0 }));
        Assert.Equal(E.Code, ex.Code);
    }

    // ---------- LZ77 ----------
    [Theory] [InlineData(ParseMode.Greedy)] [InlineData(ParseMode.Lazy)] [InlineData(ParseMode.Dp)]
    public void Lz_RoundTrip(ParseMode m)
    {
        foreach (var d in new[] { Text(5000), Rand(3000, 1), new byte[2000], new byte[] { 1 }, new byte[] { 1, 2 }, Array.Empty<byte>() })
            Assert.Equal(d, Lz77.Expand(Lz77.Parse(d, m, 32768, 3, 64)));
    }

    static long Brute(byte[] d, int i, Dictionary<int, long> memo)
    {
        if (i == d.Length) return 0;
        if (memo.TryGetValue(i, out var v)) return v;
        long best = Lz77.LiteralCost + Brute(d, i + 1, memo);
        for (int dist = 1; dist <= i; dist++)
            for (int len = 3; i + len <= d.Length && len <= 258; len++)
            {
                bool ok = true; for (int k = 0; k < len; k++) if (d[i - dist + k] != d[i + k]) { ok = false; break; }
                if (!ok) break;
                best = Math.Min(best, Lz77.MatchCost(dist) + Brute(d, i + len, memo));
            }
        return memo[i] = best;
    }

    [Fact] public void Dp_CostEqualsBruteForce()
    {
        var rng = new Random(5);
        for (int t = 0; t < 40; t++)
        {
            var d = new byte[rng.Next(1, 40)];
            for (int i = 0; i < d.Length; i++) d[i] = (byte)('a' + rng.Next(3));
            var dp = Lz77.Parse(d, ParseMode.Dp, 32768, 3, int.MaxValue);
            Assert.Equal(Brute(d, 0, new()), Lz77.Cost(dp));
        }
    }

    [Fact] public void Dp_NotWorseThanGreedyOrLazy()
    {
        var d = Text(8000);
        var dp = Lz77.Cost(Lz77.Parse(d, ParseMode.Dp, 32768, 3, 4096));
        Assert.True(dp <= Lz77.Cost(Lz77.Parse(d, ParseMode.Greedy, 32768, 3, 4096)));
        Assert.True(dp <= Lz77.Cost(Lz77.Parse(d, ParseMode.Lazy, 32768, 3, 4096)));
    }

    [Fact] public void Expand_RejectsBadDistance()
    {
        Assert.Throws<ArchiveException>(() => Lz77.Expand(new[] { Token.Literal(1), Token.Match(2, 3) }));
        Assert.Throws<ArchiveException>(() => Lz77.Expand(new[] { Token.Literal(1), Token.Match(0, 3) }));
    }

    // ---------- блок ----------
    [Fact] public void Block_RoundTrip_AllModes()
    {
        foreach (var m in Enum.GetValues<ParseMode>())
            foreach (var d in new[] { Text(16384), new byte[16384], new byte[] { 9 } })
            {
                var (fl, c) = BlockCodec.Encode(d, new CodecParams(Mode: m));
                Assert.Equal(d, BlockCodec.Decode(fl, c, d.Length));
            }
    }


    [Fact] public void Block_DecodesInstructorReferenceBlock()
    {
        // Coded block from reference/ref_03_degenerate.arc (5000 repetitions of 'A').
        byte[] coded = Convert.FromHexString(
            "00020041010100010002005E0100FF01000100000100000015000000086DB6DB6DB6DB6DA0");
        var decoded = BlockCodec.Decode(0, coded, 5000);
        Assert.Equal(new byte[5000].Select(_ => (byte)'A'), decoded);
        Assert.Equal(0x131A7BBEu, Crc32.Compute(decoded));
    }

    [Fact] public void Block_Zeros_CompressSeveralTimes()
    {
        var (fl, c) = BlockCodec.Encode(new byte[16384], new CodecParams());
        Assert.Equal(0, fl);
        Assert.True(c.Length * 4 < 16384);
    }

    [Fact] public void Block_RandomNoise_FallsBackToRaw()
    {
        var d = Rand(16384, 3);
        var (fl, c) = BlockCodec.Encode(d, new CodecParams());
        Assert.Equal(BlockCodec.FlagNoCompression, fl);
        Assert.Equal(d.Length, c.Length);
    }

    // ---------- рюкзак ----------
    [Fact]
    public void Knapsack_Dp_KnownExample()
    {
        var sel = Knapsack.Dp(new[] { 5, 4, 6, 3 }, new long[] { 10, 40, 30, 50 }, 10);
        Assert.Equal(90, Knapsack.Value(sel, new long[] { 10, 40, 30, 50 }));
    }

    [Fact]
    public void Knapsack_Dp_NotWorseThanGreedy_AndFits()
    {
        var rng = new Random(11);
        for (int t = 0; t < 50; t++)
        {
            int n = rng.Next(1, 25);
            var w = Enumerable.Range(0, n).Select(_ => rng.Next(1, 300)).ToArray();
            var v = Enumerable.Range(0, n).Select(_ => (long)rng.Next(1, 1000)).ToArray();
            long cap = rng.Next(0, 1500);
            var dp = Knapsack.Dp(w, v, cap); var gr = Knapsack.Greedy(w, v, cap);
            Assert.True(dp.Sum(i => w[i]) <= cap);
            Assert.True(Knapsack.Value(dp, v) >= Knapsack.Value(gr, v));
        }
    }

    [Fact]
    public void Knapsack_Dp_EqualsBruteForce()
    {
        var rng = new Random(21);
        for (int t = 0; t < 100; t++)
        {
            int n = rng.Next(1, 13);
            var w = Enumerable.Range(0, n).Select(_ => rng.Next(1, 200)).ToArray();
            var v = Enumerable.Range(0, n).Select(_ => (long)rng.Next(1, 1000)).ToArray();
            long cap = rng.Next(0, 800), best = 0;
            for (int mask = 0; mask < (1 << n); mask++)
            {
                long ws = 0, vs = 0;
                for (int i = 0; i < n; i++)
                    if (((mask >> i) & 1) != 0) { ws += w[i]; vs += v[i]; }
                if (ws <= cap && vs > best) best = vs;
            }
            Assert.Equal(best, Knapsack.Value(Knapsack.Dp(w, v, cap), v));
        }
    }

    [Fact] public void Crc32_KnownVector() => Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
}

