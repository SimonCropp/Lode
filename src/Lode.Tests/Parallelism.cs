using TUnit.Core.Interfaces;

// TUnit gives every test, keyed or not, one of a fixed number of slots, twice the processors.
// A window test takes the windows key and only then queues for a slot, behind the parallel
// tests, which hold theirs for as long as they run, so the windows lane, which takes as long as
// the whole run, sat waiting between its tests while they ran. The slots are made too many to
// run out, and the tests outside the windows lane take turns at as many as there were slots.
[assembly: OutsideTheWindowsLane]

static class Parallelism
{
    // More than there will be tests: a test waiting for its turn holds a slot meanwhile.
    [Before(TestDiscovery)]
    public static void EnoughSlots(BeforeTestDiscoveryContext context) =>
        context.Settings.Parallelism.MaximumParallelTests = 100_000;
}

[AttributeUsage(AttributeTargets.Assembly)]
sealed class OutsideTheWindowsLaneAttribute : Attribute, ITestRegisteredEventReceiver
{
    public ValueTask OnTestRegistered(TestRegisteredContext context)
    {
        var inLane = context.TestDetails
            .GetAttributes<NotInParallelAttribute>()
            .Any(_ => _.ConstraintKeys.Contains("windows"));
        if (!inLane)
        {
            context.SetParallelLimiter(new Turns());
        }

        return default;
    }

    sealed class Turns : IParallelLimit
    {
        public int Limit { get; } = Environment.ProcessorCount * 2;
    }
}
