using BlamTags;

namespace BlamTags.Shell;

/// <summary>Fuzzy "did you mean …?" hints — closest root field name by
/// Levenshtein distance, within tolerance. Mirrors the Rust suggester.</summary>
public static class Suggest
{
    public static string? FieldName(TagStruct parent, string typed)
    {
        string typedLower = typed.ToLowerInvariant();
        int bestDist = int.MaxValue;
        string? best = null;
        foreach (var candidate in parent.FieldNames())
        {
            int d = EditDistance(typedLower, candidate.ToLowerInvariant());
            if (d < bestDist) { bestDist = d; best = candidate; }
        }
        return best is not null && bestDist <= typed.Length / 2 + 1 ? best : null;
    }

    private static int EditDistance(string a, string b)
    {
        int m = a.Length, n = b.Length;
        var dp = new int[m + 1, n + 1];
        for (int i = 0; i <= m; i++) dp[i, 0] = i;
        for (int j = 0; j <= n; j++) dp[0, j] = j;
        for (int i = 1; i <= m; i++)
            for (int j = 1; j <= n; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                dp[i, j] = System.Math.Min(System.Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
            }
        return dp[m, n];
    }
}
