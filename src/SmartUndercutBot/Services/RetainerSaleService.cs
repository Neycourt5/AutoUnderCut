using Dalamud.Game.Chat;
using Dalamud.Plugin.Services;
using SmartUndercutBot.Core.Models;
using SmartUndercutBot.Core.Services;

namespace SmartUndercutBot.Services;

/// <summary>
/// Records every retainer sale the game announces while the player is online, from
/// the system log message itself (LogMessage 745/748, see <see cref="RetainerSaleParser"/>).
///
/// This is the plugin's only real sale signal: retainer passes see an emptied slot,
/// but not what sold, for how much or when. Each sale goes into the price memory,
/// one SOLD line goes to the session log, and <see cref="SaleRecorded"/> lets the
/// retainer loop refill the slot early. The first few sales of a session also log
/// their raw parameters, so the parameter layout can be confirmed from a session
/// log after a game patch without guessing.
/// </summary>
public sealed class RetainerSaleService : IDisposable
{
    private const int RawParameterLogLimit = 5;

    private readonly IChatGui chat;
    private readonly ConfigurationService configuration;
    private readonly Func<PriceMemory> memory;
    private readonly Func<IEnumerable<(uint ItemId, bool IsHighQuality, uint Quantity, uint AskingUnitPrice)>> listings;
    private readonly Func<uint, MarketItemFacts?> lookup;
    private readonly AutomationLog log;
    private int rawParametersLogged;

    public RetainerSaleService(
        IChatGui chat,
        ConfigurationService configuration,
        Func<PriceMemory> memory,
        Func<IEnumerable<(uint ItemId, bool IsHighQuality, uint Quantity, uint AskingUnitPrice)>> listings,
        Func<uint, MarketItemFacts?> lookup,
        AutomationLog log)
    {
        this.chat = chat;
        this.configuration = configuration;
        this.memory = memory;
        this.listings = listings;
        this.lookup = lookup;
        this.log = log;
        chat.LogMessage += OnLogMessage;
    }

    /// <summary>Raised on the framework thread after a new sale has been recorded.</summary>
    public event Action<RetainerSale>? SaleRecorded;

    public RetainerSale? LastSale { get; private set; }

    private void OnLogMessage(ILogMessage message)
    {
        try
        {
            if (!configuration.Current.SaleTrackingEnabled || !RetainerSaleParser.IsSaleMessage(message.LogMessageId))
                return;
            // The message object is only valid inside this callback: read it all now.
            var values = new int?[Math.Clamp(message.ParameterCount, 0, 16)];
            for (var i = 0; i < values.Length; i++)
                values[i] = message.TryGetIntParameter(i, out var value) ? value : null;
            if (rawParametersLogged < RawParameterLogLimit)
            {
                rawParametersLogged++;
                log.Add(AutomationLogLevel.Debug,
                    $"RETAINER SALE raw: message {message.LogMessageId}, {values.Length} parameter(s): " +
                    string.Join(", ", values.Select((v, i) => $"[{i}]={(v?.ToString() ?? "-")}")));
            }
            var parsed = RetainerSaleParser.Parse(message.LogMessageId,
                index => index >= 0 && index < values.Length ? values[index] : null, DateTimeOffset.UtcNow);
            if (parsed is null)
            {
                log.Add(AutomationLogLevel.Warning,
                    $"RETAINER SALE: message {message.LogMessageId} could not be read " +
                    $"({string.Join(", ", values.Select(v => v?.ToString() ?? "-"))}). Sales are still counted by retainer checks.");
                return;
            }
            // The parameter layout comes from game data, not from a session log yet. If
            // it were off by one, the town would be read as an item: refuse anything that
            // is not a tradable item sold in a quantity the market board allows.
            if (lookup(parsed.ItemId) is not { IsTradable: true } facts || parsed.Quantity > (uint)facts.StackSize)
            {
                log.Add(AutomationLogLevel.Warning,
                    $"RETAINER SALE: message {message.LogMessageId} did not read as a sale (item {parsed.ItemId}, " +
                    $"quantity {parsed.Quantity}; parameters {string.Join(", ", values.Select(v => v?.ToString() ?? "-"))}). " +
                    "Nothing was recorded; sales are still counted by retainer checks.");
                return;
            }
            var sale = RetainerSaleParser.ResolveQuality(parsed, listings(), configuration.Current.ObservedMarketTaxPercent);
            sale = sale with { ItemName = facts.Name };
            if (!memory().RecordSale(sale))
                return;
            LastSale = sale;
            log.Add(AutomationLogLevel.Information,
                $"SOLD {sale.ItemName}{(sale.IsHighQuality ? " HQ" : string.Empty)} x{sale.Quantity} for " +
                $"{sale.GilAfterFees:N0} gil after fees ({sale.NetUnitPrice:N0} each).");
            SaleRecorded?.Invoke(sale);
        }
        catch (Exception ex)
        {
            // A sale message must never break chat or the retainer loop.
            log.Add(AutomationLogLevel.Warning, $"RETAINER SALE: could not record a sale ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    public void Dispose() => chat.LogMessage -= OnLogMessage;
}
