using System.Text;

namespace SmartUndercutBot.Core.Services;

public static class RetainerMenuMatcher
{
    public static int FindEntrustGilEntry(IReadOnlyList<string> entries, string localizedExpected)
    {
        var expected = Normalize(localizedExpected);
        if (expected.Length > 0)
        {
            for (var index = 0; index < entries.Count; index++)
            {
                if (Normalize(entries[index]) == expected)
                    return index;
            }
        }

        // The localized Addon row is authoritative. This fallback is deliberately
        // narrow and only handles an English client if a UI payload decorates the
        // rendered row differently than the sheet text.
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = Normalize(entries[index]);
            if (entry.Contains("gil", StringComparison.Ordinal) &&
                (entry.Contains("withdraw", StringComparison.Ordinal) ||
                 entry.Contains("entrust", StringComparison.Ordinal)))
                return index;
        }

        return -1;
    }

    private static string Normalize(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
                result.Append(Rune.ToLowerInvariant(rune));
        }
        return result.ToString();
    }
}
