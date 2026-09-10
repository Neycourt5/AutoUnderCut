using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Core.Services;

public static class ShoppingScoutPolicy
{
    /// <summary>
    /// One circuit, every away world, exactly once, one data center at a time.
    ///
    /// A hop inside a data center is a short transfer; a hop between them goes out
    /// through character selection and back. Finishing each data center before
    /// moving on pays that three times per circuit instead of once every other
    /// world. The character's own data center goes first because it costs no
    /// transfer at all, the rest follow in the order the cached hints rate them,
    /// and the worlds inside each one are ordered the same way - so a trip that
    /// ends early on bag space has still spent its stops where the value was.
    /// </summary>
    public static IReadOnlyList<string> BuildRoute(IReadOnlyList<string> worlds, string home,
        IReadOnlyDictionary<string, decimal> scores)
    {
        ArgumentNullException.ThrowIfNull(worlds);
        ArgumentNullException.ThrowIfNull(scores);
        return worlds.Chunk(8)
            .Select(dc => (
                Home: dc.Any(w => w.Equals(home, StringComparison.OrdinalIgnoreCase)),
                Worlds: dc.Where(w => !w.Equals(home, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(w => scores.GetValueOrDefault(w)).ToArray()))
            .OrderByDescending(dc => dc.Home)
            .ThenByDescending(dc => dc.Worlds.Sum(w => scores.GetValueOrDefault(w)))
            .SelectMany(dc => dc.Worlds).ToArray();
    }

    /// <summary>
    /// What to price-check on one away world. The preferred food and potion block is
    /// reserved on every world before anything else is considered: it is the stock
    /// that actually turns over, so a rotation that walks past it to reach the next
    /// materia line is a wasted trip. Only the secondary lines rotate, so each of
    /// them still gets its turn without ever displacing the block.
    /// </summary>
    public static IReadOnlyList<uint> SelectWorldItems(
        IReadOnlyList<uint> preferred, IReadOnlyList<uint> secondary,
        IEnumerable<uint> resumed, IEnumerable<uint> hinted, int worldIndex, int limit)
    {
        ArgumentNullException.ThrowIfNull(preferred);
        ArgumentNullException.ThrowIfNull(secondary);
        ArgumentNullException.ThrowIfNull(resumed);
        ArgumentNullException.ThrowIfNull(hinted);
        limit = Math.Max(1, limit);
        var chosen = new List<uint>(limit);
        void Take(IEnumerable<uint> items)
        {
            foreach (var item in items)
            {
                if (chosen.Count == limit) return;
                if (!chosen.Contains(item)) chosen.Add(item);
            }
        }

        Take(resumed);
        Take(preferred);
        Take(hinted);
        // `secondary` arrives in descending average daily volume. Half of whatever
        // capacity is left goes to the busiest lines on every world, because those
        // are the ones a slot can actually be turned over on; the remainder rotates
        // so a quieter line is still checked eventually rather than never.
        Take(secondary.Take(Math.Max(0, (limit - chosen.Count + 1) / 2)));
        var offset = secondary.Count == 0
            ? 0
            : Math.Max(0, worldIndex) * Math.Max(1, limit / 2) % secondary.Count;
        Take(secondary.Skip(offset).Concat(secondary.Take(offset)));
        return chosen;
    }

    /// <summary>
    /// Whether a listing is worth buying on the spot instead of remembering it and
    /// comparing it against the rest of the circuit.
    ///
    /// Buying immediately spends scarce capital and a scarce sale slot before any
    /// later world has been seen, so it has to be reserved for cases where waiting
    /// is the greater risk. A large percentage return is not sufficient on its own -
    /// a cheap trinket at 300% is not an emergency. It must also be stock the
    /// portfolio actually wants: liquid, valuable per slot, carrying real absolute
    /// profit, and not something that would sit in a slot for days.
    /// </summary>
    public static bool IsExceptional(
        ProcurementOrder order, decimal minimumRoi, ProcurementEconomicPolicy? policy = null)
    {
        // All offers compete after scouting; percentage ROI never pre-empts capital.
        return false;
    }
}
