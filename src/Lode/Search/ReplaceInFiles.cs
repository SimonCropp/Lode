/// <summary>
/// Find in Files' Replace: in each file found, every match of what was looked for, as the file
/// is now rather than as it was found, replaced. With a regular expression, the replacement
/// can name its groups, $1 or ${name}. With Preserve case, it takes each match's case: all
/// capitals, all small, or a capital first. Each file is written back in the encoding it was
/// read in, byte order mark and all, and where it is, so it keeps its links and attributes. One
/// whose text wouldn't come back to the same bytes, a UTF-16 file with a broken pair say, is
/// left alone. What each was is kept in %Temp%\Lode\Replace for Undo, which puts back each file
/// still as Replace left it, and Redo, each still as it was before. One whose write fails part
/// way is put back as it was.
/// </summary>
static class ReplaceInFiles
{
    public static string TempRoot { get; } = Path.Combine(Path.GetTempPath(), "Lode", "Replace");

    /// <param name="Error">Why the file was left alone: it is read-only, say.</param>
    public sealed record Failure(string Path, string Error);

    /// <summary>
    /// What Replace did: the files it changed as they are now, the replacements marked, how
    /// many it replaced, and those it couldn't change.
    /// </summary>
    public sealed record Outcome(IReadOnlyList<FileResult> Changed, int Replaced, IReadOnlyList<Failure> Failed, UndoHistory.Entry? Undo);

