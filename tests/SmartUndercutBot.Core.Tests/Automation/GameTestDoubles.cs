using SmartUndercutBot.Core.Models;

// Only the boundary to Dalamud is substituted. Tests compile and run the actual
// procurement controller, configuration, ledger, and IPC adapters.
namespace Dalamud.Configuration
{
    public interface IPluginConfiguration { int Version { get; set; } }
}

namespace Dalamud.Plugin.Services
{
    public interface IFramework { event Action<IFramework>? Update; }
    public interface ICommandManager { bool ProcessCommand(string command); }
    public interface IPlayerState
    {
        bool IsLoaded { get; }
        TestWorldRef CurrentWorld { get; }
        TestWorldRef HomeWorld { get; }
    }
    public record TestWorldRef(TestWorld Value);
    public record TestWorld(string Name);
}

namespace Dalamud.Plugin.Ipc
{
    public interface ICallGateSubscriber<T>
    {
        bool HasFunction { get; }
        bool HasAction { get; }
        T InvokeFunc();
        void InvokeAction();
    }
    public interface ICallGateSubscriber<T1, T>
    {
        bool HasFunction { get; }
        T InvokeFunc(T1 arg);
    }
    public interface ICallGateSubscriber<T1, T2, T3, T>
    {
        bool HasFunction { get; }
        T InvokeFunc(T1 a, T2 b, T3 c);
    }
}

namespace Dalamud.Plugin
{
    using Dalamud.Plugin.Ipc;
    public interface IDalamudPluginInterface
    {
        ICallGateSubscriber<T> GetIpcSubscriber<T>(string name);
        ICallGateSubscriber<T1, T> GetIpcSubscriber<T1, T>(string name);
        ICallGateSubscriber<T1, T2, T3, T> GetIpcSubscriber<T1, T2, T3, T>(string name);
    }
}

namespace SmartUndercutBot.Automation
{
    public sealed class AutomationController
    {
        public int? LastKnownFreeSaleSlots { get; set; } = 5;
        public IReadOnlyList<StockExposure> ListedStock { get; set; } = [];
        public bool IsActive { get; set; }
        public bool RequiresManualRestart { get; set; }
        public int Starts { get; private set; }
        public void Halt(string reason) { IsActive = false; RequiresManualRestart = true; }
        public void StartNow() { Starts++; IsActive = true; RequiresManualRestart = false; }
    }

    public sealed class BagListingController
    {
        public bool IsBusy { get; set; }
        public bool IsRetainerListOpen { get; set; } = true;
        public bool IsSuspended { get; private set; }
        public int Resumes { get; private set; }
        public void ResumeAutomatic() { IsSuspended = false; Resumes++; }
        public void Halt(string reason) { IsBusy = false; IsSuspended = true; }
    }
}

namespace SmartUndercutBot.Services
{
    public sealed class ConfigurationService
    {
        public Configuration Current { get; } = new();
        public void Save() => Current.Normalize();
    }
    public enum AutomationLogLevel { Debug, Information, Warning, Error }
    public sealed class AutomationLog
    {
        public List<string> Messages { get; } = [];
        public void Add(AutomationLogLevel level, string message) => Messages.Add(message);
    }
    public interface IRetainerListingService
    {
        bool IsRetainerListOpen { get; }
        IReadOnlySet<ulong> OwnedRetainerIds { get; }
    }
    public interface IUniversalisService
    {
        string ResolveDataCenter(string configured);
        Task<IReadOnlyList<ProcurementMarketItem>> ScanAsync(IReadOnlyList<ProcurementRule> rules,
            string dataCenter, CancellationToken cancellationToken);
    }
    public interface ITaskbarAttentionService
    {
        void StopFlashing();
        void FlashUntilForeground();
    }
}
