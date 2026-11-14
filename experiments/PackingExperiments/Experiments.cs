using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SmartArchiver;

// Обов'язкові експерименти Проєкту 2 (методичка 10.71, 10.72):
//   pack       1) DP-рюкзак vs жадібне пакування першого тому;
//              2) compress -> encrypt vs encrypt -> compress;          (зона «Захист і пакування»)
//   codec      3) greedy vs lazy vs DP LZ77; 4) compression vs NO_COMPRESSION   (зона «Стиснення»)
//   classifier 5) навчання MLP, класифікатор vs фіксована стратегія          (зона «Аналіз і оптимізація»)
//   ga         6) Q_baseline, GA vs random search                          (зона «Аналіз і оптимізація»)
// Запуск з кореня репозиторію:
//   dotnet run --project experiments/PackingExperiments -c Release -- teacher_data [all|pack|codec|classifier|ga] [--evals 200]
// Результати пишуться в experiments/results/.
static partial class ExperimentsMain
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string[] TypeNames = { "природний текст", "вихідний код", "структуровані дані", "растр", "виконувані", "вже стиснуте", "штучні дані" };

    sealed record Item(string Name, byte[] Data, byte Type);
    sealed record Blk(int File, int Raw, int Weight, long Value);

    static int Main(string[] args)
    {
        var a = args.ToList();
        int evals = 200;
        int ei = a.IndexOf("--evals");
        if (ei >= 0) { evals = int.Parse(a[ei + 1]); a.RemoveRange(ei, 2); }
        var dataDir = a.Count > 0 ? a[0] : "teacher_data";
        var which = a.Count > 1 ? a[1].ToLowerInvariant() : "all";
        if (which is not ("all" or "pack" or "codec" or "classifier" or "ga"))
        {
            Console.Error.WriteLine("Другий аргумент: all | pack | codec | classifier | ga");
            return 1;
        }
        var outDir = Path.Combine("experiments", "results");
        var cfgPath = File.Exists("team_config.json") ? "team_config.json" : Path.Combine(dataDir, "team_config.json");
        if (!Directory.Exists(Path.Combine(dataDir, "corpus")))
        {
            Console.Error.WriteLine($"Не знайдено {Path.Combine(dataDir, "corpus")}. Розпакуй дані викладача так, щоб були {dataDir}/corpus, {dataDir}/train.");
            return 1;
        }
        var cfg = TeamConfig.Load(cfgPath);
        Directory.CreateDirectory(outDir);

        var stamp = $"seed={cfg.Seed}; max_volume_bytes={cfg.MaxVolumeBytes}; block_size={cfg.BlockSize}; commit={GitCommit()}; date={DateTime.Now:yyyy-MM-dd HH:mm}";
        Console.WriteLine("# " + stamp);

        var corpus = Load(Path.Combine(dataDir, "corpus"));
        var train = Directory.Exists(Path.Combine(dataDir, "train")) ? Load(Path.Combine(dataDir, "train")) : new List<Item>();

        var written = new List<string>();
        if (which is "all" or "pack")
        {
            var md = Header("Експерименти: пакування в томи і порядок операцій", stamp);
            Experiment1(cfg, corpus, train, outDir, md, stamp);
            Experiment2(cfg, corpus, outDir, md, stamp);
            written.Add(Save(outDir, "packing_and_order.md", md));
        }
        if (which is "all" or "codec")
        {
            var md = Header("Експерименти: розбір LZ77 і режим NO_COMPRESSION", stamp);
            Experiment3(cfg, corpus, outDir, md, stamp);
            Experiment4(cfg, corpus, outDir, md, stamp);
            written.Add(Save(outDir, "codec.md", md));
        }
        if (which is "all" or "classifier")
        {
            var md = Header("Експерименти: класифікатор типу файла", stamp);
            Experiment5(cfg, dataDir, corpus, outDir, md, stamp);
            written.Add(Save(outDir, "classifier.md", md));
        }
        if (which is "all" or "ga")
        {
            var md = Header("Експерименти: генетичний алгоритм", stamp);
            Experiment6(cfg, corpus, evals, outDir, md, stamp);
            written.Add(Save(outDir, "ga.md", md));
        }
        Console.WriteLine();
        Console.WriteLine("Готово. Таблиці: " + string.Join(", ", written) + $"; сирі дані (csv) теж у {outDir}");
        return 0;
    }

    // ------------------------------------------------------------------ Експеримент 1

    static void Experiment1(TeamConfig cfg, List<Item> corpus, List<Item> train, string outDir, StringBuilder md, string stamp)
    {
        Console.WriteLine();
        Console.WriteLine("=== Експеримент 1. DP-рюкзак vs жадібне пакування ===");
        var opts = cfg.ToOptions();

        // 1a: корпус, перший том
        var blocks = Encode(corpus, opts);
        var r = FirstVolume(corpus, blocks, cfg.MaxVolumeBytes);
        var dpOpts = Options(cfg, dp: true); var grOpts = Options(cfg, dp: false);
        var (dpVols, dpBytes, dpOk) = FullPack(corpus, dpOpts, cfg.Password);
        var (grVols, grBytes, grOk) = FullPack(corpus, grOpts, cfg.Password);

        Console.WriteLine($"Корпус: {corpus.Count} файлів, {blocks.Count} блоків, сумарна вага {blocks.Sum(b => (long)b.Weight)} Б, W першого тому = {r.W} Б");
        Console.WriteLine($"  DP:     цінність={r.DpValue}  вага={r.DpWeight}  блоків={r.DpCount}  час={r.DpMs:F3} мс  томів={dpVols}  архів={dpBytes} Б  відновлено={dpOk}");
        Console.WriteLine($"  Greedy: цінність={r.GrValue}  вага={r.GrWeight}  блоків={r.GrCount}  час={r.GrMs:F3} мс  томів={grVols}  архів={grBytes} Б  відновлено={grOk}");

        md.AppendLine("## Експеримент 1. DP-рюкзак проти жадібного пакування першого тому").AppendLine();
        md.AppendLine("**Питання.** Чи дає точний DP-рюкзак більшу сумарну цінність блоків у першому томі, ніж жадібний алгоритм за спаданням value/weight, і якою ціною за часом?").AppendLine();
        md.AppendLine($"Вага блока = coded_len + 15, цінність = пріоритет типу × raw_len, W = max_volume_bytes − 41 − 32 − 6 − таблиця файлів.").AppendLine();
        md.AppendLine($"### 1a. Робочий корпус ({corpus.Count} файлів, {blocks.Count} блоків, W = {r.W} Б)").AppendLine();
        md.AppendLine("| Алгоритм | Цінність 1-го тому | Зайнято W, Б | Блоків у 1-му томі | Час рюкзака, мс | Томів | Розмір архіву, Б | Відновлено |");
        md.AppendLine("|---|---|---|---|---|---|---|---|");
        md.AppendLine($"| DP | {r.DpValue} | {r.DpWeight} | {r.DpCount} | {r.DpMs.ToString("F3", Inv)} | {dpVols} | {dpBytes} | {(dpOk ? "так" : "НІ")} |");
        md.AppendLine($"| Greedy | {r.GrValue} | {r.GrWeight} | {r.GrCount} | {r.GrMs.ToString("F3", Inv)} | {grVols} | {grBytes} | {(grOk ? "так" : "НІ")} |");
        md.AppendLine();

        var csv1 = new StringBuilder("# " + stamp + "\nscenario,files,blocks,W,dp_value,greedy_value,dp_weight,greedy_weight,dp_ms,greedy_ms\n");
        csv1.AppendLine(string.Join(",", "corpus", corpus.Count, blocks.Count, r.W, r.DpValue, r.GrValue, r.DpWeight, r.GrWeight, r.DpMs.ToString("F4", Inv), r.GrMs.ToString("F4", Inv)));

        // 1b: випадкові набори з train/, щоб побачити, як часто DP виграє
        if (train.Count > 0)
        {
            var trainBlocks = Encode(train, opts);
            var byFile = trainBlocks.GroupBy(b => b.File).ToDictionary(g => g.Key, g => g.ToList());
            var rng = new Random(cfg.Seed);
            int runs = 100, wins = 0, ties = 0, losses = 0;
            double sumGain = 0, maxGain = 0, dpMs = 0, grMs = 0;
            for (int run = 0; run < runs; run++)
            {
                int k = rng.Next(8, 31);
                var pick = Enumerable.Range(0, train.Count).OrderBy(_ => rng.Next()).Take(k).ToList();
                var items = pick.Select(i => train[i]).ToList();
                var bl = new List<Blk>();
                for (int j = 0; j < pick.Count; j++) bl.AddRange(byFile[pick[j]].Select(b => b with { File = j }));
                var rr = FirstVolume(items, bl, cfg.MaxVolumeBytes);
                if (rr.DpValue < rr.GrValue) throw new Exception("DP гірший за greedy — помилка в рюкзаку");
                if (rr.DpValue > rr.GrValue) wins++; else ties++;
                double gain = rr.GrValue == 0 ? 0 : 100.0 * (rr.DpValue - rr.GrValue) / rr.GrValue;
                sumGain += gain; maxGain = Math.Max(maxGain, gain); dpMs += rr.DpMs; grMs += rr.GrMs;
                csv1.AppendLine(string.Join(",", $"train_subset_{run}", k, bl.Count, rr.W, rr.DpValue, rr.GrValue, rr.DpWeight, rr.GrWeight, rr.DpMs.ToString("F4", Inv), rr.GrMs.ToString("F4", Inv)));
            }
            Console.WriteLine($"Випадкові набори з train/ ({runs} запусків по 8–30 файлів): DP краще у {wins}, однаково у {ties}, гірше у {losses}; середній виграш {sumGain / runs:F2}%, максимальний {maxGain:F2}%; середній час DP {dpMs / runs:F3} мс, greedy {grMs / runs:F3} мс");

            md.AppendLine($"### 1b. {runs} випадкових наборів по 8–30 файлів із train/ (seed {cfg.Seed})").AppendLine();
            md.AppendLine("| Показник | Значення |").AppendLine("|---|---|");
            md.AppendLine($"| DP строго краще за greedy | {wins} з {runs} |");
            md.AppendLine($"| Однакова цінність | {ties} з {runs} |");
            md.AppendLine($"| DP гірше за greedy | {losses} з {runs} (має бути 0) |");
            md.AppendLine($"| Середній виграш DP за цінністю | {(sumGain / runs).ToString("F2", Inv)} % |");
            md.AppendLine($"| Максимальний виграш DP | {maxGain.ToString("F2", Inv)} % |");
            md.AppendLine($"| Середній час DP / greedy, мс | {(dpMs / runs).ToString("F3", Inv)} / {(grMs / runs).ToString("F3", Inv)} |");
            md.AppendLine();
        }
        md.AppendLine("**Висновок.** _(дописати самостійно: чи виграє DP, наскільки, чому жадібний алгоритм іноді програє, чи виправдана різниця в часі; складність DP O(n·W), greedy O(n log n))._").AppendLine();
        File.WriteAllText(Path.Combine(outDir, "exp1_dp_vs_greedy.csv"), csv1.ToString(), new UTF8Encoding(true));
    }

    sealed record FirstVolResult(long W, long DpValue, long GrValue, long DpWeight, long GrWeight, int DpCount, int GrCount, double DpMs, double GrMs);

    static FirstVolResult FirstVolume(List<Item> files, List<Blk> blocks, int maxVolume)
    {
        // Як у ArchiveService.PackVolumes: таблиця файлів усіх кандидатів резервується ДО рюкзака.
        long table = blocks.Select(b => b.File).Distinct().Sum(fi => Format.FileEntrySize(files[fi].Name));
        long W = maxVolume - Format.HeaderSize - Format.MacSize - Format.PayloadFixed - table;
        var w = blocks.Select(b => b.Weight).ToArray();
        var v = blocks.Select(b => b.Value).ToArray();

        Knapsack.Dp(w, v, W); Knapsack.Greedy(w, v, W);          // прогрів (методичка 6.32)
        var sw = Stopwatch.StartNew(); int[] dp = Array.Empty<int>();
        const int reps = 5;
        for (int i = 0; i < reps; i++) dp = Knapsack.Dp(w, v, W);
        double dpMs = sw.Elapsed.TotalMilliseconds / reps;
        sw.Restart(); int[] gr = Array.Empty<int>();
        for (int i = 0; i < reps; i++) gr = Knapsack.Greedy(w, v, W);
        double grMs = sw.Elapsed.TotalMilliseconds / reps;

        return new FirstVolResult(W, Knapsack.Value(dp, v), Knapsack.Value(gr, v),
            dp.Sum(i => (long)w[i]), gr.Sum(i => (long)w[i]), dp.Length, gr.Length, dpMs, grMs);
    }

    static List<Blk> Encode(List<Item> files, ArchiverOptions o)
    {
        var res = new List<Blk>();
        for (int fi = 0; fi < files.Count; fi++)
        {
            var f = files[fi]; var cp = o.ForType(f.Type);
            int prio = o.Priority.TryGetValue(f.Type, out var p) ? p : 1;
            if (f.Data.Length == 0) { res.Add(new Blk(fi, 0, Format.BlockRecSize, 1)); continue; }
            for (int off = 0; off < f.Data.Length; off += o.BlockSize)
            {
                int l = Math.Min(o.BlockSize, f.Data.Length - off);
                var (_, data) = BlockCodec.Encode(f.Data.AsSpan(off, l).ToArray(), cp);
                res.Add(new Blk(fi, l, data.Length + Format.BlockRecSize, (long)prio * l));
            }
        }
        return res;
    }

    static ArchiverOptions Options(TeamConfig cfg, bool dp) => new()
    {
        Codec = new CodecParams(), MaxVolume = cfg.MaxVolumeBytes, BlockSize = cfg.BlockSize, Priority = cfg.Priority, UseDpPacking = dp,
    };

    static (int Volumes, long Bytes, bool Ok) FullPack(List<Item> files, ArchiverOptions o, string pw)
    {
        var input = files.Select(f => new InputFile(f.Name, f.Data, f.Type)).ToList();
        var vols = ArchiveService.Pack(input, pw, o);
        var back = ArchiveService.Unpack(vols.Reverse().ToList(), pw).ToDictionary(x => x.Name, x => x.Data);
        bool ok = files.All(f => back.TryGetValue(f.Name, out var d) && d.AsSpan().SequenceEqual(f.Data));
        return (vols.Length, vols.Sum(v => (long)v.Length), ok);
    }

    // ------------------------------------------------------------------ Експеримент 2

    static void Experiment2(TeamConfig cfg, List<Item> corpus, string outDir, StringBuilder md, string stamp)
    {
        Console.WriteLine();
        Console.WriteLine("=== Експеримент 2. compress -> encrypt vs encrypt -> compress ===");
        var key = new byte[32]; var nonce = new byte[12];
        new Random(cfg.Seed).NextBytes(key); new Random(cfg.Seed + 1).NextBytes(nonce);   // фіксовані значення лише для відтворюваного експерименту
        var opts = cfg.ToOptions();

        var csv = new StringBuilder("# " + stamp + "\nfile,type,raw,compress_then_encrypt,encrypt_then_compress,entropy_raw,entropy_encrypted\n");
        var groups = new Dictionary<int, long[]>();
        foreach (var f in corpus)
        {
            var cp = opts.ForType(f.Type);
            long ce = CompressedSize(f.Data, cp, opts.BlockSize);                     // AES-CTR не змінює довжину
            var enc = Crypto.AesCtr(key, nonce, f.Data);
            long ec = CompressedSize(enc, opts.ForType(f.Type) with { ForceRaw = false }, opts.BlockSize);
            csv.AppendLine(string.Join(",", f.Name, f.Type, f.Data.Length, ce, ec, Entropy(f.Data).ToString("F3", Inv), Entropy(enc).ToString("F3", Inv)));
            var g = groups.TryGetValue(f.Type, out var x) ? x : groups[f.Type] = new long[3];
            g[0] += f.Data.Length; g[1] += ce; g[2] += ec;
        }

        md.AppendLine("## Експеримент 2. Порядок операцій: стиснення до шифрування чи після").AppendLine();
        md.AppendLine("**Питання.** Чому методичка вимагає compress → encrypt? Що буде, якщо спочатку зашифрувати AES-256-CTR, а потім стискати?").AppendLine();
        md.AppendLine("Розміри без заголовків і HMAC (вони однакові для обох варіантів). CTR не змінює довжину, тому розмір compress → encrypt дорівнює розміру стиснених даних.").AppendLine();
        md.AppendLine("| Група | Сирий, Б | compress → encrypt, Б | Коеф. | encrypt → compress, Б | Коеф. |");
        md.AppendLine("|---|---|---|---|---|---|");
        long tr = 0, tce = 0, tec = 0;
        foreach (var (t, g) in groups.OrderBy(k => k.Key))
        {
            md.AppendLine($"| {TypeNames[t]} | {g[0]} | {g[1]} | {((double)g[0] / g[1]).ToString("F2", Inv)} | {g[2]} | {((double)g[0] / g[2]).ToString("F2", Inv)} |");
            Console.WriteLine($"  {TypeNames[t],-20} raw={g[0],7}  compress->encrypt={g[1],7} ({(double)g[0] / g[1]:F2}x)  encrypt->compress={g[2],7} ({(double)g[0] / g[2]:F2}x)");
            tr += g[0]; tce += g[1]; tec += g[2];
        }
        md.AppendLine($"| **Разом** | {tr} | {tce} | {((double)tr / tce).ToString("F2", Inv)} | {tec} | {((double)tr / tec).ToString("F2", Inv)} |").AppendLine();
        Console.WriteLine($"  {"РАЗОМ",-20} raw={tr,7}  compress->encrypt={tce,7} ({(double)tr / tce:F2}x)  encrypt->compress={tec,7} ({(double)tr / tec:F2}x)");
        md.AppendLine("Ентропія по файлах (біт/байт, до і після шифрування) — у exp2_order.csv.").AppendLine();
        md.AppendLine("**Висновок.** _(дописати самостійно: чому шифротекст не стискається — він статистично схожий на випадковий шум, ентропія ≈ 8 біт/байт, LZ77 не знаходить повторів, тому блоки йдуть у NO_COMPRESSION)._").AppendLine();
        File.WriteAllText(Path.Combine(outDir, "exp2_order.csv"), csv.ToString(), new UTF8Encoding(true));
    }

    static long CompressedSize(byte[] data, CodecParams cp, int blockSize)
    {
        long s = 0;
        for (int off = 0; off < data.Length; off += blockSize)
        {
            int l = Math.Min(blockSize, data.Length - off);
            s += BlockCodec.Encode(data.AsSpan(off, l).ToArray(), cp).Data.Length;
        }
        return s;
    }

    static double Entropy(byte[] d)
    {
        if (d.Length == 0) return 0;
        var c = new long[256]; foreach (var b in d) c[b]++;
        double h = 0; foreach (var x in c) if (x > 0) { double p = (double)x / d.Length; h -= p * Math.Log2(p); }
        return h;
    }

    // ------------------------------------------------------------------ допоміжне

    static List<Item> Load(string dir)
    {
        var res = new List<Item>();
        foreach (var line in File.ReadLines(Path.Combine(dir, "labels.csv")).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var p = line.Split(',');
            res.Add(new Item(p[0], File.ReadAllBytes(Path.Combine(dir, p[0])), byte.Parse(p[1])));
        }
        return res;
    }

    static string GitCommit()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --short HEAD") { RedirectStandardOutput = true, RedirectStandardError = true };
            using var pr = Process.Start(psi)!;
            var s = pr.StandardOutput.ReadToEnd().Trim(); pr.WaitForExit();
            return pr.ExitCode == 0 && s.Length > 0 ? s : "немає-git";
        }
        catch { return "немає-git"; }
    }
}
