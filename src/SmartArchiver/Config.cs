using System.Text.Json;

namespace SmartArchiver;

public sealed class TeamConfig
{
    public int Seed { get; init; } = 2001;
    public string Password { get; init; } = "algo2026";
    public double Alpha { get; init; } = 0.7;
    public double Beta { get; init; } = 0.3;
    public double TRef { get; init; } = 30.0;
    public int MaxVolumeBytes { get; init; } = 65536;
    public int BlockSize { get; init; } = 16384;
    public double F1Min { get; init; } = 0.61;
    public Dictionary<byte, int> Priority { get; init; } = new() { [0] = 3, [1] = 3, [2] = 2, [3] = 2, [4] = 2, [5] = 1, [6] = 1 };

    public static TeamConfig Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;
        var d = new TeamConfig();
        double G(JsonElement e, string k, double def) => e.TryGetProperty(k, out var v) ? v.GetDouble() : def;
        var q = r.TryGetProperty("метрика_Q", out var qe) ? qe : default;
        var pk = r.TryGetProperty("пакування", out var pe) ? pe : default;
        var prio = new Dictionary<byte, int>();
        if (r.TryGetProperty("пріоритети_типів", out var pt) && r.TryGetProperty("класифікатор", out var cl)
            && cl.TryGetProperty("класи", out var classes))
            foreach (var c in classes.EnumerateObject())
                if (pt.TryGetProperty(c.Value.GetString() ?? "", out var pv)) prio[byte.Parse(c.Name)] = pv.GetInt32();
        return new TeamConfig
        {
            Seed = r.TryGetProperty("seed", out var s) ? s.GetInt32() : d.Seed,
            Password = r.TryGetProperty("пароль_сеансу", out var pw) ? pw.GetString() ?? d.Password : d.Password,
            Alpha = q.ValueKind == JsonValueKind.Object ? G(q, "alpha", d.Alpha) : d.Alpha,
            Beta = q.ValueKind == JsonValueKind.Object ? G(q, "beta", d.Beta) : d.Beta,
            TRef = q.ValueKind == JsonValueKind.Object ? G(q, "t_ref_секунд", d.TRef) : d.TRef,
            MaxVolumeBytes = pk.ValueKind == JsonValueKind.Object ? (int)G(pk, "max_volume_bytes", d.MaxVolumeBytes) : d.MaxVolumeBytes,
            BlockSize = pk.ValueKind == JsonValueKind.Object ? (int)G(pk, "block_size", d.BlockSize) : d.BlockSize,
            F1Min = r.TryGetProperty("класифікатор", out var c2) ? G(c2, "F1_min", d.F1Min) : d.F1Min,
            Priority = prio.Count > 0 ? prio : d.Priority,
        };
    }

    public ArchiverOptions ToOptions(CodecParams? codec = null) => new()
    {
        Codec = codec ?? new CodecParams(), MaxVolume = MaxVolumeBytes, BlockSize = BlockSize, Priority = Priority,
    };
}
