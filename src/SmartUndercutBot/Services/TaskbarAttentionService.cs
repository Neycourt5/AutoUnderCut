using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SmartUndercutBot.Services;

public interface ITaskbarAttentionService
{
    void FlashUntilForeground();
    void StopFlashing();
}

/// <summary>
/// Uses the same Windows taskbar-attention mechanism as tell-notification plugins.
/// FLASHW_TIMERNOFG stops automatically when the game becomes the foreground window.
/// </summary>
public sealed class TaskbarAttentionService : ITaskbarAttentionService, IDisposable
{
    private const uint FlashStop = 0;
    private const uint FlashAll = 3;
    private const uint FlashTimerNoForeground = 12;

    public void FlashUntilForeground() => Flash(FlashAll | FlashTimerNoForeground, uint.MaxValue);

    public void StopFlashing() => Flash(FlashStop, 0);

    private static void Flash(uint flags, uint count)
    {
        var window = Process.GetCurrentProcess().MainWindowHandle;
        if (window == 0)
            return;
        var info = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            Window = window,
            Flags = flags,
            Count = count,
            Timeout = 0,
        };
        FlashWindowEx(ref info);
    }

    public void Dispose() => StopFlashing();

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);
}
