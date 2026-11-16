/// <summary>
/// <see cref="Updates"/> for tests: running 0.3.0, as a copy installed as a dotnet tool or as a
/// build's output, with NuGet's answer and the clock given rather than asked for.
/// </summary>
static class TestUpdates
{
    public static readonly DateTimeOffset Now = new(2026, 11, 15, 9, 30, 0, TimeSpan.Zero);

    public const string Store = @"C:\Users\Someone\.dotnet\tools\.store\lode\0.3.0\lode\0.3.0\tools\net11.0\any\";

    public static NuGetVersion Current { get; } = NuGetVersion.Parse("0.3.0");

    public static Updates Installed(Settings settings, Func<Cancel, Task<NuGetVersion?>> fetchLatest, TimeProvider? time = null) =>
        new(settings, Updates.Find(Store), Current, fetchLatest, time ?? new Clock(Now));

    public static Updates Installed(Settings settings, string? latest = null) =>
        Installed(settings, _ => Task.FromResult(Parse(latest)));

    public static Updates Build(Settings? settings = null) =>
        new(settings ?? new(), null, Current, _ => Task.FromResult<NuGetVersion?>(null), new Clock(Now));

    static NuGetVersion? Parse(string? version)
    {
        if (version == null)
        {
            return null;
        }

        return NuGetVersion.Parse(version);
    }
}

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
