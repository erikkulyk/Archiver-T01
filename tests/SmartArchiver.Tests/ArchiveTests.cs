using SmartArchiver;
using Xunit;

public class ArchiveTests
{
    const string Pw = "algo2026";
    static byte[] Rand(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
    static byte[] Text(int n) => System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("hello archive world, hello again; ", n / 34 + 1)))[..n];
    static ArchiverOptions Opts(int maxVol = 65536) => new() { MaxVolume = maxVol };
    static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    static void AssertRoundTrip(IReadOnlyList<InputFile> files, ArchiverOptions? o = null)
    {
        var vols = ArchiveService.Pack(files, Pw, o ?? Opts());
        var back = ArchiveService.Unpack(vols, Pw).ToDictionary(x => x.Name, x => x.Data);
        Assert.Equal(files.Count, back.Count);
        foreach (var f in files) Assert.Equal(f.Data, back[f.Name]);
    }

    static ArchiveException Fails(Action a) => Assert.Throws<ArchiveException>(a);

    [Fact] public void OneTextFile() => AssertRoundTrip(new[] { new InputFile("a.txt", Text(10000)) });
    [Fact] public void EmptyArchive_OneVolume()
    {
        var vols = ArchiveService.Pack(Array.Empty<InputFile>(), Pw, Opts());
        Assert.Single(vols);
        Assert.Empty(ArchiveService.Unpack(vols, Pw));
    }
    [Fact] public void EmptyFile_And_OneByte() => AssertRoundTrip(new[] { new InputFile("e", Array.Empty<byte>()), new InputFile("o", new byte[] { 42 }) });
    [Fact] public void AllZeros() => AssertRoundTrip(new[] { new InputFile("z", new byte[50000]) });
    [Fact] public void ShorterThanMinMatch() => AssertRoundTrip(new[] { new InputFile("s", new byte[] { 1, 2 }) });

    [Fact] public void FileLargerThanVolume_TwoOrMoreVolumes_ReverseOrder()
    {
        var files = new[] { new InputFile("big.bin", Rand(150000, 1), 5), new InputFile("t.txt", Text(30000)) };
        var vols = ArchiveService.Pack(files, Pw, Opts());
        Assert.True(vols.Length >= 3);
        Assert.All(vols, v => Assert.True(v.Length <= 65536));
        var back = ArchiveService.Unpack(vols.Reverse().ToList(), Pw).ToDictionary(x => x.Name, x => x.Data);
        foreach (var f in files) Assert.Equal(f.Data, back[f.Name]);
    }

    [Fact] public void GreedyPacking_AlsoWorks()
        => AssertRoundTrip(new[] { new InputFile("a", Rand(90000, 2)), new InputFile("b", Text(20000)) }, new ArchiverOptions { UseDpPacking = false });

    [Fact] public void ReferenceTypes_AllClassesRoundTrip()
    {
        var files = Enumerable.Range(0, 7).Select(c => new InputFile($"f{c}", c == 6 ? new byte[5000] : (c == 5 ? Rand(5000, 9) : Text(5000)), (byte)c)).ToList();
        AssertRoundTrip(files);
    }


    [Theory]
    [InlineData("ref_01_single_file.arc", 1)]
    [InlineData("ref_02_empty_file.arc", 1)]
    [InlineData("ref_03_degenerate.arc", 1)]
    [InlineData("ref_04_seven_groups.arc", 7)]
    [InlineData("ref_05_no_compression.arc", 1)]
    public void InstructorReferenceArchives_Unpack(string name, int expectedFiles)
    {
        var result = ArchiveService.Unpack(new[] { Fixture(name) }, Pw);
        Assert.Equal(expectedFiles, result.Count);
    }

    [Fact] public void InstructorMultivolumeReferenceArchive_UnpacksInAnyOrder()
    {
        var volumes = new[]
        {
            Fixture("ref_06_two_volumes.part0.arc"),
            Fixture("ref_06_two_volumes.part1.arc"),
            Fixture("ref_06_two_volumes.part2.arc"),
        };
        var result = ArchiveService.Unpack(volumes.Reverse().ToList(), Pw);
        Assert.Equal(14, result.Count);
    }

    [Theory]
    [InlineData("bad_01_tampered_byte.arc", E.Hmac)]
    [InlineData("bad_02_tampered_tag.arc", E.Hmac)]
    [InlineData("bad_03_truncated.arc", E.Length)]
    [InlineData("bad_04_bad_magic.arc", E.Magic)]
    [InlineData("bad_05_reserved.arc", E.Payload)]
    [InlineData("bad_06_bad_crc.arc", E.Crc)]
    public void InstructorMalformedArchives_ReturnExpectedError(string name, string expectedCode)
    {
        var ex = Fails(() => ArchiveService.Unpack(new[] { Fixture(name) }, Pw));
        Assert.Equal(expectedCode, ex.Code);
    }

    // ---------- негативні ----------
    [Fact] public void WrongPassword_E_HMAC()
    {
        var v = ArchiveService.Pack(new[] { new InputFile("a", Text(100)) }, Pw, Opts());
        Assert.Equal(E.Hmac, Fails(() => ArchiveService.Unpack(v, "wrong")).Code);
    }

    [Fact] public void TamperedCiphertext_E_HMAC()
    {
        var v = ArchiveService.Pack(new[] { new InputFile("a", Text(100)) }, Pw, Opts());
        v[0][Format.HeaderSize + 3] ^= 1;
        Assert.Equal(E.Hmac, Fails(() => ArchiveService.Unpack(v, Pw)).Code);
    }

    [Fact] public void BadMagic_E_MAGIC()
    {
        var v = ArchiveService.Pack(new[] { new InputFile("a", Text(100)) }, Pw, Opts());
        v[0][0] ^= 1;
        Assert.Equal(E.Magic, Fails(() => ArchiveService.Unpack(v, Pw)).Code);
    }

    [Fact] public void TruncatedVolume_E_LENGTH()
    {
        var v = ArchiveService.Pack(new[] { new InputFile("a", Text(100)) }, Pw, Opts());
        v[0] = v[0][..^5];
        Assert.Equal(E.Length, Fails(() => ArchiveService.Unpack(v, Pw)).Code);
    }

    [Fact] public void MissingAndDuplicateVolume_E_VOLUMES()
    {
        var v = ArchiveService.Pack(new[] { new InputFile("a", Rand(150000, 4)) }, Pw, Opts());
        Assert.True(v.Length >= 3);
        Assert.Equal(E.Volumes, Fails(() => ArchiveService.Unpack(v.Skip(1).ToList(), Pw)).Code);
        Assert.Equal(E.Volumes, Fails(() => ArchiveService.Unpack(new[] { v[0], v[1], v[1] }.Concat(v.Skip(2)).ToList(), Pw)).Code);
    }

    [Fact] public void UnsafeNames_RejectedOnPack()
    {
        foreach (var n in new[] { "", ".", "..", "a/b", "a\\b", "C:x", "a\0b", "../s" })
            Assert.Equal(E.Payload, Fails(() => ArchiveService.Pack(new[] { new InputFile(n, new byte[1]) }, Pw, Opts())).Code);
    }

    // Спеціально сформовані архіви (інший «автор» томів): небезпечне ім'я і поганий CRC
    static byte[][] Craft(VolumePayload p) => CraftRaw(Format.WritePayload(p));

    static byte[][] CraftRaw(byte[] payload)
    {
        var salt = new byte[16]; var prefix = new byte[8];
        var (ke, km) = Crypto.DeriveKeys(Pw, salt);
        return new[] { ArchiveService.SealVolume(payload, 0, 1, ke, km, salt, prefix) };
    }

    [Fact] public void CraftedUnsafeName_E_PAYLOAD()
    {
        // A validly authenticated payload with a path traversal name.
        var payload = Convert.FromHexString("0001000000072E2E2F6576696C000000000000000000010001000000000000000000000000000001");
        Assert.Equal(E.Payload, Fails(() => ArchiveService.Unpack(CraftRaw(payload), Pw)).Code);
    }

    [Fact] public void CraftedBadCrc_E_CRC()
    {
        var data = new byte[] { 1, 2, 3 };
        var p = new VolumePayload(); p.Files.Add(new FileEntry("a", 3, Crc32.Compute(data) ^ 1, 0, 1));
        p.Blocks.Add(new BlockRecord(0, 0, 3, 3, BlockCodec.FlagNoCompression)); p.Data.Add(data);
        Assert.Equal(E.Crc, Fails(() => ArchiveService.Unpack(Craft(p), Pw)).Code);
    }

    [Fact] public void CraftedBlockSeqGap_E_PAYLOAD()
    {
        var data = new byte[] { 1, 2, 3 };
        var p = new VolumePayload(); p.Files.Add(new FileEntry("a", 3, Crc32.Compute(data), 0, 1));
        p.Blocks.Add(new BlockRecord(0, 1, 3, 3, BlockCodec.FlagNoCompression)); p.Data.Add(data);
        Assert.Equal(E.Payload, Fails(() => ArchiveService.Unpack(Craft(p), Pw)).Code);
    }

    [Fact] public void Metric_Q_IsZeroWhenNotRestored()
        => Assert.Equal(0, Metric.Q(0.7, 0.3, 0.9, 0.9, false));

    [Fact] public void Ga_Elitism_And_SameEvalsAsRandom()
    {
        double Fit(Chromosome c) => -Math.Abs(c.WindowExp - 13) - Math.Abs(c.MinMatch - 4) - Math.Abs(c.Threshold - 0.95);
        var ga = Genetic.RunGa(Fit, 100, 10, new Random(1)); var rs = Genetic.RunRandom(Fit, 100, new Random(2));
        Assert.Equal(100, ga.Evals); Assert.Equal(100, rs.Evals);
        for (int i = 1; i < ga.BestPerGeneration.Count; i++) Assert.True(ga.BestPerGeneration[i] >= ga.BestPerGeneration[i - 1]);
    }

    [Fact] public void Mlp_LearnsSeparableToyData()
    {
        var rng = new Random(0); var xs = new List<double[]>(); var ys = new List<int>();
        for (int i = 0; i < 200; i++) { int c = i % 7; var x = new double[Features.Count]; x[c] = 1 + rng.NextDouble() * 0.1; xs.Add(x); ys.Add(c); }
        var m = Mlp.Create(Features.Count, 16, 7, new Random(1)); m.Train(xs, ys, 60, 0.05, new Random(2));
        Assert.True(Classifier.MacroF1(ys, xs.Select(m.Predict).ToList()) > 0.95);
    }
}
