namespace SmartArchiver;

public static class Knapsack
{
    /// <summary>Точний рюкзак 0/1 (ДП). Повертає індекси обраних предметів.</summary>
    public static int[] Dp(int[] weights, long[] values, long capacity)
    {
        int n = weights.Length;
        if (capacity < 0 || n == 0) return Array.Empty<int>();
        int W = checked((int)capacity);
        var dp = new long[W + 1];
        var take = new bool[n * (W + 1)];
        for (int i = 0; i < n; i++)
        {
            int w = weights[i];
            if (w > W) continue;
            for (int c = W; c >= w; c--)
            {
                long cand = dp[c - w] + values[i];
                if (cand > dp[c]) { dp[c] = cand; take[i * (W + 1) + c] = true; }
            }
        }
        var sel = new List<int>(); int cap = W;
        for (int i = n - 1; i >= 0; i--)
            if (take[i * (W + 1) + cap]) { sel.Add(i); cap -= weights[i]; }
        sel.Reverse();
        return sel.ToArray();
    }

    /// <summary>Жадібний baseline: за спаданням value/weight.</summary>
    public static int[] Greedy(int[] weights, long[] values, long capacity)
    {
        var order = Enumerable.Range(0, weights.Length)
            .OrderByDescending(i => (double)values[i] / Math.Max(1, weights[i])).ToArray();
        var sel = new List<int>(); long used = 0;
        foreach (var i in order)
            if (used + weights[i] <= capacity) { sel.Add(i); used += weights[i]; }
        sel.Sort();
        return sel.ToArray();
    }

    public static long Value(IEnumerable<int> sel, long[] values) => sel.Sum(i => values[i]);
}
