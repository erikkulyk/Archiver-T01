namespace SmartArchiver;

/// <summary>Бітовий запис, старший біт першим (MSB-first), підтверджено еталонним блоком викладача.</summary>
public sealed class BitWriter
{
    private readonly List<byte> _buf = new();
    private int _cur, _n;

    public void Write(uint value, int bits)
    {
        for (int i = bits - 1; i >= 0; i--)
        {
            _cur = (_cur << 1) | (int)((value >> i) & 1);
            if (++_n == 8) { _buf.Add((byte)_cur); _cur = 0; _n = 0; }
        }
    }

    public byte[] ToArray()
    {
        var r = new List<byte>(_buf);
        if (_n > 0) r.Add((byte)(_cur << (8 - _n)));
        return r.ToArray();
    }
}

public sealed class BitReader
{
    private readonly byte[] _d;
    private int _pos, _bit;
    public BitReader(byte[] data) { _d = data; }

    public int ReadBit()
    {
        if (_pos >= _d.Length) throw new ArchiveException(E.Payload, "unexpected end of bitstream");
        int b = (_d[_pos] >> (7 - _bit)) & 1;
        if (++_bit == 8) { _bit = 0; _pos++; }
        return b;
    }

    public uint ReadBits(int n)
    {
        uint v = 0;
        for (int i = 0; i < n; i++) v = (v << 1) | (uint)ReadBit();
        return v;
    }
}
