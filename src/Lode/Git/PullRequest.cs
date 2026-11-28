/// <summary>
/// What Create pull request does for the branch checked out, as GitHub Desktop's does (Ctrl+R):
/// opens the page on the site origin is on that creates one from the branch, where it tracks one
/// of origin's that is there still, as the status bar's View on views it; offers to push it first
/// where it tracks none, as it isn't on origin yet, and Push would publish it; otherwise says why
/// there is none to create.
/// </summary>
abstract record PullRequest
{
    /// <summary>
    /// The page that creates one from the branch on origin.
    /// </summary>
    public sealed record Open(string Url) : PullRequest;

    /// <summary>
    /// A branch tracking none, to be pushed first, publishing it to a branch of its name on
    /// origin, as Push does.
    /// </summary>
    public sealed record PushFirst(string Branch) : PullRequest;

    /// <summary>
    /// Why there is none to create.
    /// </summary>
    public sealed record Cannot(string Why) : PullRequest;

    /// <summary>
    /// For the branch checked out in <paramref name="state"/>, with origin on
    /// <paramref name="site"/>, null where it is on no site Lode knows. A branch tracking one
    /// gone from origin, merged and deleted say, one of the repository's own, or another
    /// remote's, upstream's in a fork say, has no branch on origin by its name, and so none to
    /// create one from, any more than it has a page to view there.
    /// </summary>
    public static PullRequest For(GitState state, RemoteSite? site)
    {
        if (site == null)
        {
            return new Cannot("Origin isn't on GitHub, GitLab, Bitbucket or Azure DevOps, where Lode creates pull requests.");
        }

        var name = site.PullRequestName;
        if (state.Branch is not { } branch)
        {
            return new Cannot($"No branch is checked out. Check one out first, to create a {name} from.");
        }

        if (state.OriginBranch is { } onOrigin)
        {
            return new Open(site.PullRequestUrl(onOrigin));
        }

        if (state.UpstreamName is not { } upstream)
        {
            if (state.Commit == null)
            {
                return new Cannot($"{branch} has no commits yet, so nothing to create a {name} from.");
            }

            return new PushFirst(branch);
        }

        if (state.UpstreamGone)
        {
            return new Cannot($"{branch} tracks {upstream}, which has gone from origin, deleted once it was merged say, so there is no branch there to create a {name} from.");
        }

        return new Cannot($"{branch} tracks {upstream}, not a branch on origin, so there is none there to create a {name} from.");
    }

    /// <summary>
    /// What is asked of a branch tracking none: to push it, then create the pull request, as
    /// GitHub Desktop asks to publish one first.
    /// </summary>
    public static GitStatusActions.Question PushFirstQuestion(string branch, GitState state, RemoteSite site)
    {
        var name = site.PullRequestName;
        var text = $"A {name} is made from a branch origin has.";
        if (state.Changes > 0)
        {
            text += " Uncommitted changes aren't pushed: commit them first.";
        }

        return new(
            $"Create {name}",
            $"{branch} isn't on origin yet",
            text,
            [
                new(
                    GitStatusActions.Choice.Push,
                    $"Push, then create the {name}",
                    $"Publish {branch} to origin, as origin/{branch}, which it tracks from then on, then open the page on {site.Name} to create the {name}.")
            ]);
    }
}
