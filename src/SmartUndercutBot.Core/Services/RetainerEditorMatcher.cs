using System.Globalization;
using System.Text;
using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

public static class RetainerEditorMatcher
{
    public static string NormalizeName(string text)
    {
        var output = new StringBuilder();
        var space = false;
        foreach (var c in text)
        {
            if (c == '\uE03C' || c == '\u00AD' || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format)
                continue;
            if (char.IsWhiteSpace(c)) { space = output.Length > 0; continue; }
            if (space) output.Append(' ');
            space = false;
            output.Append(c);
        }
        return output.ToString();
    }

    public static bool NameMatches(string displayed, string expected) =>
        !string.IsNullOrWhiteSpace(expected) && string.Equals(NormalizeName(displayed), NormalizeName(expected),
            StringComparison.OrdinalIgnoreCase);

    public static RetainerListing? Resolve(IEnumerable<RetainerListing> listings, IReadOnlySet<short> excluded,
        uint itemId, string name, uint quantity, uint price, bool hq) => listings
        .Where(x => !excluded.Contains(x.Slot) && (itemId == 0 || x.ItemId == itemId) && NameMatches(name, x.ItemName))
        .Where(x => x.Quantity == quantity && x.IsHighQuality == hq)
        .OrderByDescending(x => x.CurrentPrice == price)
        .ThenBy(x => x.Slot).FirstOrDefault();
}
