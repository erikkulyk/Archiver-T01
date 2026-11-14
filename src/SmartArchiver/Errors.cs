namespace SmartArchiver;

/// <summary>Контрольована відмова з кодом (замість випадкових IndexOutOfRange тощо).</summary>
public sealed class ArchiveException : Exception
{
    public string Code { get; }
    public ArchiveException(string code, string message) : base($"{code}: {message}") { Code = code; }
}

public static class E
{
    public const string Magic = "E_MAGIC";
    public const string Version = "E_VERSION";   // ПЕРЕВІРИТИ за FORMAT_SPEC_P2
    public const string Length = "E_LENGTH";
    public const string Hmac = "E_HMAC";
    public const string Payload = "E_PAYLOAD";
    public const string Code = "E_CODE";
    public const string Crc = "E_CRC";
    public const string Volumes = "E_VOLUMES";
    public const string Capacity = "E_CAPACITY";
}
