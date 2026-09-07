using SmartUndercutBot.Core.Models;

namespace SmartUndercutBot.Automation;

public interface IRetainerAutomation
{
    int? LastKnownFreeSaleSlots { get; }
    IReadOnlyList<StockExposure> ListedStock { get; }
    bool IsActive { get; }
    bool RequiresManualRestart { get; }
    void StartNow();
    void Halt(string reason);
}
