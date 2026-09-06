namespace Lagerkraft.Shared;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset? now = null) =>
        UtcNow = now ?? new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; private set; }

    public void Advance(TimeSpan by) => UtcNow += by;

    public void Set(DateTimeOffset now) => UtcNow = now;
}
