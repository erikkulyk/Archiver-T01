using SmartArchiver;
using Xunit;

/// <summary>
/// Тести з обов'язкових списків методички (10.20, 10.22, 10.63, 10.64), яких раніше не було,
/// і точна перевірка рюкзака повним перебором (6.8, 10.36).
/// </summary>
public class SpecBoundaryTests
{
    const string Pw = "algo2026";

    static byte[] PackOne()
    {
        var data = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("hello archive ", 200)));
        return Assert.Single(ArchiveService.Pack(new[] { new InputFile("a.txt", data, 0) }, Pw, new ArchiverOptions()));
    }

    static string UnpackCode(byte[] vol) =>
        Assert.Throws<ArchiveException>(() => ArchiveService.Unpack(new[] { vol }, Pw)).Code;

    // ---------- заголовок тому (10.64) ----------

    [Fact] public void Header_BadVersion_Rejected_E_VERSION()
    {
        // Код E_VERSION ще не звірено з FORMAT_SPEC_P2.md: якщо специфікація вимагає інший код, змінити тут і в Errors.cs.
        var vol = PackOne();
        vol[4] = 2;                                   // байт 4 = version
        Assert.Equal(E.Version, UnpackCode(vol));
    }

    [Fact] public void Header_CipherLenLargerThanVolume_E_LENGTH()
    {
        var vol = PackOne();
        vol[40]++;                                    // байти 37..40 = cipher_len (big-endian), збільшуємо на 1
        Assert.Equal(E.Length, UnpackCode(vol));
    }

    [Fact] public void Header_ExtraByteAfterHmac_E_LENGTH()
    {
        var vol = PackOne();
        var longer = vol.Concat(new byte[] { 0 }).ToArray();   // cipher_len не відповідає фактичній довжині
        Assert.Equal(E.Length, UnpackCode(longer));
    }

    // ---------- межі DIST (10.20) ----------

    static List<Token> Prefix(int n) => Enumerable.Range(0, n).Select(i => Token.Literal((byte)(i * 7))).ToList();

    [Theory] [InlineData(32767)] [InlineData(32768)]
    public void Lz_Distance_AtWindowBoundary_Accepted(int dist)
    {
        var toks = Prefix(40000); toks.Add(Token.Match(dist, 3));
        var outp = Lz77.Expand(toks);
        Assert.Equal(40003, outp.Length);
        Assert.Equal(outp[40000 - dist], outp[40000]);
    }

    [Fact] public void Lz_Distance_32769_Rejected()
    {
        var toks = Prefix(40000); toks.Add(Token.Match(32769, 3));
        Assert.Throws<ArchiveException>(() => Lz77.Expand(toks));
    }

    [Fact] public void Lz_Distance32768_UsesDistCode15_AndRoundTrips()
    {
        // 32768 випадкових байтів з алфавіту 0..63 (щоб блок стискався Гаффманом, а не йшов у NO_COMPRESSION),
        // далі повтор їхнього початку: довгий збіг можливий лише на відстані рівно 32768.
        var rng = new Random(7);
        var head = Enumerable.Range(0, 32768).Select(_ => (byte)rng.Next(64)).ToArray();
        var d = head.Concat(head.Take(300)).ToArray();
        var toks = Lz77.Parse(d, ParseMode.Greedy, 32768, 3, 64);
        Assert.Contains(toks, t => t.IsMatch && t.Dist == 32768 && t.Len == 258);
        Assert.Equal(15, Lz77.BitLength(32768 - 1));            // максимальний DIST-код = 15, а не 16
        var (fl, coded) = BlockCodec.Encode(d, new CodecParams(Mode: ParseMode.Greedy));
        Assert.Equal(0, fl);
        Assert.Equal(d, BlockCodec.Decode(fl, coded, d.Length));
    }

    // ---------- символи поза алфавітом (10.22, 10.64) ----------

    /// <summary>Складає закодований блок із трьома розрідженими таблицями (count u16; symbol u16 + length u8).</summary>
    static byte[] Block((int Sym, int Len)[] lit, (int Sym, int Len)[] len, (int Sym, int Len)[] dist)
    {
        var b = new List<byte>();
        void Table((int Sym, int Len)[] t)
        {
            b.Add((byte)(t.Length >> 8)); b.Add((byte)t.Length);
            foreach (var (s, l) in t) { b.Add((byte)(s >> 8)); b.Add((byte)s); b.Add((byte)l); }
        }
        Table(lit); Table(len); Table(dist);
        b.AddRange(new byte[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 });  // token_count = 1, bitstream_size = 1, один байт бітового потоку
        return b.ToArray();
    }

    static readonly (int, int)[] OkLit = { (65, 1), (256, 1) };
    static readonly (int, int)[] OkLen = { (0, 1), (1, 1) };
    static readonly (int, int)[] OkDist = { (0, 1), (1, 1) };

    [Fact] public void Huffman_DistSymbol16_Rejected_E_CODE()
    {
        var ex = Assert.Throws<ArchiveException>(() => BlockCodec.Decode(0, Block(OkLit, OkLen, new[] { (15, 1), (16, 1) }), 10));
        Assert.Equal(E.Code, ex.Code);
    }

    [Fact] public void Huffman_LitSymbol257_Rejected_E_CODE()
    {
        var ex = Assert.Throws<ArchiveException>(() => BlockCodec.Decode(0, Block(new[] { (65, 1), (257, 1) }, OkLen, OkDist), 10));
        Assert.Equal(E.Code, ex.Code);
    }

    [Fact] public void Huffman_LenSymbol256_Rejected_E_CODE()
    {
        var ex = Assert.Throws<ArchiveException>(() => BlockCodec.Decode(0, Block(OkLit, new[] { (0, 1), (256, 1) }, OkDist), 10));
        Assert.Equal(E.Code, ex.Code);
    }

    [Fact] public void Huffman_CodeLength16_Rejected_E_CODE()
    {
        var ex = Assert.Throws<ArchiveException>(() => BlockCodec.Decode(0, Block(new[] { (65, 16), (256, 1) }, OkLen, OkDist), 10));
        Assert.Equal(E.Code, ex.Code);
    }

    // ---------- рюкзак: ДП = повний перебір ----------

    [Fact] public void Knapsack_Dp_EqualsBruteForce()
    {
        var rng = new Random(21);
        for (int t = 0; t < 200; t++)
        {
            int n = rng.Next(0, 13);
            var w = Enumerable.Range(0, n).Select(_ => rng.Next(1, 60)).ToArray();
            var v = Enumerable.Range(0, n).Select(_ => (long)rng.Next(1, 100)).ToArray();
            long cap = rng.Next(0, 200);

            long best = 0;
            for (int mask = 0; mask < (1 << n); mask++)
            {
                long sw = 0, sv = 0;
                for (int i = 0; i < n; i++) if ((mask >> i & 1) == 1) { sw += w[i]; sv += v[i]; }
                if (sw <= cap) best = Math.Max(best, sv);
            }

            var sel = Knapsack.Dp(w, v, cap);
            Assert.Equal(sel.Length, sel.Distinct().Count());           // кожен предмет не більше одного разу
            Assert.True(sel.Sum(i => w[i]) <= cap);                      // відновлене рішення вміщується
            Assert.Equal(best, Knapsack.Value(sel, v));                  // і має оптимальну цінність
        }
    }
}
