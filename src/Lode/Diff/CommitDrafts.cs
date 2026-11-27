/// <summary>
/// What is being got ready to commit in each repository while Lode runs: the files left
/// unticked, the lines picked of the files only partly to go in, and the summary and description
/// typed. Shared by the Changes pane and the diff, wherever it shows, so lines picked in the one
/// show in the other, and going to another repository and back loses none of it.
/// </summary>
static class CommitDrafts
{
    internal sealed class Draft
    {
        public HashSet<string> Unticked { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The lines picked of each file only some of whose lines go in.
        /// </summary>
        public Dictionary<string, LinePicks> Picks { get; } = new(StringComparer.Ordinal);

        public string Summary { get; set; } = "";
        public string Description { get; set; } = "";

        /// <summary>
        /// The commit being amended, the last on the branch, which the commit goes in place of;
        /// null for a new commit.
        /// </summary>
        public string? Amending { get; set; }

        /// <summary>
        /// True for a file to go in whole, false for one left out, null for one only some of
        /// whose lines go in.
        /// </summary>
        public bool? TickOf(string path)
        {
            if (Unticked.Contains(path))
            {
                return false;
            }

            if (Picks.ContainsKey(path))
            {
                return null;
            }

            return true;
        }

        /// <summary>
        /// Ticks a file whole, or leaves it out, forgetting any lines picked of it.
        /// </summary>
        public void SetTick(string path, bool tick)
        {
            Picks.Remove(path);
            if (tick)
            {
                Unticked.Remove(path);
                return;
            }

            Unticked.Add(path);
        }

        /// <summary>
        /// Forgets the files no longer changed: changed again, they start ticked.
        /// </summary>
        public void Keep(IEnumerable<string> changed)
        {
            var paths = changed.ToHashSet(StringComparer.Ordinal);
            Unticked.IntersectWith(paths);
            foreach (var path in Picks.Keys.Where(_ => !paths.Contains(_)).ToList())
            {
                Picks.Remove(path);
            }
        }
    }

    static Dictionary<string, Draft> drafts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The draft of <paramref name="repo"/>, made the first time it is asked for. Outside a
    /// repository, one kept nowhere.
    /// </summary>
    public static Draft Of(string? repo)
    {
        if (repo == null)
        {
            return new();
        }

        if (!drafts.TryGetValue(repo, out var found))
        {
            found = new();
            drafts[repo] = found;
        }

        return found;
    }

    /// <summary>
    /// The lines picked of a file changed, by its repository and path, for whatever shows its tick
    /// or its lines to draw them again.
    /// </summary>
    public static event Action<string, string>? PicksChanged;

    /// <summary>
    /// The lines picked of the file at <paramref name="path"/>, whose diff is
    /// <paramref name="diff"/>: every line of one ticked whole, none of one left out, and those
    /// picked of one partly ticked, unless its diff has changed since, when it is ticked whole
    /// again, as which lines were picked can't be told any more.
    /// </summary>
    public static LinePicks PicksOf(string repo, string path, TextDiff diff)
    {
        var draft = Of(repo);
        var all = LinePicks.All(diff);
        if (draft.Picks.TryGetValue(path, out var picks))
        {
            if (picks.PatchHash == diff.PatchHash)
            {
                return picks;
            }

            draft.Picks.Remove(path);
            PicksChanged?.Invoke(repo, path);
        }

        if (draft.Unticked.Contains(path))
        {
            return all.With(LinePicks.Pickable(diff), false);
        }

        return all;
    }

    /// <summary>
    /// Notes the lines picked of a file: every line ticks it whole, none leaves it out.
    /// </summary>
    public static void SetPicks(string repo, string path, LinePicks picks)
    {
        var draft = Of(repo);
        switch (picks.State)
        {
            case true:
                draft.SetTick(path, true);
                break;
            case false:
                draft.SetTick(path, false);
                break;
            default:
                draft.Picks[path] = picks;
                draft.Unticked.Remove(path);
                break;
        }

        PicksChanged?.Invoke(repo, path);
    }

    /// <summary>
    /// Tells what shows the file that its tick changed: ticked whole, or not at all.
    /// </summary>
    public static void Notify(string repo, string path) =>
        PicksChanged?.Invoke(repo, path);

    /// <summary>
    /// Ticks the file whole again, its lines picked lost: its diff changed after they were picked.
    /// </summary>
    public static void ForgetPicks(string repo, string path)
    {
        if (Of(repo).Picks.Remove(path))
        {
            PicksChanged?.Invoke(repo, path);
        }
    }
}
