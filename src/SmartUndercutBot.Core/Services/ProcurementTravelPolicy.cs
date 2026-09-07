namespace SmartUndercutBot.Core.Services;

public static class ProcurementTravelPolicy
{
    private static readonly HashSet<string> ExcludedWorlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bismarck", "Ravana", "Sephirot", "Sophia", "Zurvan",
    };

    public static bool CanShopOnWorld(string world) =>
        !string.IsNullOrWhiteSpace(world) && !ExcludedWorlds.Contains(world.Trim());

    public static string ShoppingScope(string? scope)
    {
        var allowed = (scope ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !x.Equals("Oceania", StringComparison.OrdinalIgnoreCase) &&
                        !x.Equals("Materia", StringComparison.OrdinalIgnoreCase) && CanShopOnWorld(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return allowed.Length == 0 ? "North-America" : string.Join(',', allowed);
    }
}
