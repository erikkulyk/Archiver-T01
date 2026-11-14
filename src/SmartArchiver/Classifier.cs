using System.Text.Json;

namespace SmartArchiver;

public static class Features
{
    public const int Count = 27;
    private const int SampleMax = 262144;

    public static double[] Compute(byte[] data)
    {
        var f = new double[Count];
        int n = Math.Min(data.Length, SampleMax);
        if (n == 0) return f;
        var hist = new int[256]; var bi = new int[65536];
        int print = 0, letters = 0, digits = 0, ws = 0, punct = 0, nl = 0, ctrl = 0, high = 0, ff = 0, same = 0, near = 0, lag4 = 0;
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
        for (int i = 0; i < n; i++)
        {
            byte b = data[i]; hist[b]++;
            if ((b >= 32 && b < 127) || b == 9 || b == 10 || b == 13) print++;
            if ((b | 32) >= 'a' && (b | 32) <= 'z') letters++;
            if (b >= '0' && b <= '9') digits++;
            if (b == ' ' || b == 9 || b == 10) ws++;
            if ("{}[]();<>=,:\"'".IndexOf((char)b) >= 0) punct++;
            if (b == 10) nl++;
            if (b < 32 && b != 9 && b != 10 && b != 13) ctrl++;
            if (b >= 128) high++;
            if (b == 255) ff++;
            if (i > 0)
            {
                byte p = data[i - 1]; bi[(p << 8) | b]++;
                if (p == b) same++;
                if (Math.Abs(p - b) <= 2) near++;
                sx += p; sy += b; sxx += (double)p * p; syy += (double)b * b; sxy += (double)p * b;
            }
            if (i >= 4 && data[i - 4] == b) lag4++;
        }
        double H(int[] h, int total) { double e = 0; foreach (var c in h) if (c > 0) { double p = (double)c / total; e -= p * Math.Log2(p); } return e; }
        double nn = n; int m = Math.Max(1, n - 1);
        double cov = sxy / m - (sx / m) * (sy / m), vx = sxx / m - (sx / m) * (sx / m), vy = syy / m - (sy / m) * (sy / m);
        int k = 0;
        f[k++] = H(hist, n) / 8; f[k++] = H(bi, m) / 16; f[k++] = print / nn; f[k++] = hist[0] / nn; f[k++] = high / nn;
        f[k++] = hist.Count(c => c > 0) / 256.0; f[k++] = hist.Max() / nn; f[k++] = letters / nn; f[k++] = digits / nn;
        f[k++] = ws / nn; f[k++] = punct / nn; f[k++] = Math.Min(1, nl / nn * 10); f[k++] = nl == 0 ? 1 : Math.Min(1, n / (double)nl / 200);
        f[k++] = (vx > 1e-9 && vy > 1e-9) ? cov / Math.Sqrt(vx * vy) : 0;
        f[k++] = same / (double)m; f[k++] = near / (double)m; f[k++] = lag4 / nn; f[k++] = ctrl / nn; f[k++] = ff / nn;
        for (int g = 0; g < 8; g++) { int s = 0; for (int v = g * 32; v < g * 32 + 32; v++) s += hist[v]; f[k++] = s / nn; }
        return f;
    }
}

public sealed class Mlp
{
    public int In { get; set; } public int Hid { get; set; } public int Out { get; set; }
    public double[] W1 { get; set; } = Array.Empty<double>(); public double[] B1 { get; set; } = Array.Empty<double>();
    public double[] W2 { get; set; } = Array.Empty<double>(); public double[] B2 { get; set; } = Array.Empty<double>();
    public double[] Mean { get; set; } = Array.Empty<double>(); public double[] Std { get; set; } = Array.Empty<double>();

    public static Mlp Create(int nIn, int nHid, int nOut, Random rng)
    {
        var m = new Mlp { In = nIn, Hid = nHid, Out = nOut, W1 = new double[nIn * nHid], B1 = new double[nHid], W2 = new double[nHid * nOut], B2 = new double[nOut], Mean = new double[nIn], Std = Enumerable.Repeat(1.0, nIn).ToArray() };
        double s1 = Math.Sqrt(2.0 / nIn), s2 = Math.Sqrt(2.0 / nHid);
        for (int i = 0; i < m.W1.Length; i++) m.W1[i] = (rng.NextDouble() * 2 - 1) * s1;
        for (int i = 0; i < m.W2.Length; i++) m.W2[i] = (rng.NextDouble() * 2 - 1) * s2;
        return m;
    }

