namespace SmartArchiver;

public sealed record Chromosome(int WindowExp, int MinMatch, double Threshold)
{
    public CodecParams ToParams(CodecParams b) => b with { Window = 1 << WindowExp, MinMatch = MinMatch, NoCompThreshold = Threshold };
    public override string ToString() => $"window=2^{WindowExp}, min_match={MinMatch}, thr={Threshold:F3}";
}

public sealed record SearchResult(Chromosome Best, double BestQ, List<double> BestPerGeneration, int Evals);

/// <summary>ГА з 3 генами (window, min_match, поріг NO_COMPRESSION) з елітизмом і порівнянням із random search.</summary>
public static class Genetic
{
    public static Chromosome RandomChromosome(Random r) => new(r.Next(8, 16), r.Next(3, 9), 0.85 + r.NextDouble() * 0.15);

    public static SearchResult RunRandom(Func<Chromosome, double> fit, int evals, Random rng)
    {
        Chromosome? best = null; double bq = double.NegativeInfinity; var hist = new List<double>();
        for (int i = 0; i < evals; i++)
        {
            var c = RandomChromosome(rng); var q = fit(c);
            if (q > bq) { bq = q; best = c; }
            hist.Add(bq);
        }
        return new SearchResult(best!, bq, hist, evals);
    }

    public static SearchResult RunGa(Func<Chromosome, double> fit, int evals, int pop, Random rng, double mutation = 0.25)
    {
        var popu = new List<(Chromosome C, double Q)>(); int used = 0;
        for (; used < Math.Min(pop, evals); used++) { var c = RandomChromosome(rng); popu.Add((c, fit(c))); }
        var best = popu.OrderByDescending(p => p.Q).First();
        var hist = new List<double> { best.Q };

        (Chromosome C, double Q) Tournament() =>
            Enumerable.Range(0, 3).Select(_ => popu[rng.Next(popu.Count)]).OrderByDescending(p => p.Q).First();

        while (used < evals)
        {
            var next = new List<(Chromosome C, double Q)> { best };          // елітизм: найкращий не втрачається
            while (next.Count < pop && used < evals)
            {
                var a = Tournament().C; var b = Tournament().C;
                var child = new Chromosome(
                    rng.Next(2) == 0 ? a.WindowExp : b.WindowExp,
                    rng.Next(2) == 0 ? a.MinMatch : b.MinMatch,
                    rng.Next(2) == 0 ? a.Threshold : b.Threshold);
                if (rng.NextDouble() < mutation) child = child with { WindowExp = Math.Clamp(child.WindowExp + rng.Next(-2, 3), 8, 15) };
                if (rng.NextDouble() < mutation) child = child with { MinMatch = Math.Clamp(child.MinMatch + rng.Next(-1, 2), 3, 8) };
                if (rng.NextDouble() < mutation) child = child with { Threshold = Math.Clamp(child.Threshold + (rng.NextDouble() - 0.5) * 0.06, 0.85, 1.0) };
                next.Add((child, fit(child))); used++;
            }
            popu = next;
            var gb = popu.OrderByDescending(p => p.Q).First();
            if (gb.Q > best.Q) best = gb;
            hist.Add(best.Q);
        }
        return new SearchResult(best.C, best.Q, hist, used);
    }
}
