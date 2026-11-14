using System.Diagnostics;

namespace SmartArchiver;

public sealed record EvalResult(double Q, double S, double T, long BOrig, long BArc, double Seconds, bool Ok);

public static class Metric
{
    /// <summary>Q = α·S + β·T; Q = 0, якщо хоч один файл не відновлено побайтово.</summary>
    public static double Q(double alpha, double beta, double s, double t, bool ok) => ok ? alpha * s + beta * t : 0;

    public static EvalResult Evaluate(IReadOnlyList<InputFile> files, ArchiverOptions o, string password, TeamConfig cfg)
    {
        long bOrig = files.Sum(f => (long)f.Data.Length); long bArc = 0; bool ok = true;
        var sw = Stopwatch.StartNew();
        try
        {
            var vols = ArchiveService.Pack(files, password, o);
            bArc = vols.Sum(v => (long)v.Length);
            var back = ArchiveService.Unpack(vols, password).ToDictionary(x => x.Name, x => x.Data);
            foreach (var f in files)
                if (!back.TryGetValue(f.Name, out var d) || !d.AsSpan().SequenceEqual(f.Data)) { ok = false; break; }
        }
        catch (ArchiveException) { ok = false; }
        sw.Stop();
        double t = sw.Elapsed.TotalSeconds;
        double S = bOrig + bArc == 0 ? 0 : (double)bOrig / (bOrig + bArc);
        double T = cfg.TRef / (cfg.TRef + t);
        return new EvalResult(Q(cfg.Alpha, cfg.Beta, S, T, ok), S, T, bOrig, bArc, t, ok);
    }
}