    private double[] Forward(double[] x, double[] h)
    {
        for (int j = 0; j < Hid; j++)
        {
            double s = B1[j];
            for (int i = 0; i < In; i++) s += W1[i * Hid + j] * ((x[i] - Mean[i]) / Std[i]);
            h[j] = Math.Tanh(s);
        }
        var o = new double[Out]; double max = double.NegativeInfinity;
        for (int c = 0; c < Out; c++)
        {
            double s = B2[c];
            for (int j = 0; j < Hid; j++) s += W2[j * Out + c] * h[j];
            o[c] = s; max = Math.Max(max, s);
        }
        double z = 0; for (int c = 0; c < Out; c++) { o[c] = Math.Exp(o[c] - max); z += o[c]; }
        for (int c = 0; c < Out; c++) o[c] /= z;
        return o;
    }

    public int Predict(double[] x)
    {
        var p = Forward(x, new double[Hid]); int best = 0;
        for (int c = 1; c < Out; c++) if (p[c] > p[best]) best = c;
        return best;
    }

    public void Train(List<double[]> xs, List<int> ys, int epochs, double lr, Random rng)
    {
        int n = xs.Count;
        for (int i = 0; i < In; i++)
        {
            Mean[i] = xs.Average(x => x[i]);
            Std[i] = Math.Max(1e-6, Math.Sqrt(xs.Average(x => (x[i] - Mean[i]) * (x[i] - Mean[i]))));
        }
        var h = new double[Hid]; var idx = Enumerable.Range(0, n).ToArray();
        for (int ep = 0; ep < epochs; ep++)
        {
            for (int i = n - 1; i > 0; i--) { int j = rng.Next(i + 1); (idx[i], idx[j]) = (idx[j], idx[i]); }
            double rate = lr / (1 + 0.01 * ep);
            foreach (var t in idx)
            {
                var x = xs[t]; var p = Forward(x, h);
                var dO = (double[])p.Clone(); dO[ys[t]] -= 1;
                var dH = new double[Hid];
                for (int j = 0; j < Hid; j++)
                {
                    double g = 0;
                    for (int c = 0; c < Out; c++) { g += dO[c] * W2[j * Out + c]; W2[j * Out + c] -= rate * dO[c] * h[j]; }
                    dH[j] = g * (1 - h[j] * h[j]);
                }
                for (int c = 0; c < Out; c++) B2[c] -= rate * dO[c];
                for (int j = 0; j < Hid; j++)
                {
                    for (int i = 0; i < In; i++) W1[i * Hid + j] -= rate * dH[j] * ((x[i] - Mean[i]) / Std[i]);
                    B1[j] -= rate * dH[j];
                }
            }
        }
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this));

    public static Mlp Load(string path)
    {
        var model = JsonSerializer.Deserialize<Mlp>(File.ReadAllText(path));
        if (model is null)
            throw new InvalidDataException("Classifier model file is empty or invalid.");
        if (model.W1 is null || model.B1 is null || model.W2 is null || model.B2 is null || model.Mean is null || model.Std is null ||
            model.In != Features.Count || model.Out != Classifier.Classes || model.Hid < 1 ||
            model.W1.Length != model.In * model.Hid || model.B1.Length != model.Hid ||
            model.W2.Length != model.Hid * model.Out || model.B2.Length != model.Out ||
            model.Mean.Length != model.In || model.Std.Length != model.In ||
            model.Std.Any(v => !double.IsFinite(v) || v <= 0))
            throw new InvalidDataException("Classifier model has incompatible dimensions or invalid normalization values.");
        return model;
    }
}

public static class Classifier
{
    public const int Classes = 7;

    public static double MacroF1(IList<int> truth, IList<int> pred, int classes = Classes)
    {
        if (truth.Count == 0 || truth.Count != pred.Count)
            throw new ArgumentException("Macro-F1 requires equally sized, non-empty truth and prediction lists.");
        if (classes < 1 || truth.Any(v => v < 0 || v >= classes) || pred.Any(v => v < 0 || v >= classes))
            throw new ArgumentException("Macro-F1 labels are outside the configured class range.");
        double sum = 0;
        for (int c = 0; c < classes; c++)
        {
            int tp = 0, fp = 0, fn = 0;
            for (int i = 0; i < truth.Count; i++)
            {
                if (pred[i] == c && truth[i] == c) tp++;
                else if (pred[i] == c) fp++;
                else if (truth[i] == c) fn++;
            }
            double p = tp + fp == 0 ? 0 : (double)tp / (tp + fp), r = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
            sum += p + r == 0 ? 0 : 2 * p * r / (p + r);
        }
        return sum / classes;
    }

