using SmartArchiver;
using Xunit;

public class FormatTests
{
    [Fact]
    public void Payload_UsesInstructorFieldOrderAndWidths()
    {
        var payload = new VolumePayload();
        payload.Files.Add(new FileEntry("hello.txt", 480, 0x70208846, 0, 1));
        payload.Blocks.Add(new BlockRecord(0, 0, 480, 57, 0));
        payload.Data.Add(new byte[57]);

        var bytes = Format.WritePayload(payload);
        byte[] expectedPrefix = Convert.FromHexString(
            "00010000000968656C6C6F2E747874000001E07020884600010001000000000000000001E00000003900");
        Assert.Equal(expectedPrefix, bytes[..expectedPrefix.Length]);
        Assert.Equal(99, bytes.Length);

        var decoded = Format.ReadPayload(bytes);
        Assert.Equal("hello.txt", Assert.Single(decoded.Files).Name);
        Assert.Equal(480, decoded.Files[0].OrigSize);
        Assert.Equal((byte)1, decoded.Files[0].Strategy);
        var block = Assert.Single(decoded.Blocks);
        Assert.Equal(480, block.RawLen);
        Assert.Equal(57, block.CodedLen);
        Assert.Equal(0, block.Seq);
    }


    [Fact]
    public void Pack_EmptyFile_StoresZeroLengthRawBlock()
    {
        const string password = "algo2026";
        var volume = Assert.Single(ArchiveService.Pack(new[] { new InputFile("empty.dat", Array.Empty<byte>(), 6) }, password, new ArchiverOptions()));
        var header = Format.ReadHeader(volume);
        var (encKey, _) = Crypto.DeriveKeys(password, header.Salt);
        var cipherText = volume.AsSpan(Format.HeaderSize, (int)header.CipherLen).ToArray();
        var payload = Format.ReadPayload(Crypto.AesCtr(encKey, header.Nonce, cipherText));

        Assert.Equal((byte)1, Assert.Single(payload.Files).Strategy);
        var block = Assert.Single(payload.Blocks);
        Assert.Equal(0, block.RawLen);
        Assert.Equal(0, block.CodedLen);
        Assert.Equal(BlockCodec.FlagNoCompression, block.Flags);
        Assert.Empty(Assert.Single(payload.Data));
    }

    [Fact]
    public void Payload_RejectsNonZeroReservedField()
    {
        Assert.Equal(E.Payload, Assert.Throws<ArchiveException>(() =>
            Format.ReadPayload(Convert.FromHexString("000000010000"))).Code);
    }
}
