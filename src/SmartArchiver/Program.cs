using System.Text;
using SmartArchiver;

static class Program
{
    static int Main(string[] args)
    {
        try { return Run(args); }
        catch (ArchiveException e) { Console.Error.WriteLine(e.Message); return 2; }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or FormatException or System.Text.Json.JsonException)
        { Console.Error.WriteLine("I/O error: " + e.Message); return 3; }
    }

    static string? Opt(List<string> a, string name)
    {
        int i = a.IndexOf(name);
        if (i < 0) return null;
        if (i + 1 >= a.Count || a[i + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Missing value for {name}.");
        var v = a[i + 1]; a.RemoveRange(i, 2); return v;
    }

    static int Run(string[] argv)
    {
        var a = argv.ToList();
        if (a.Count == 0) { Usage(); return 1; }
        var cmd = a[0]; a.RemoveAt(0);
        var cfgPath = Opt(a, "--config") ?? "team_config.json";
        var cfg = File.Exists(cfgPath) ? TeamConfig.Load(cfgPath) : new TeamConfig();
        var modelPath = Opt(a, "--model");
        if (modelPath != null && !File.Exists(modelPath))
            throw new FileNotFoundException("The specified classifier model was not found.", modelPath);
        var model = modelPath != null ? Mlp.Load(modelPath) : null;
        if (model == null && (cmd is "pack" or "bench" or "ga"))
            Console.Error.WriteLine("WARNING: no --model supplied; every file will be assigned fallback class 0.");
        byte Classify(byte[] d) => model == null ? (byte)0 : (byte)model.Predict(Features.Compute(d));

        switch (cmd)
        {
            case "pack":   // pack <out_prefix> <inputs...>   (пароль: --password або з конфігу)
            {
                if (a.Count < 2) throw new ArgumentException("Usage: pack <out_prefix> <files|dirs...>");
                var pw = Opt(a, "--password") ?? cfg.Password;
                var prefix = a[0];
                var outParent = Path.GetDirectoryName(Path.GetFullPath(prefix));
                if (!string.IsNullOrEmpty(outParent)) Directory.CreateDirectory(outParent);
                var files = a.Skip(1).SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "*", SearchOption.TopDirectoryOnly) : new[] { p })
                    .Select(p => { var d = File.ReadAllBytes(p); return new InputFile(Path.GetFileName(p), d, Classify(d)); }).ToList();
                var vols = ArchiveService.Pack(files, pw, cfg.ToOptions());
                for (int i = 0; i < vols.Length; i++) File.WriteAllBytes($"{prefix}_{i:D3}.arc", vols[i]);
                Console.WriteLine($"{files.Count} file(s) -> {vols.Length} volume(s), {vols.Sum(v => v.Length)} bytes");
                return 0;
            }
            case "unpack": // unpack <out_dir> <vol...>
            {
                if (a.Count < 2) throw new ArgumentException("Usage: unpack <out_dir> <volume...>");
                var pw = Opt(a, "--password") ?? cfg.Password;
                var outDir = a[0]; Directory.CreateDirectory(outDir);
                var vols = a.Skip(1).Select(File.ReadAllBytes).ToList();
                var res = ArchiveService.Unpack(vols, pw);          // імена вже перевірені (E_PAYLOAD)
                foreach (var f in res) File.WriteAllBytes(Path.Combine(outDir, f.Name), f.Data);
                Console.WriteLine($"{res.Count} file(s) restored");
                return 0;
            }
            case "bench":  // bench <dir>  — таблиця розмірів/часу та Q
            {
                if (a.Count != 1) throw new ArgumentException("Usage: bench <corpus_dir>");
                var files = Directory.GetFiles(a[0], "*", SearchOption.AllDirectories).Where(p => !Path.GetFileName(p).Equals("labels.csv", StringComparison.OrdinalIgnoreCase))
                    .Select(p => { var d = File.ReadAllBytes(p); return new InputFile(Path.GetRelativePath(a[0], p).Replace('/', '_').Replace('\\', '_'), d, Classify(d)); }).ToList();
                foreach (var f in files)
                {
                    var r = Metric.Evaluate(new[] { f }, cfg.ToOptions(), cfg.Password, cfg);
                    Console.WriteLine($"{f.Name,-40} type={f.Type} raw={r.BOrig,9} arc={r.BArc,9} t={r.Seconds:F2}s ok={r.Ok}");
                }
                var all = Metric.Evaluate(files, cfg.ToOptions(), cfg.Password, cfg);
                Console.WriteLine($"TOTAL raw={all.BOrig} arc={all.BArc} ratio={(double)all.BOrig / Math.Max(1, all.BArc):F3} S={all.S:F4} T={all.T:F4} Q={all.Q:F4} ok={all.Ok}");
                return all.Ok ? 0 : 4;
            }
            case "train":  // train <data_dir_with_train_and_valid> <model_out>
            {
                if (a.Count != 2) throw new ArgumentException("Usage: train <data_dir_with_train_and_valid> <model_out.json>");
                var (tx, ty) = Classifier.LoadSet(Path.Combine(a[0], "train"));
                var (vx, vy) = Classifier.LoadSet(Path.Combine(a[0], "valid"));
                var (m, f1, all) = Classifier.TrainBest(tx, ty, vx, vy, cfg.Seed);
                var modelOut = Path.GetFullPath(a[1]);
                Directory.CreateDirectory(Path.GetDirectoryName(modelOut)!);
                m.Save(modelOut);
                Console.WriteLine($"train samples={tx.Count}; valid samples={vx.Count}");
                Console.WriteLine($"valid macro-F1 per init: {string.Join(", ", all.Select(x => x.ToString("F4")))}; best={f1:F4}; required F1_min={cfg.F1Min:F4}");
                if (f1 < cfg.F1Min)
                {
                    Console.Error.WriteLine("WARNING: validation macro-F1 is below F1_min. The model was saved for inspection, but the classifier acceptance threshold was not met.");
                    return 5;
                }
                return 0;
            }
            case "ga":     // ga <corpus_dir> [--evals 200]
            {
                if (a.Count < 1) throw new ArgumentException("Usage: ga <corpus_dir> [--evals 200]");
                int evals = int.Parse(Opt(a, "--evals") ?? "200");
                if (a.Count != 1 || evals < 1) throw new ArgumentException("GA requires one corpus directory and a positive --evals value.");
                var files = Directory.GetFiles(a[0], "*", SearchOption.AllDirectories).Where(p => !Path.GetFileName(p).Equals("labels.csv", StringComparison.OrdinalIgnoreCase))
                    .Select(p => { var d = File.ReadAllBytes(p); return new InputFile(Path.GetRelativePath(a[0], p).Replace('/', '_').Replace('\\', '_'), d, Classify(d)); }).ToList();
                Func<Chromosome, double> fit = c => Metric.Evaluate(files, cfg.ToOptions(c.ToParams(new CodecParams())), cfg.Password, cfg).Q;
                var baseQ = Metric.Evaluate(files, cfg.ToOptions(), cfg.Password, cfg).Q;
                var ga = Genetic.RunGa(fit, evals, 10, new Random(cfg.Seed));
                var rs = Genetic.RunRandom(fit, evals, new Random(cfg.Seed + 1));
                Console.WriteLine($"Q_baseline={baseQ:F4}\nGA     best={ga.BestQ:F4} [{ga.Best}] evals={ga.Evals}\nRandom best={rs.BestQ:F4} [{rs.Best}] evals={rs.Evals}");
                Console.WriteLine("GA best per generation: " + string.Join(" ", ga.BestPerGeneration.Select(x => x.ToString("F4"))));
                return 0;
            }
            default: Usage(); return 1;
        }
    }

    static void Usage() => Console.WriteLine(
@"smartarchiver <command> [--config team_config.json] [--model model.json]
  pack   <out_prefix> <files|dirs...> [--password P]
  unpack <out_dir> <volumes...>       [--password P]
  bench  <corpus_dir>
  train  <data_dir (train/ + valid/)> <model_out.json>
  ga     <corpus_dir> [--evals 200]");
}
