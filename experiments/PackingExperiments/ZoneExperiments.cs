using System.Diagnostics;
using System.Text;
using SmartArchiver;

static partial class ExperimentsMain
{
    static StringBuilder Header(string title, string stamp)
    {
        var md = new StringBuilder();
        md.AppendLine("# " + title).AppendLine().AppendLine("`" + stamp + "`").AppendLine();
        md.AppendLine("Дані: робочий корпус викладача (21 файл, по 3 на кожну з 7 груп). Якщо не сказано інше, клас файла береться з labels.csv (справжня мітка), щоб ізолювати досліджуваний алгоритм від помилок класифікатора.").AppendLine();
        return md;
    }

    static string Save(string outDir, string name, StringBuilder md)
    {
        var path = Path.Combine(outDir, name);
        File.WriteAllText(path, md.ToString(), new UTF8Encoding(true));
        return path;
    }

    static string F(double v, string fmt = "F2") => v.ToString(fmt, Inv);

    // ------------------------------------------------------------------ Експеримент 3: greedy vs lazy vs DP

    static void Experiment3(TeamConfig cfg, List<Item> corpus, string outDir, StringBuilder md, string stamp)
    {
        Console.WriteLine();
        Console.WriteLine("=== Експеримент 3. LZ77: greedy vs lazy vs DP ===");
        var modes = new[] { ParseMode.Greedy, ParseMode.Lazy, ParseMode.Dp };
        var csv = new StringBuilder("# " + stamp + "\nfile,type,mode,raw,tokens,literals,matches,cost_bits,coded_bytes,ms\n");
        // [група][режим] = (raw, literals, matches, costBits, coded, ms)
        var agg = new Dictionary<(int, ParseMode), double[]>();

        foreach (var f in corpus)
            foreach (var m in modes)
            {
                var cp = new CodecParams(Mode: m);
                // прогрів
                foreach (var blk in Blocks(f.Data, cfg.BlockSize).Take(1)) Lz77.Parse(blk, m, cp.Window, cp.MinMatch, cp.MaxChain);
                long lits = 0, matches = 0, cost = 0, coded = 0;
                var sw = Stopwatch.StartNew();
                foreach (var blk in Blocks(f.Data, cfg.BlockSize))
                {
                    var toks = Lz77.Parse(blk, m, cp.Window, cp.MinMatch, cp.MaxChain);
                    lits += toks.Count(t => !t.IsMatch); matches += toks.Count(t => t.IsMatch); cost += Lz77.Cost(toks);
                    coded += ForcedSize(toks);
                }
                double ms = sw.Elapsed.TotalMilliseconds;
                csv.AppendLine(string.Join(",", f.Name, f.Type, m, f.Data.Length, lits + matches, lits, matches, cost, coded, F(ms, "F3")));
                var key = ((int)f.Type, m);
                var x = agg.TryGetValue(key, out var v) ? v : agg[key] = new double[6];
                x[0] += f.Data.Length; x[1] += lits; x[2] += matches; x[3] += cost; x[4] += coded; x[5] += ms;
            }

        md.AppendLine("## Експеримент 3. Розбір LZ77: жадібний, лінивий і оптимальний (ДП)").AppendLine();
        md.AppendLine("**Питання.** Чи дає lazy greedy менший потік, ніж greedy, і наскільки оптимальний ДП-розбір кращий за обидва та повільніший?").AppendLine();
        md.AppendLine("Вартість потоку — за фіксованою моделлю ДП (літерал 9 біт, посилання 17 + max(0, bit_length(d−1) − 1) біт). «Після Гаффмана» — реальний розмір закодованих блоків без перевірки на NO_COMPRESSION. Вікно 32768, MIN_MATCH 3, блоки по 16 КБ.").AppendLine();
        md.AppendLine("### Разом по корпусу").AppendLine();
        md.AppendLine("| Режим | Літералів | Посилань | Вартість потоку, біт | Після Гаффмана, Б | Час розбору, мс |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var m in modes)
        {
            var t = new double[6];
            foreach (var (k, v) in agg) if (k.Item2 == m) for (int i = 0; i < 6; i++) t[i] += v[i];
            md.AppendLine($"| {m} | {t[1]:F0} | {t[2]:F0} | {t[3]:F0} | {t[4]:F0} | {F(t[5], "F1")} |");
            Console.WriteLine($"  {m,-7} literals={t[1],8:F0} matches={t[2],7:F0} cost_bits={t[3],9:F0} coded={t[4],8:F0} B  time={t[5],8:F1} ms");
        }
        md.AppendLine().AppendLine("### Після Гаффмана по групах, Б").AppendLine();
        md.AppendLine("| Група | Сирий | Greedy | Lazy | DP | Lazy vs Greedy | DP vs Greedy |");
        md.AppendLine("|---|---|---|---|---|---|---|");
        for (int g = 0; g < 7; g++)
        {
            if (!agg.ContainsKey((g, ParseMode.Greedy))) continue;
            double gr = agg[(g, ParseMode.Greedy)][4], lz = agg[(g, ParseMode.Lazy)][4], dp = agg[(g, ParseMode.Dp)][4];
            md.AppendLine($"| {TypeNames[g]} | {agg[(g, ParseMode.Greedy)][0]:F0} | {gr:F0} | {lz:F0} | {dp:F0} | {F(100 * (lz - gr) / gr)} % | {F(100 * (dp - gr) / gr)} % |");
        }
        md.AppendLine().AppendLine("Від'ємний відсоток означає, що варіант дав менший розмір, ніж greedy.").AppendLine();
        md.AppendLine("**Висновок.** _(дописати: чи завжди lazy кращий за greedy; де різниця найбільша і чому; чому ДП оптимальний за фіксованою моделлю вартостей, але після Гаффмана може бути не найменшим — реальні довжини кодів відрізняються від фіксованих 9/17 біт; чи виправданий час ДП)._").AppendLine();
        File.WriteAllText(Path.Combine(outDir, "exp3_lz77_modes.csv"), csv.ToString(), new UTF8Encoding(true));
    }

    // ------------------------------------------------------------------ Експеримент 4: compression vs NO_COMPRESSION

    static void Experiment4(TeamConfig cfg, List<Item> corpus, string outDir, StringBuilder md, string stamp)
    {
        Console.WriteLine();
        Console.WriteLine("=== Експеримент 4. Стиснення vs NO_COMPRESSION ===");
        var opts = cfg.ToOptions();
        var csv = new StringBuilder("# " + stamp + "\nfile,type,block,raw,forced_coded,chosen,stored_raw\n");
        var g = new Dictionary<int, long[]>();   // raw, forced, chosen, blocks, rawBlocks
        foreach (var f in corpus)
        {
            var cp = new CodecParams();   // однакова стратегія для всіх, без підказки класифікатора
            int bi = 0;
            foreach (var blk in Blocks(f.Data, cfg.BlockSize))
            {
                long forced = ForcedSize(Lz77.Parse(blk, cp.Mode, cp.Window, cp.MinMatch, cp.MaxChain));
                var (fl, data) = BlockCodec.Encode(blk, cp);
                bool raw = fl == BlockCodec.FlagNoCompression;
                csv.AppendLine(string.Join(",", f.Name, f.Type, bi++, blk.Length, forced, data.Length, raw ? 1 : 0));
                var x = g.TryGetValue(f.Type, out var v) ? v : g[f.Type] = new long[5];
                x[0] += blk.Length; x[1] += forced; x[2] += data.Length; x[3]++; if (raw) x[4]++;
            }
        }
        md.AppendLine("## Експеримент 4. Стиснення проти NO_COMPRESSION").AppendLine();
        md.AppendLine("**Питання.** Для яких даних стиснення програє сирому зберіганню, і скільки економить правило «якщо coded_len ≥ raw_len — зберегти без стиснення»?").AppendLine();
        md.AppendLine("«Завжди стискати» — розмір, якби кожен блок записувався стиснутим навіть коли він більший за сирий. «З NO_COMPRESSION» — реальна поведінка архіватора.").AppendLine();
        md.AppendLine("| Група | Сирий, Б | Завжди стискати, Б | З NO_COMPRESSION, Б | Блоків у NO_COMPRESSION |");
        md.AppendLine("|---|---|---|---|---|");
        long tr = 0, tf = 0, tc = 0;
        foreach (var (t, x) in g.OrderBy(k => k.Key))
        {
            md.AppendLine($"| {TypeNames[t]} | {x[0]} | {x[1]} | {x[2]} | {x[4]} з {x[3]} |");
            Console.WriteLine($"  {TypeNames[t],-20} raw={x[0],7} always_compress={x[1],7} with_no_compression={x[2],7} raw_blocks={x[4]}/{x[3]}");
            tr += x[0]; tf += x[1]; tc += x[2];
        }
        md.AppendLine($"| **Разом** | {tr} | {tf} | {tc} | |").AppendLine();
        md.AppendLine("**Висновок.** _(дописати: які групи йдуть у NO_COMPRESSION і чому — вже стиснуті дані й шум не мають надлишковості, Гаффман дає ~8 біт на байт плюс таблиці; чому рішення приймається за фактичним розміром, а не за ентропією — методичка 10.28)._").AppendLine();
        File.WriteAllText(Path.Combine(outDir, "exp4_no_compression.csv"), csv.ToString(), new UTF8Encoding(true));
    }

    // ------------------------------------------------------------------ Експеримент 5: класифікатор

    static void Experiment5(TeamConfig cfg, string dataDir, List<Item> corpus, string outDir, StringBuilder md, string stamp)
    {
        Console.WriteLine();
        Console.WriteLine("=== Експеримент 5. Класифікатор: навчання і вплив на стратегію ===");
        var (tx, ty) = Classifier.LoadSet(Path.Combine(dataDir, "train"));
        var (vx, vy) = Classifier.LoadSet(Path.Combine(dataDir, "valid"));
        var sw = Stopwatch.StartNew();
        var (model, f1, all) = Classifier.TrainBest(tx, ty, vx, vy, cfg.Seed);
        double trainSec = sw.Elapsed.TotalSeconds;
        var vPred = vx.Select(model.Predict).ToList();
        Console.WriteLine($"  train={tx.Count} valid={vx.Count}; valid macro-F1 по ініціалізаціях: {string.Join(", ", all.Select(x => F(x, "F4")))}; найкраща {F(f1, "F4")}; F1_min={F(cfg.F1Min)}; час {F(trainSec, "F1")} с");

        // матриця помилок на valid
        var conf = new int[7, 7];
        for (int i = 0; i < vy.Count; i++) conf[vy[i], vPred[i]]++;

        var predicted = corpus.Select(f => (byte)model.Predict(Features.Compute(f.Data))).ToList();
        int correct = corpus.Where((f, i) => predicted[i] == f.Type).Count();

        var opts = cfg.ToOptions();
        var fixedFiles = corpus.Select(f => new InputFile(f.Name, f.Data, 0)).ToList();                       // фіксована стратегія
        var clsFiles = corpus.Select((f, i) => new InputFile(f.Name, f.Data, predicted[i])).ToList();          // за класифікатором
        var trueFiles = corpus.Select(f => new InputFile(f.Name, f.Data, f.Type)).ToList();                    // ідеальний класифікатор
        Metric.Evaluate(fixedFiles, opts, cfg.Password, cfg);                                                   // прогрів
        var rows = new[] { ("Фіксована стратегія (усі файли як клас 0)", fixedFiles), ("Стратегія за класифікатором", clsFiles), ("Стратегія за справжніми мітками", trueFiles) }
            .Select(r => (r.Item1, Median(Enumerable.Range(0, 3).Select(_ => Metric.Evaluate(r.Item2, opts, cfg.Password, cfg)).ToList()))).ToList();

        md.AppendLine("## Експеримент 5. Класифікатор типу файла").AppendLine();
        md.AppendLine("**Питання.** Наскільки точно перцептрон визначає тип файла і чи впливає це на результат архівування?").AppendLine();
        md.AppendLine("### Навчання (train → вибір моделі на valid)").AppendLine();
        md.AppendLine("| Показник | Значення |").AppendLine("|---|---|");
        md.AppendLine($"| Файлів train / valid | {tx.Count} / {vx.Count} |");
        md.AppendLine($"| Ознак | {Features.Count} |");
        md.AppendLine($"| macro-F1 на valid по ініціалізаціях | {string.Join(", ", all.Select(x => F(x, "F4")))} |");
        md.AppendLine($"| Обрана модель, macro-F1 на valid | {F(f1, "F4")} |");
        md.AppendLine($"| Поріг F1_min | {F(cfg.F1Min)} |");
        md.AppendLine("| Базова лінія: мажоритарний клас / випадковий вибір | 0.0357 / 0.1614 |");
        md.AppendLine($"| Правильно на робочому корпусі | {correct} з {corpus.Count} |");
        md.AppendLine($"| Час навчання | {F(trainSec, "F1")} с |").AppendLine();
        md.AppendLine("### Матриця помилок на valid (рядок — справжній клас, стовпець — передбачений)").AppendLine();
        md.AppendLine("| | " + string.Join(" | ", Enumerable.Range(0, 7)) + " |");
        md.AppendLine("|---" + string.Concat(Enumerable.Repeat("|---", 7)) + "|");
        for (int r = 0; r < 7; r++) md.AppendLine($"| {r} {TypeNames[r]} | " + string.Join(" | ", Enumerable.Range(0, 7).Select(c => conf[r, c].ToString())) + " |");
        md.AppendLine().AppendLine("### Вплив на архівування (медіана 3 запусків)").AppendLine();
        md.AppendLine("Клас впливає на стратегію так: «вже стиснуте» одразу зберігається без стиснення, «растр» стискається з ДП-розбором, решта — звичайний lazy LZ77 + Гаффман.").AppendLine();
        md.AppendLine("| Стратегія | Архів, Б | Час, с | S | T | Q |").AppendLine("|---|---|---|---|---|---|");
        foreach (var (name, e) in rows)
        {
            md.AppendLine($"| {name} | {e.BArc} | {F(e.Seconds, "F3")} | {F(e.S, "F4")} | {F(e.T, "F4")} | {F(e.Q, "F4")} |");
            Console.WriteLine($"  {name,-45} arc={e.BArc,7} t={e.Seconds:F3}s Q={e.Q:F4} ok={e.Ok}");
        }
        md.AppendLine().AppendLine("**Висновок.** _(дописати: чи досягнуто F1_min; які класи плутаються і чому; наскільки змінюються розмір і час від класифікатора — якщо виграш малий, пояснити, чому: NO_COMPRESSION і так спрацьовує за фактичним розміром, тому класифікатор економить переважно час на марному стисненні)._").AppendLine();

        var csv = new StringBuilder("# " + stamp + "\nfile,true_type,predicted_type\n");
        for (int i = 0; i < corpus.Count; i++) csv.AppendLine($"{corpus[i].Name},{corpus[i].Type},{predicted[i]}");
        File.WriteAllText(Path.Combine(outDir, "exp5_classifier_corpus.csv"), csv.ToString(), new UTF8Encoding(true));
    }

    static EvalResult Median(List<EvalResult> xs) => xs.OrderBy(x => x.Seconds).ElementAt(xs.Count / 2);

    // ------------------------------------------------------------------ Експеримент 6: GA vs random search

    static void Experiment6(TeamConfig cfg, List<Item> corpus, int evals, string outDir, StringBuilder md, string stamp)
    {
        Console.WriteLine();
        Console.WriteLine($"=== Експеримент 6. GA vs random search ({evals} оцінок кожен; це найдовший експеримент) ===");
        var files = corpus.Select(f => new InputFile(f.Name, f.Data, f.Type)).ToList();
        int done = 0; var total = 2 * evals; var sw = Stopwatch.StartNew();
        Func<Chromosome, double> fit = c =>
        {
            var q = Metric.Evaluate(files, cfg.ToOptions(c.ToParams(new CodecParams())), cfg.Password, cfg).Q;
            if (++done % 20 == 0) Console.WriteLine($"  ... {done}/{total} оцінок, {sw.Elapsed.TotalSeconds:F0} с");
            return q;
        };
        var baseline = Metric.Evaluate(files, cfg.ToOptions(), cfg.Password, cfg);
        var ga = Genetic.RunGa(fit, evals, 10, new Random(cfg.Seed));
        var rs = Genetic.RunRandom(fit, evals, new Random(cfg.Seed + 1));
        bool elitismOk = ga.BestPerGeneration.Zip(ga.BestPerGeneration.Skip(1)).All(p => p.Second >= p.First);
        var def = new CodecParams();

        Console.WriteLine($"  Q_baseline={F(baseline.Q, "F4")} (window={def.Window}, min_match={def.MinMatch}, thr={F(def.NoCompThreshold, "F3")})");
        Console.WriteLine($"  GA     best Q={F(ga.BestQ, "F4")} [{ga.Best}] оцінок={ga.Evals}");
        Console.WriteLine($"  Random best Q={F(rs.BestQ, "F4")} [{rs.Best}] оцінок={rs.Evals}");
        Console.WriteLine($"  Елітизм (найкращий не погіршується між поколіннями): {(elitismOk ? "так" : "НІ")}");

        md.AppendLine("## Експеримент 6. Генетичний алгоритм проти випадкового пошуку").AppendLine();
        md.AppendLine("**Питання.** Чи знаходить ГА кращу конфігурацію, ніж baseline і випадковий пошук за однакової кількості оцінок fitness?").AppendLine();
        md.AppendLine($"Хромосома: window = 2^(8…15), min_match 3…8, поріг NO_COMPRESSION 0.85…1.0. Fitness = Q на корпусі (Q = 0, якщо хоч один файл не відновлено). Популяція 10, турнір із 3, елітизм. Seed GA = {cfg.Seed}, seed random = {cfg.Seed + 1}.").AppendLine();
        md.AppendLine("| Метод | Найкраще Q | Параметри | Оцінок fitness |").AppendLine("|---|---|---|---|");
        md.AppendLine($"| Baseline (фіксовані) | {F(baseline.Q, "F4")} | window=2^15, min_match={def.MinMatch}, thr={F(def.NoCompThreshold, "F3")} | 1 |");
        md.AppendLine($"| Генетичний алгоритм | {F(ga.BestQ, "F4")} | {ga.Best} | {ga.Evals} |");
        md.AppendLine($"| Випадковий пошук | {F(rs.BestQ, "F4")} | {rs.Best} | {rs.Evals} |").AppendLine();
        md.AppendLine($"Перевірка елітизму (найкраще Q покоління не спадає): **{(elitismOk ? "виконується" : "ПОРУШЕНО")}**; покоління 0: {F(ga.BestPerGeneration[0], "F4")}, останнє: {F(ga.BestPerGeneration[^1], "F4")}.").AppendLine();
        md.AppendLine("Криві збіжності (найкраще Q після кожного покоління / кожної оцінки) — у exp6_ga_curve.csv, з них можна побудувати графік.").AppendLine();
        md.AppendLine("Увага: Q містить складову часу T, тому повторний запуск може дати трохи інші числа. Головний внесок дає S (α = 0.7).").AppendLine();
        md.AppendLine("**Висновок.** _(дописати: чи покращив ГА baseline і наскільки; чи кращий він за random search; якщо різниця мала — чому: простір лише з трьох генів невеликий, і випадковий пошук теж швидко знаходить добрі точки; що саме оптимізує ГА)._").AppendLine();

        var csv = new StringBuilder("# " + stamp + "\nmethod,step,best_q\n");
        for (int i = 0; i < ga.BestPerGeneration.Count; i++) csv.AppendLine($"ga_generation,{i},{F(ga.BestPerGeneration[i], "F5")}");
        for (int i = 0; i < rs.BestPerGeneration.Count; i++) csv.AppendLine($"random_eval,{i + 1},{F(rs.BestPerGeneration[i], "F5")}");
        File.WriteAllText(Path.Combine(outDir, "exp6_ga_curve.csv"), csv.ToString(), new UTF8Encoding(true));
    }

    // ------------------------------------------------------------------ допоміжне

    static IEnumerable<byte[]> Blocks(byte[] d, int size)
    {
        for (int off = 0; off < d.Length; off += size) yield return d.AsSpan(off, Math.Min(size, d.Length - off)).ToArray();
    }

    /// <summary>
    /// Точний розмір закодованого блока для заданого розбору — так само, як рахує BlockCodec.Encode,
    /// але без заміни на NO_COMPRESSION: 3 розріджені таблиці (2 + 3·symbols), token_count і bitstream_size (8), бітовий потік.
    /// </summary>
    static long ForcedSize(List<Token> toks)
    {
        var fl = new long[BlockCodec.LitSize]; var fn = new long[BlockCodec.LenSize]; var fd = new long[BlockCodec.DistSize];
        foreach (var t in toks)
        {
            if (!t.IsMatch) fl[t.Lit]++;
            else { fl[256]++; fn[t.Len - 3]++; fd[Lz77.BitLength(t.Dist - 1)]++; }
        }
        var ll = Huffman.BuildLengths(fl); var nl = Huffman.BuildLengths(fn); var dl = Huffman.BuildLengths(fd);
        long bits = 0;
        foreach (var t in toks)
        {
            if (!t.IsMatch) { bits += ll[t.Lit]; continue; }
            int code = Lz77.BitLength(t.Dist - 1);
            bits += ll[256] + nl[t.Len - 3] + dl[code] + (code >= 2 ? code - 1 : 0);
        }
        long tables = 3 * 2 + 3L * (ll.Count(x => x > 0) + nl.Count(x => x > 0) + dl.Count(x => x > 0));
        return tables + 8 + (bits + 7) / 8;
    }
}
