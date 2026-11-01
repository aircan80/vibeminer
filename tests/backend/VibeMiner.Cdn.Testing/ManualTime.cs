namespace VibeMiner.Cdn.Testing;

/// <summary>A clock a test sets by hand. Only <see cref="GetUtcNow"/> is manual; timers stay real.</summary>
public sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
