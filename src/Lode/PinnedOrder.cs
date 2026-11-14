/// <summary>
/// Reordering the folders pinned to Quick access. Windows has no call that moves a pinned
/// folder; pinning adds a folder at the end. So a new order is reached by unpinning every
/// folder from the first one out of place onwards, then pinning them again in the new order.
/// </summary>
static class PinnedOrder
{
    /// <summary>
    /// <paramref name="pinned"/> with <paramref name="moved"/> taken out and put back at
    /// <paramref name="index"/>, counted in the list without it.
    /// </summary>
    public static List<string> Move(IReadOnlyList<string> pinned, string moved, int index)
    {
        var result = pinned.Where(_ => !Locations.Same(_, moved)).ToList();
        result.Insert(Math.Clamp(index, 0, result.Count), moved);
        return result;
    }

    /// <summary>
    /// The unpins then pins that turn <paramref name="current"/> into <paramref name="desired"/>,
    /// in order. None when they already match.
    /// </summary>
    public static List<(string Path, bool Pin)> Steps(IReadOnlyList<string> current, IReadOnlyList<string> desired)
    {
        var first = 0;
        while (first < current.Count &&
               first < desired.Count &&
               Locations.Same(current[first], desired[first]))
        {
            first++;
        }

        var steps = new List<(string, bool)>();
        for (var index = first; index < current.Count; index++)
        {
            steps.Add((current[index], false));
        }

        for (var index = first; index < desired.Count; index++)
        {
            steps.Add((desired[index], true));
        }

        return steps;
    }
}
