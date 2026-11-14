using SmartArchiver;
using Xunit;

/// <summary>
/// Інтеграційні та fuzz-тести (методичка 6.24, 6.25, 6.26): будь-які зіпсовані дані мають давати
/// лише контрольовану відмову ArchiveException з кодом, а не IndexOutOfRange, OverflowException тощо.
/// </summary>
public class IntegrationFuzzTests
{
    const string Pw = "algo2026";
    static readonly string[] KnownCodes = { E.Magic, E.Version, E.Length, E.Hmac, E.Payload, E.Code, E.Crc, E.Volumes, E.Capacity };

    static List<InputFile> SampleFiles()
    {
        var rng = new Random(3);
        var noise = new byte[6000]; rng.NextBytes(noise);
        return new List<InputFile>
        {
            new("text.txt", System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Архіватор стискає текст. ", 400))), 0),
            new("zeros.dat", new byte[20000], 6),
            new("noise.bin", noise, 5),
            new("empty.txt", Array.Empty<byte>(), 0),
            new("one.bin", new byte[] { 42 }, 6),
        };
    }

    /// <summary>Виконує дію і перевіряє: або успіх, або ArchiveException з відомим кодом. Будь-який інший виняток — провал.</summary>
    static void OnlyControlledFailure(Action act)
    {
        try { act(); }
        catch (ArchiveException ex) { Assert.Contains(ex.Code, KnownCodes); }
    }

    [Fact] public void EndToEnd_MultiVolume_AnyOrder_RestoresAllFiles()
    {
        var files = SampleFiles();
        var vols = ArchiveService.Pack(files, Pw, new ArchiverOptions { MaxVolume = 4096, BlockSize = 1024 });
        Assert.True(vols.Length >= 2);                       // шум 6000 Б не влазить в один том 4096 Б
        Assert.All(vols, v => Assert.True(v.Length <= 4096));
        var shuffled = vols.OrderBy(_ => Guid.NewGuid()).ToList();
        var back = ArchiveService.Unpack(shuffled, Pw).ToDictionary(f => f.Name, f => f.Data);
        Assert.Equal(files.Count, back.Count);
        foreach (var f in files) Assert.Equal(f.Data, back[f.Name]);
    }

    [Fact] public void EndToEnd_TwoArchivesOfSameFiles_HaveDifferentSaltAndNonce()
    {
        var files = SampleFiles();
        var a = Format.ReadHeader(ArchiveService.Pack(files, Pw, new ArchiverOptions())[0]);
        var b = Format.ReadHeader(ArchiveService.Pack(files, Pw, new ArchiverOptions())[0]);
        Assert.False(a.Salt.SequenceEqual(b.Salt));
        Assert.False(a.Nonce.SequenceEqual(b.Nonce));
    }

    [Fact] public void Fuzz_RandomByteChanges_OnlyControlledErrors()
    {
        var vol = Assert.Single(ArchiveService.Pack(SampleFiles(), Pw, new ArchiverOptions()));
        var rng = new Random(2001);
        for (int t = 0; t < 60; t++)                       // кожен Unpack виконує PBKDF2 (100000 ітерацій), тому 60, а не тисячі
        {
            var bad = (byte[])vol.Clone();
            int changes = rng.Next(1, 4);
            for (int k = 0; k < changes; k++) bad[rng.Next(bad.Length)] ^= (byte)rng.Next(1, 256);
            // Будь-яка зміна байта має бути помічена: E_MAGIC / E_VERSION / E_LENGTH / E_HMAC.
            var ex = Assert.Throws<ArchiveException>(() => ArchiveService.Unpack(new[] { bad }, Pw));
            Assert.Contains(ex.Code, KnownCodes);
        }
    }

    [Fact] public void Fuzz_TruncateAndExtend_OnlyControlledErrors()
    {
        var vol = Assert.Single(ArchiveService.Pack(SampleFiles(), Pw, new ArchiverOptions()));
        foreach (int len in new[] { 0, 1, 3, 4, 5, 40, 41, 72, 73, 74, vol.Length / 2, vol.Length - 1 })
            Assert.Throws<ArchiveException>(() => ArchiveService.Unpack(new[] { vol[..len] }, Pw));
        Assert.Throws<ArchiveException>(() => ArchiveService.Unpack(new[] { vol.Concat(new byte[7]).ToArray() }, Pw));
    }

    [Fact] public void Fuzz_PayloadParser_GarbageWithValidHmac_OnlyControlledErrors()
    {
        // HMAC правильний, тому відмова має прийти саме від строгого parser'а payload, а не від криптографії.
        var salt = new byte[16]; var prefix = new byte[8];
        var (kEnc, kMac) = Crypto.DeriveKeys(Pw, salt);
        var rng = new Random(77);
        for (int t = 0; t < 30; t++)                        // повний шлях через Unpack
        {
            var payload = new byte[rng.Next(0, 300)]; rng.NextBytes(payload);
            var vol = ArchiveService.SealVolume(payload, 0, 1, kEnc, kMac, salt, prefix);
            OnlyControlledFailure(() => ArchiveService.Unpack(new[] { vol }, Pw));
        }
        for (int t = 0; t < 5000; t++)                      // сам parser payload, без криптографії — швидко
        {
            var payload = new byte[rng.Next(0, 300)]; rng.NextBytes(payload);
            OnlyControlledFailure(() => Format.ReadPayload(payload));
        }
    }

    [Fact] public void Fuzz_PayloadParser_MutatedRealPayload_OnlyControlledErrors()
    {
        // Справжній payload із пошкодженими полями (розміри, індекси, CRC, довжини імен) і правильним HMAC.
        var salt = new byte[16]; var prefix = new byte[8];
        var vol = Assert.Single(ArchiveService.Pack(SampleFiles(), Pw, new ArchiverOptions(), salt, prefix));
        var h = Format.ReadHeader(vol);
        var (kEnc, kMac) = Crypto.DeriveKeys(Pw, salt);
        var plain = Crypto.AesCtr(kEnc, h.Nonce, vol.AsSpan(Format.HeaderSize, (int)h.CipherLen).ToArray());
        var rng = new Random(5);
        for (int t = 0; t < 40; t++)
        {
            var p = (byte[])plain.Clone();
            int pos = rng.Next(Math.Min(p.Length, 200));             // переважно службова частина на початку
            p[pos] ^= (byte)rng.Next(1, 256);
            var bad = ArchiveService.SealVolume(p, 0, 1, kEnc, kMac, salt, prefix);
            OnlyControlledFailure(() => ArchiveService.Unpack(new[] { bad }, Pw));
        }
        for (int t = 0; t < 5000; t++)                      // ті самі пошкодження, але одразу в parser і декодер блоків
        {
            var p = (byte[])plain.Clone();
            for (int k = rng.Next(1, 4); k > 0; k--) p[rng.Next(p.Length)] ^= (byte)rng.Next(1, 256);
            OnlyControlledFailure(() =>
            {
                var pl = Format.ReadPayload(p);
                for (int i = 0; i < pl.Blocks.Count; i++) BlockCodec.Decode(pl.Blocks[i].Flags, pl.Data[i], pl.Blocks[i].RawLen);
            });
        }
    }
}
