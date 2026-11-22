/// <summary>
/// Find in Files searching a whole code folder for a word: leaving out what git ignores, as it
/// does by default, against searching everything, bin, obj and node_modules too, and as plain
/// text against as a regular expression. The folder is the one the LodeBenchmarkCode environment
/// variable names, a real code folder; without it, Lode's own repository. Git is asked what each
/// repository ignores every time, as it is outside the code folder, where nothing watches it.
/// </summary>
[MemoryDiagnoser]
public class ContentSearchBenchmarks
{
    const string codeVariable = "LodeBenchmarkCode";

    [Params(false, true)]
    public bool IncludeIgnored { get; set; }

    [Params(false, true)]
    public bool Regex { get; set; }

    string folder = "";

    [GlobalSetup]
    public void Setup() =>
        folder = Environment.GetEnvironmentVariable(codeVariable) ?? Lode();

    // The repository this was built from, found up from where it runs.
    static string Lode()
    {
        for (var current = AppContext.BaseDirectory; current != null; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }
        }

        throw new("Not run from inside a repository, and LodeBenchmarkCode isn't set.");
    }

    // How many matches, so the search is used.
    [Benchmark]
    public async Task<int> Search()
    {
        var find = "Directory";
        if (Regex)
        {
            find = @"\bDirector(y|ies)\b";
        }

        var query = new FindQuery(find, "", Regex, IncludeIgnored: IncludeIgnored);
        var where = SearchWhere.Parse(query.Where, folder, [], out _)!;
        var matcher = Matcher.Create(query, out _)!;
        var found = new ConcurrentQueue<FileResult>();
        var counts = new ContentSearch.Counts();
        await ContentSearch.Run(query, where, matcher, null, _ => GitScope.List(_, Cancel.None), found, counts, new(), Cancel.None);
        return counts.Matches;
    }
}
