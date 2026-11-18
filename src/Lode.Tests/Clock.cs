/// <summary>
/// A clock that says what it is told, in UTC, so a time shown in local time reads the same on
/// every machine.
/// </summary>
sealed class Clock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}