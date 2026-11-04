namespace MAIC.Domain.Knowledge;

public static class KnowledgeClock
{
    /// <summary>PostgreSQL stores microseconds; truncating keeps write responses equal to later reads.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset value) => new(value.UtcTicks / 10 * 10, TimeSpan.Zero);
}