    /// <summary>
    /// Reads the supplied labels.csv schema: file,type,type_name,size.
    /// The class label is the second column (type), not the final size column.
    /// </summary>
    public static (List<double[]> X, List<int> Y) LoadSet(string dir)
    {
        var labelsPath = Path.Combine(dir, "labels.csv");
        if (!File.Exists(labelsPath))
            throw new FileNotFoundException("labels.csv was not found in the dataset directory.", labelsPath);

        var xs = new List<double[]>();
        var ys = new List<int>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int lineNumber = 0;

        foreach (var line in File.ReadLines(labelsPath))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = ParseCsvLine(line);
            if (lineNumber == 1 && fields.Count >= 2 &&
                fields[0].Trim().Equals("file", StringComparison.OrdinalIgnoreCase) &&
                fields[1].Trim().Equals("type", StringComparison.OrdinalIgnoreCase))
                continue;

            if (fields.Count < 2)
                throw new InvalidDataException($"Invalid labels.csv row {lineNumber}: expected at least file,type columns.");

            var relativePath = fields[0].Trim();
            if (relativePath.Length == 0)
                throw new InvalidDataException($"Invalid labels.csv row {lineNumber}: file name is empty.");
            if (!int.TryParse(fields[1].Trim(), out int label) || label < 0 || label >= Classes)
                throw new InvalidDataException($"Invalid labels.csv row {lineNumber}: type must be an integer from 0 to {Classes - 1}.");
            if (!seen.Add(relativePath))
                throw new InvalidDataException($"Invalid labels.csv row {lineNumber}: duplicate file '{relativePath}'.");

            var path = Path.GetFullPath(Path.Combine(dir, relativePath));
            var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(root, StringComparison.Ordinal) || !File.Exists(path))
                throw new InvalidDataException($"Invalid labels.csv row {lineNumber}: file '{relativePath}' does not exist inside the dataset directory.");

            xs.Add(Features.Compute(File.ReadAllBytes(path)));
            ys.Add(label);
        }

        if (xs.Count == 0)
            throw new InvalidDataException($"No labeled samples were loaded from '{labelsPath}'.");
        return (xs, ys);
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else quoted = !quoted;
            }
            else if (ch == ',' && !quoted)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else field.Append(ch);
        }
        if (quoted) throw new InvalidDataException("Invalid labels.csv: unterminated quoted field.");
        fields.Add(field.ToString());
        return fields;
    }

    /// <summary>Кілька незалежних ініціалізацій; модель обирається за macro-F1 на valid.</summary>
    public static (Mlp Model, double ValidF1, List<double> AllF1) TrainBest(
        List<double[]> tx, List<int> ty, List<double[]> vx, List<int> vy, int seed, int inits = 3, int hidden = 32, int epochs = 300)
    {
        if (tx.Count == 0 || vx.Count == 0)
            throw new ArgumentException("Training and validation datasets must both contain samples.");
        if (tx.Count != ty.Count || vx.Count != vy.Count)
            throw new ArgumentException("Feature and label counts must match for both datasets.");
        if (inits < 1 || hidden < 1 || epochs < 1)
            throw new ArgumentOutOfRangeException(nameof(inits), "Initializations, hidden units and epochs must be positive.");
        if (tx.Any(x => x.Length != Features.Count) || vx.Any(x => x.Length != Features.Count))
            throw new ArgumentException($"Every sample must contain exactly {Features.Count} features.");
        if (ty.Any(y => y < 0 || y >= Classes) || vy.Any(y => y < 0 || y >= Classes))
            throw new ArgumentException($"Class labels must be in the range 0..{Classes - 1}.");

        Mlp? best = null; double bestF1 = -1; var all = new List<double>();
        for (int k = 0; k < inits; k++)
        {
            var rng = new Random(seed + k);
            var m = Mlp.Create(Features.Count, hidden, Classes, rng);
            m.Train(tx, ty, epochs, 0.02, rng);
            var f1 = MacroF1(vy, vx.Select(m.Predict).ToList()); all.Add(f1);
            if (f1 > bestF1) { bestF1 = f1; best = m; }
        }
        return (best!, bestF1, all);
    }
}
