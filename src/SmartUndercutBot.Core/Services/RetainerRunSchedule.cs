namespace SmartUndercutBot.Core.Services;

// Bag fills may use the bell between full checks, but must not postpone a full
// price/gil pass indefinitely. Keep its deadline independently of UI step delays.
public sealed class RetainerRunSchedule
{
    public DateTimeOffset? NextFullCheck { get; private set; }

    public DateTimeOffset CompletePass(DateTimeOffset now, TimeSpan interval, bool fillOnly)
    {
        if (!fillOnly)
            NextFullCheck = now + interval;
        else
            NextFullCheck ??= now;
        return NextFullCheck.Value > now ? NextFullCheck.Value : now;
    }
}
