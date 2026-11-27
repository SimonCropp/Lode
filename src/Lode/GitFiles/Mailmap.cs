/// <summary>
/// Who git log names an author as, by a .mailmap, as git's mailmap.c reads one: the working
/// tree's .mailmap, then the file mailmap.file names, a later line for the same email winning.
/// A line maps an email to a name, an email to another, or, with a name of the commit's too,
/// only that name with that email. Emails and names are matched whatever their case.
/// </summary>
sealed class Mailmap
{
    sealed class Mapping
    {
        public string? Name { get; set; }
        public Dictionary<string, string?> ByName { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    readonly Dictionary<string, Mapping> byEmail = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => byEmail.Count == 0;

    /// <summary>
    /// The repository's mailmap. Throws for mailmap.blob, which is read from an object, as isn't
    /// done here.
    /// </summary>
    public static Mailmap Read(GitRepository repository)
    {
        var mailmap = new Mailmap();
        if (repository.Config.Get("mailmap.blob") != null)
        {
            throw new GitFilesException("mailmap.blob needs git");
        }

        mailmap.ReadFile(Path.Combine(repository.WorkTree, ".mailmap"));

        if (repository.Config.Get("mailmap.file") is { } file)
        {
            mailmap.ReadFile(GitConfig.ExpandPath(file));
        }

        return mailmap;
    }

    void ReadFile(string path)
    {
        if (GitRepository.ReadSmall(path) is not { } text)
        {
            return;
        }

        foreach (var line in text.Split('\n'))
        {
            Add(line.TrimEnd('\r'));
        }
    }

    // As git's read_mailmap_line: the proper name and email, then the commit's name and email.
    internal void Add(string line)
    {
        if (line.StartsWith('#'))
        {
            return;
        }

        if (Parse(line, false, out var name, out var email) is not { } rest)
        {
            if (email != null)
            {
                Map(name, email, null, null);
            }

            return;
        }

        Parse(rest, true, out var oldName, out var oldEmail);
        if (email != null)
        {
            Map(name, email, oldName, oldEmail);
        }
    }

    // A name, then an email in angle brackets: the rest of the line after them, or null where
    // nothing comes after.
    static string? Parse(string text, bool allowEmptyEmail, out string? name, out string? email)
    {
        name = null;
        email = null;
        var left = text.IndexOf('<');
        if (left < 0)
        {
            return null;
        }

        var right = text.IndexOf('>', left + 1);
        if (right < 0 ||
            (!allowEmptyEmail && right == left + 1))
        {
            return null;
        }

        var trimmed = text[..left].Trim();
        if (trimmed.Length > 0)
        {
            name = trimmed;
        }

        email = text[(left + 1)..right];
        var rest = text[(right + 1)..];
        if (rest.Length == 0)
        {
            return null;
        }

        return rest;
    }

    void Map(string? newName, string newEmail, string? oldName, string? oldEmail)
    {
        oldEmail ??= newEmail;
        if (!byEmail.TryGetValue(oldEmail, out var mapping))
        {
            mapping = new();
            byEmail[oldEmail] = mapping;
        }

        if (oldName == null)
        {
            if (newName != null)
            {
                mapping.Name = newName;
            }

            return;
        }

        mapping.ByName[oldName] = newName;
    }

    /// <summary>
    /// The name git log's %aN gives an author: as mapped, otherwise as the commit has it.
    /// </summary>
    public string NameOf(string name, string email)
    {
        if (!byEmail.TryGetValue(email, out var mapping))
        {
            return name;
        }

        if (mapping.ByName.TryGetValue(name, out var byName))
        {
            return byName ?? name;
        }

        return mapping.Name ?? name;
    }
}
