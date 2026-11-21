/// <summary>
/// Reordering the folders pinned to Quick access. Windows has no call that moves a pinned
/// folder; pinning adds a folder at the end. So a new order is reached by moving folders to the
/// end one after another, each unpinned then pinned again straight away, so no folder is left
/// unpinned for longer than it takes to pin it again.
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
    /// The folders that, moved to the end in turn, turn <paramref name="current"/> into
    /// <paramref name="desired"/>. Those at the start of <paramref name="desired"/> that are
    /// already in that order in <paramref name="current"/> stay put; the rest move. None when
    /// they already match.
    /// </summary>
    public static List<string> ToMoveToEnd(IReadOnlyList<string> current, IReadOnlyList<string> desired)
    {
        var staying = 0;
        foreach (var folder in current)
        {
            if (staying < desired.Count &&
                Locations.Same(folder, desired[staying]))
            {
                staying++;
            }
        }

        return desired.Skip(staying).ToList();
    }
}
