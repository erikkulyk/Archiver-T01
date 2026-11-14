namespace SmartArchiver;

public readonly record struct Token(int Dist, int Len, byte Lit)
{
    public bool IsMatch => Len > 0;
    public static Token Literal(byte b) => new(0, 0, b);
    public static Token Match(int dist, int len) => new(dist, len, 0);
}

public enum ParseMode { Greedy, Lazy, Dp }

public static class Lz77
{
    public const int MaxMatch = 258;
    public const int MaxWindow = 32768;
    public const int LiteralCost = 9;

    public static int BitLength(int v) => v <= 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)v);

    /// <summary>Фіксована вартість посилання з team_config: 17 + max(0, bit_length(d-1) - 1).</summary>
    public static int MatchCost(int dist) => 17 + Math.Max(0, BitLength(dist - 1) - 1);

    public static long Cost(IEnumerable<Token> tokens)
    {
        long c = 0;
        foreach (var t in tokens) c += t.IsMatch ? MatchCost(t.Dist) : LiteralCost;
        return c;
    }

    public static byte[] Expand(IList<Token> tokens)
    {
        var o = new List<byte>();
        foreach (var t in tokens)
        {
            if (!t.IsMatch) { o.Add(t.Lit); continue; }
            if (t.Dist <= 0 || t.Dist > MaxWindow || t.Dist > o.Count || t.Len < 3 || t.Len > MaxMatch)
                throw new ArchiveException(E.Payload, "invalid LZ77 reference");
            int s = o.Count - t.Dist;
            for (int k = 0; k < t.Len; k++) o.Add(o[s + k]);
        }
        return o.ToArray();
    }

    private sealed class MatchFinder
    {
        private const int HashBits = 15;
        private readonly byte[] _d; private readonly int _n, _window, _minMatch, _maxChain;
        private readonly int[] _head, _prev;

        public MatchFinder(byte[] d, int window, int minMatch, int maxChain)
        {
            _d = d; _n = d.Length; _window = window; _minMatch = minMatch; _maxChain = maxChain;
            _head = new int[1 << HashBits]; Array.Fill(_head, -1);
            _prev = new int[Math.Max(1, _n)];
        }

        private int Hash(int i) => ((_d[i] << 10) ^ (_d[i + 1] << 5) ^ _d[i + 2]) & ((1 << HashBits) - 1);

        public void Insert(int i)
        {
            if (i + 2 >= _n) return;
            int h = Hash(i); _prev[i] = _head[h]; _head[h] = i;
        }

        /// <summary>Кандидати зі строго зростаючою довжиною; для кожної довжини — найближча відстань.</summary>
        public void Candidates(int i, List<(int Dist, int Len)> res)
        {
            res.Clear();
            if (i + 2 >= _n) return;
            int maxLen = Math.Min(MaxMatch, _n - i), best = _minMatch - 1, steps = 0;
            int p = _head[Hash(i)];
            while (p >= 0 && steps < _maxChain)
            {
                int dist = i - p;
                if (dist > _window) break;
                int l = 0;
                while (l < maxLen && _d[p + l] == _d[i + l]) l++;
                if (l > best) { best = l; res.Add((dist, l)); if (l == maxLen) break; }
                p = _prev[p]; steps++;
            }
        }
    }

    public static List<Token> Parse(byte[] d, ParseMode mode, int window, int minMatch, int maxChain)
    {
        window = Math.Clamp(window, 1, MaxWindow);
        minMatch = Math.Clamp(minMatch, 3, MaxMatch);
        return mode switch
        {
            ParseMode.Greedy => ParseGreedy(d, window, minMatch, maxChain),
            ParseMode.Lazy => ParseLazy(d, window, minMatch, maxChain),
            _ => ParseDp(d, window, minMatch, maxChain),
        };
    }

    private static List<Token> ParseGreedy(byte[] d, int window, int minMatch, int maxChain)
    {
        var mf = new MatchFinder(d, window, minMatch, maxChain);
        var res = new List<Token>(); var cand = new List<(int Dist, int Len)>();
        int i = 0, n = d.Length;
        while (i < n)
        {
            mf.Candidates(i, cand);
            if (cand.Count > 0)
            {
                var (dist, len) = cand[^1];
                res.Add(Token.Match(dist, len));
                for (int k = 0; k < len; k++) mf.Insert(i + k);
                i += len;
            }
            else { res.Add(Token.Literal(d[i])); mf.Insert(i); i++; }
        }
        return res;
    }

    private static List<Token> ParseLazy(byte[] d, int window, int minMatch, int maxChain)
    {
        var mf = new MatchFinder(d, window, minMatch, maxChain);
        var res = new List<Token>(); var c1 = new List<(int Dist, int Len)>(); var c2 = new List<(int Dist, int Len)>();
        int i = 0, n = d.Length;
        while (i < n)
        {
            mf.Candidates(i, c1);
            if (c1.Count == 0) { res.Add(Token.Literal(d[i])); mf.Insert(i); i++; continue; }
            var (dist, len) = c1[^1];
            mf.Insert(i);
            if (len < MaxMatch && i + 1 < n)
            {
                mf.Candidates(i + 1, c2);
                if (c2.Count > 0 && c2[^1].Len > len) { res.Add(Token.Literal(d[i])); i++; continue; }
            }
            res.Add(Token.Match(dist, len));
            for (int k = 1; k < len; k++) mf.Insert(i + k);
            i += len;
        }
        return res;
    }

    private static List<Token> ParseDp(byte[] d, int window, int minMatch, int maxChain)
    {
        int n = d.Length;
        var mf = new MatchFinder(d, window, minMatch, maxChain);
        var cands = new List<(int Dist, int Len)>[n];
        for (int i = 0; i < n; i++)
        {
            var l = new List<(int Dist, int Len)>();
            mf.Candidates(i, l); cands[i] = l; mf.Insert(i);
        }
        var cost = new long[n + 1];
        var choiceLen = new int[n]; var choiceDist = new int[n];
        for (int i = n - 1; i >= 0; i--)
        {
            long best = LiteralCost + cost[i + 1]; int bl = 0, bd = 0, prevLen = 0;
            foreach (var (dist, len) in cands[i])
            {
                int lo = Math.Max(minMatch, prevLen + 1), mc = MatchCost(dist);
                for (int l = lo; l <= len; l++)
                {
                    long c = mc + cost[i + l];
                    if (c < best) { best = c; bl = l; bd = dist; }
                }
                prevLen = len;
            }
            cost[i] = best; choiceLen[i] = bl; choiceDist[i] = bd;
        }
        var res = new List<Token>();
        for (int i = 0; i < n;)
        {
            if (choiceLen[i] == 0) { res.Add(Token.Literal(d[i])); i++; }
            else { res.Add(Token.Match(choiceDist[i], choiceLen[i])); i += choiceLen[i]; }
        }
        return res;
    }
}