    /// <summary>
    /// Replaces what <paramref name="matcher"/> finds in each of <paramref name="files"/> with
    /// <paramref name="replacement"/>.
    /// </summary>
    public static Outcome Run(
        IReadOnlyList<string> files,
        Matcher matcher,
        string replacement,
        bool preserveCase,
        int context,
        IProgress<string>? progress,
        Cancel cancel)
    {
        var kept = Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));
        var changed = new List<FileResult>();
        var failed = new List<Failure>();
        var written = new List<Written>();
        var replaced = 0;
        for (var index = 0; index < files.Count; index++)
        {
            // Stopped, what was written so far is still kept, for Undo.
            if (cancel.IsCancellationRequested)
            {
                break;
            }

            var path = files[index];
            progress?.Report($"{index + 1:N0} of {files.Count:N0} files");
            try
            {
                // Numbered by the file, not by how many are written: one that fails keeps what it
                // was where the next would otherwise keep its own over it, and that copy may be
                // all there is of it.
                if (ReplaceIn(path, matcher, replacement, preserveCase, context, kept, index) is not { } done)
                {
                    continue;
                }

                written.Add(done.Written);
                changed.Add(done.Shown);
                replaced += done.Shown.MatchCount;
            }
            catch (Exception exception)
                when (
                    exception is
                        IOException or
                        UnauthorizedAccessException or
                        SecurityException or
                        RegexMatchTimeoutException)
            {
                failed.Add(new(path, Describe(exception)));
            }
            catch (CantWriteBack exception)
            {
                failed.Add(new(path, exception.Message));
            }
        }

        UndoHistory.Entry? undo = null;
        if (written.Count > 0)
        {
            undo = new Replaced(written);
        }

        return new(changed, replaced, failed, undo);
    }

    static string Describe(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException => "Windows didn't allow it to be written: it may be read-only.",
            RegexMatchTimeoutException => "The pattern took too long over it.",
            _ => exception.Message
        };

    sealed class CantWriteBack(string message) : Exception(message);

    // A file Replace wrote: where it kept what the file was, and what it wrote, to put either
    // back, and the hash of each, to tell whether the file is still that.
    sealed record Written(string Path, string Before, string After, byte[] BeforeHash, byte[] AfterHash);

    static (FileResult Shown, Written Written)? ReplaceIn(string path, Matcher matcher, string replacement, bool preserveCase, int context, string kept, int number)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
        {
            throw new CantWriteBack("It is read-only.");
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.LongLength > TextFile.MostBytes)
        {
            throw new CantWriteBack($"It is over {TextFile.MostBytes / 1024 / 1024} MB.");
        }

        string text;
        TextFile.Encoding kind;
        var file = TextFile.Decode(bytes, out _);
        if (file == null)
        {
            throw new CantWriteBack("It is binary.");
        }

        text = file.Text.ToString();
        kind = file.Kind;

        if (!TextFile.Encode(text, kind).AsSpan().SequenceEqual(bytes))
        {
            throw new CantWriteBack("Its text wouldn't be written back as the same bytes it was read from.");
        }

        var (newText, ranges) = Replace(text, matcher, replacement, preserveCase);
        if (ranges.Count == 0 ||
            newText == text)
        {
            return null;
        }

        // What was read went back to the same bytes, checked above, but what replaces it may
        // not: a file that isn't UTF-8 is read a byte a character, and has no byte for most
        // characters, each of which would be written as a question mark.
        var after = TextFile.Encode(newText, kind);
        if (!TextFile.Holds(after, kind, newText))
        {
            throw new CantWriteBack("What replaces it has characters the file's encoding has none for.");
        }

        Directory.CreateDirectory(kept);
        var before = Path.Combine(kept, $"{number}.before");
        var afterCopy = Path.Combine(kept, $"{number}.after");
        File.WriteAllBytes(before, bytes);
        File.WriteAllBytes(afterCopy, after);
        WriteInPlace(path, after, bytes, before);
        return (
            FileResult.From(path, newText, ranges, context),
            new(path, before, afterCopy, SHA256.HashData(bytes), SHA256.HashData(after)));
    }

    /// <summary>
    /// <paramref name="text"/> with every match replaced, and where each replacement is in it.
    /// </summary>
    internal static (string Text, List<(int Start, int Length)> Ranges) Replace(string text, Matcher matcher, string replacement, bool preserveCase)
    {
        var builder = new StringBuilder(text.Length);
        var ranges = new List<(int Start, int Length)>();
        if (matcher.Regex is { } regex)
        {
            var last = 0;
            foreach (Match match in regex.Matches(text))
            {
                builder.Append(text, last, match.Index - last);
                var value = match.Result(replacement);
                if (preserveCase)
                {
                    value = InCaseOf(match.Value, value);
                }

                ranges.Add((builder.Length, value.Length));
                builder.Append(value);
                last = match.Index + match.Length;
            }

            builder.Append(text, last, text.Length - last);
            return (builder.ToString(), ranges);
        }

        var matches = new List<(int Start, int Length)>();
        matcher.Find(text, matches);
        var position = 0;
        foreach (var (start, length) in matches)
        {
            builder.Append(text, position, start - position);
            var value = replacement;
            if (preserveCase)
            {
                value = InCaseOf(text.Substring(start, length), value);
            }

            ranges.Add((builder.Length, value.Length));
            builder.Append(value);
            position = start + length;
        }

        builder.Append(text, position, text.Length - position);
        return (builder.ToString(), ranges);
    }

    /// <summary>
    /// <paramref name="replacement"/> in the case of <paramref name="match"/>: all capitals, all
    /// small, or a capital first; otherwise as typed.
    /// </summary>
    internal static string InCaseOf(string match, string replacement)
    {
        if (replacement.Length == 0 ||
            !match.Any(char.IsLetter))
        {
            return replacement;
        }

        if (!match.Any(char.IsLower))
        {
            return replacement.ToUpperInvariant();
        }

        if (!match.Any(char.IsUpper))
        {
            return replacement.ToLowerInvariant();
        }

        var first = match.First(char.IsLetter);
        var rest = match[(match.IndexOf(first) + 1)..];
        if (char.IsUpper(first) &&
            !rest.Any(char.IsUpper))
        {
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        }

        return replacement;
    }

    // Truncated and written, rather than replaced by a new file, so it keeps its identity: hard
    // links, its attributes, and a program watching it. A write that fails once it is truncated,
    // on a disk that fills as what replaces is longer, or a share that drops, would leave it
    // short or empty, so what it was is written back. Should that fail too, the copy at wasKept
    // is all there is of it, and the failure ends saying where that is. Unbuffered, so a write
    // that fails throws here, not later as the file is closed.
    static void WriteInPlace(string path, byte[] bytes, byte[] was, string wasKept)
    {
        using var stream = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.Read, bufferSize: 0);
        try
        {
            Write(stream, bytes);
        }
        catch (Exception exception)
        {
            try
            {
                stream.Position = 0;
                stream.SetLength(0);
                Write(stream, was);
            }
            catch (Exception putBack)
            {
                throw new IOException($"{Describe(exception)} It couldn't be put back as it was either: what it was is kept in {wasKept}", putBack);
            }

            throw;
        }
    }

    /// <summary>
    /// Writes bytes to a file truncated for them: what it is to be, or, should that fail, what it
    /// was. Tests fail it, as a disk that fills or a share that drops would.
    /// </summary>
    internal static Action<FileStream, byte[]> Write = (stream, bytes) => stream.Write(bytes);

    /// <summary>
    /// Takes away what Replace kept more than a day ago: the undo history doesn't outlast Lode.
    /// </summary>
    public static void ClearOldTemp()
    {
        if (!Directory.Exists(TempRoot))
        {
            return;
        }

        var before = DateTime.UtcNow.AddDays(-1);
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(TempRoot))
            {
                if (Directory.GetLastWriteTimeUtc(folder) < before)
                {
                    try
                    {
                        Directory.Delete(folder, true);
                    }
                    catch (Exception exception)
                        when (
                            exception is
                                IOException or
                                UnauthorizedAccessException)
                    {
                        // Left for the next start.
                    }
                }
            }
        }
        catch (Exception exception)
            when (
                exception is
                    IOException or
                    UnauthorizedAccessException)
        {
            // Left for the next start.
        }
    }

    // Undo puts back what each file was, where it is still what Replace wrote; Redo writes again
    // what Replace wrote, where it is still what it was. A file changed since is left as it is,
    // and said so.
    sealed class Replaced(IReadOnlyList<Written> files) : UndoHistory.Entry(Named(files.Count))
    {
        static string Named(int count)
        {
            if (count == 1)
            {
                return "replace in 1 file";
            }

            return $"replace in {count:N0} files";
        }

        public override UndoHistory.Outcome Undo(UndoHistory.IFileActions actions, IntPtr owner) =>
            Put(write: _ => _.Before, isNow: _ => _.After, whenIs: _ => _.AfterHash);

        public override UndoHistory.Outcome Redo(UndoHistory.IFileActions actions, IntPtr owner) =>
            Put(write: _ => _.After, isNow: _ => _.Before, whenIs: _ => _.BeforeHash);

        // isNow is the copy of what the file is, when it is still that: where it is kept, should
        // the write fail and putting it back fail too.
        UndoHistory.Outcome Put(Func<Written, string> write, Func<Written, string> isNow, Func<Written, byte[]> whenIs)
        {
            var left = new List<string>();
            var done = 0;
            foreach (var file in files)
            {
                try
                {
                    var now = File.Exists(file.Path) ? File.ReadAllBytes(file.Path) : null;
                    if (now == null ||
                        !SHA256.HashData(now).AsSpan().SequenceEqual(whenIs(file)))
                    {
                        left.Add($"{file.Path} has changed since");
                        continue;
                    }

                    WriteInPlace(file.Path, File.ReadAllBytes(write(file)), now, isNow(file));
                    done++;
                }
                catch (Exception exception)
                    when (
                        exception is
                            IOException or
                            UnauthorizedAccessException or
                            SecurityException)
                {
                    left.Add($"{file.Path}: {exception.Message}");
                }
            }

            string? failure = null;
            if (left.Count > 0)
            {
                failure = $"Left as they are:\n{string.Join("\n", left)}";
            }

            if (done == 0)
            {
                return new(UndoHistory.Result.Gone, failure);
            }

            return new(UndoHistory.Result.Done, failure);
        }
    }
}
