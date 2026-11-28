/// <summary>
/// Git purge, run for real against throwaway repositories in the temp folder. Only moving to the
/// Recycle Bin is stood in for, so a test run leaves nothing there.
/// </summary>
public class GitPurgeTests
{
    [Test]
    public async Task AFolderInARepoIsJustItself()
    {
        using var code = new CodeCommandsTests.Scratch();
        Directory.CreateDirectory(code.At("Repo", ".git"));
        Directory.CreateDirectory(code.At("Repo", "src", "Deep"));

        await Assert.That(Folders(code, "Repo")).IsEquivalentTo(["Repo"]);
        await Assert.That(Folders(code, "Repo", "src", "Deep")).IsEquivalentTo(["Repo/src/Deep"]);
    }

    // However deep, but not into a repository: one nested in another is left alone.
    [Test]
    public async Task OutsideARepoEveryRepoBelow()
    {
        using var code = new CodeCommandsTests.Scratch();
        Directory.CreateDirectory(code.At("One", ".git"));
        Directory.CreateDirectory(code.At("One", "Nested", ".git"));
        Directory.CreateDirectory(code.At("Group", "Two", ".git"));
        Directory.CreateDirectory(code.At("Group", "Deeper", "Still", "Three", ".git"));
        Directory.CreateDirectory(code.At("NotARepo", "Empty"));

        await Assert.That(Folders(code)).IsEquivalentTo(["Group/Deeper/Still/Three", "Group/Two", "One"]);
    }

    // The code folder may sit in a home folder kept in git, which would make everything in it
    // look untracked.
    [Test]
    public async Task ARepoAboveTheCodeFolderDoesNotCount()
    {
        using var scratch = new CodeCommandsTests.Scratch();
        Directory.CreateDirectory(scratch.At(".git"));
        Directory.CreateDirectory(scratch.At("Code", "Repo", ".git"));
        Directory.CreateDirectory(scratch.At("Code", "Group"));
        var codeFolder = scratch.At("Code");

        await Assert.That(GitPurge.Folders(scratch.At("Code", "Group"), codeFolder)).IsEmpty();
        await Assert.That(GitPurge.Folders(codeFolder, codeFolder).Select(scratch.Relative)).IsEquivalentTo(["Code/Repo"]);
    }

    // Several folders: what each would purge, less what purging another already covers. A
    // repository nested in another isn't covered, as a purge leaves it alone.
    [Test]
    public async Task SeveralFoldersArePurgedOnceEach()
    {
        using var code = new CodeCommandsTests.Scratch();
        Directory.CreateDirectory(code.At("Repo", ".git"));
        Directory.CreateDirectory(code.At("Repo", "src", "Deep"));
        Directory.CreateDirectory(code.At("Repo", "Nested", ".git"));
        Directory.CreateDirectory(code.At("Group", "One", ".git"));
        Directory.CreateDirectory(code.At("Group", "Two", ".git"));

        var folders = GitPurge.Folders(
            [
                code.At("Repo", "src", "Deep"),
                code.At("Repo"),
                code.At("Repo", "Nested"),
                code.At("Group"),
                code.At("Group", "One")
            ],
            code.Root);

        await Assert.That(folders.Select(code.Relative)).IsEquivalentTo(["Group/One", "Group/Two", "Repo", "Repo/Nested"], CollectionOrdering.Matching);
    }

    static List<string> Folders(CodeCommandsTests.Scratch code, params string[] parts) =>
        GitPurge.Folders(code.At(parts), code.Root)
            .Select(code.Relative)
            .ToList();

    [Test]
    public async Task FindsWhatGitDoesNotTrack()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = Untidy(code, "Repo");

        await Verify(
            new
            {
                repo = Names(GitPurge.Find(repo)),
                src = Names(GitPurge.Find(Path.Combine(repo, "src"))),
                // A folder git ignores: what is in it, not the folder itself.
                bin = Names(GitPurge.Find(Path.Combine(repo, "bin")))
            })
            .Snapshot(
                """
                {
                  repo: [
                    Empty/,
                    bin/,
                    src/Untracked.cs,
                    src/build.log,
                    src/obj/,
                    src/ünï cödé.txt
                  ],
                  src: [
                    Untracked.cs,
                    build.log,
                    obj/,
                    ünï cödé.txt
                  ],
                  bin: [
                    Debug
                  ]
                }
                """);
    }

    static List<string> Names(GitPurge.Found found) =>
        found.Entries
            .Select(_ => _.Name)
            .ToList();

    // Untracked and ignored files and folders go, read-only ones too. Tracked files, and the
    // repository nested inside, stay.
    [Test]
    public async Task PurgingDeletesOutright()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = Untidy(code, "Repo");
        File.SetAttributes(Path.Combine(repo, "bin", "Debug", "Repo.dll"), FileAttributes.ReadOnly);

        var outcome = await GitPurge.Run([repo], permanent: true, IntPtr.Zero);

        await Verify(
            new
            {
                outcome,
                left = Left(repo)
            })
            .Snapshot(
                """
                {
                  outcome: {
                    Succeeded: true,
                    Summary: 6 items deleted,
                    Details:
                Repo: 6 items
                  Empty/
                  bin/
                  src/Untracked.cs
                  src/build.log
                  src/obj/
                  src/ünï cödé.txt
                  },
                  left: [
                    .gitignore,
                    Nested,
                    Nested/Untracked.txt,
                    src,
                    src/Tracked.cs,
                    tracked.txt
                  ]
                }
                """);
    }

    // Below the top of a repository, only what is in that folder goes.
    [Test]
    public async Task PurgingAFolderInARepo()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = Untidy(code, "Repo");

        var outcome = await GitPurge.Run([Path.Combine(repo, "src")], permanent: true, IntPtr.Zero);

        await Verify(
            new
            {
                outcome,
                left = Left(repo)
            })
            .Snapshot(
                """
                {
                  outcome: {
                    Succeeded: true,
                    Summary: 4 items deleted,
                    Details:
                src: 4 items
                  Untracked.cs
                  build.log
                  obj/
                  ünï cödé.txt
                  },
                  left: [
                    .gitignore,
                    Empty,
                    Nested,
                    Nested/Untracked.txt,
                    bin,
                    bin/Debug,
                    bin/Debug/Repo.dll,
                    src,
                    src/Tracked.cs,
                    tracked.txt
                  ]
                }
                """);
    }

    // Every repository's at once, in one go to the Recycle Bin: here a stand-in, which turns one
    // file down as a file in use would be.
    [Test]
    public async Task PurgingToTheRecycleBin()
    {
        using var code = new CodeCommandsTests.Scratch();
        var one = Untidy(code, "One");
        var two = Untidy(code, "Two");
        var calls = new List<List<string>>();

        var outcome = await GitPurge.Run(
            [one, two],
            (paths, _) =>
            {
                calls.Add(paths.Select(code.Relative).ToList());
                IReadOnlyList<GitPurge.Failure> failures = [new(Path.Combine(two, "src", "build.log"), "The file is in use.")];
                return Task.FromResult(failures);
            },
            permanent: false);

        await Verify(
            new
            {
                calls,
                outcome
            })
            .Snapshot(
                """
                {
                  calls: [
                    [
                      One/Empty,
                      One/bin,
                      One/src/Untracked.cs,
                      One/src/build.log,
                      One/src/obj,
                      One/src/ünï cödé.txt,
                      Two/Empty,
                      Two/bin,
                      Two/src/Untracked.cs,
                      Two/src/build.log,
                      Two/src/obj,
                      Two/src/ünï cödé.txt
                    ]
                  ],
                  outcome: {
                    Succeeded: false,
                    Summary: 11 items moved to the Recycle Bin, 1 couldn't be,
                    Details:
                Two: 1 of 6 items couldn't be moved to the Recycle Bin
                  src/build.log: The file is in use.
                  Empty/
                  bin/
                  src/Untracked.cs
                  src/obj/
                  src/ünï cödé.txt

                One: 6 items
                  Empty/
                  bin/
                  src/Untracked.cs
                  src/build.log
                  src/obj/
                  src/ünï cödé.txt
                  }
                }
                """);
    }

    // It asks once the dry run has found what will go, saying how many items, how big, the first
    // of them, build output after the rest, and where they go; turned down, nothing goes.
    [Test]
    public async Task AsksOnceItKnowsWhatWillGo()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = Untidy(code, "Repo");
        var before = Left(repo);
        var questions = new List<string>();

        var outcome = await GitPurge.Run(
            [repo],
            permanent: true,
            IntPtr.Zero,
            confirm: question =>
            {
                questions.Add(question);
                return false;
            });

        await Assert.That(Left(repo)).IsEquivalentTo(before);
        await Verify(
            new
            {
                questions,
                outcome
            })
            .Snapshot(
                """
                {
                  questions: [
                    Delete 6 items, 34 bytes, that git doesn't track, ignored ones included, in Repo?

                    Empty/
                    src/Untracked.cs
                    src/build.log
                    src/ünï cödé.txt
                    bin/
                    src/obj/

                They are deleted permanently, as set in Options. This cannot be undone.
                  ],
                  outcome: {
                    Succeeded: false,
                    Summary: Cancelled,
                    Details: 
                  }
                }
                """);
    }

    // Of several repositories, each entry is named with the one it is in, and what doesn't fit
    // is counted. Said yes to, it all goes.
    [Test]
    public async Task AsksOnceForEveryRepo()
    {
        using var code = new CodeCommandsTests.Scratch();
        var one = Untidy(code, "One");
        var two = Untidy(code, "Two");
        var questions = new List<string>();
        var removed = new List<string>();

        var outcome = await GitPurge.Run(
            [one, two],
            (paths, _) =>
            {
                removed.AddRange(paths.Select(code.Relative));
                return Task.FromResult<IReadOnlyList<GitPurge.Failure>>([]);
            },
            permanent: false,
            confirm: question =>
            {
                questions.Add(question);
                return true;
            });

        await Verify(
            new
            {
                questions,
                removed,
                outcome
            })
            .Snapshot(
                """
                {
                  questions: [
                    Delete 12 items, 68 bytes, that git doesn't track, ignored ones included, in 2 repositories (One, Two)?

                    One/Empty/
                    One/src/Untracked.cs
                    One/src/build.log
                    One/src/ünï cödé.txt
                    Two/Empty/
                    Two/src/Untracked.cs
                    Two/src/build.log
                    Two/src/ünï cödé.txt
                    One/bin/
                    One/src/obj/
                    and 2 more

                They go to the Recycle Bin.
                  ],
                  removed: [
                    One/Empty,
                    One/bin,
                    One/src/Untracked.cs,
                    One/src/build.log,
                    One/src/obj,
                    One/src/ünï cödé.txt,
                    Two/Empty,
                    Two/bin,
                    Two/src/Untracked.cs,
                    Two/src/build.log,
                    Two/src/obj,
                    Two/src/ünï cödé.txt
                  ],
                  outcome: {
                    Succeeded: true,
                    Summary: 12 items moved to the Recycle Bin,
                    Details:
                One: 6 items
                  Empty/
                  bin/
                  src/Untracked.cs
                  src/build.log
                  src/obj/
                  src/ünï cödé.txt

                Two: 6 items
                  Empty/
                  bin/
                  src/Untracked.cs
                  src/build.log
                  src/obj/
                  src/ünï cödé.txt
                  }
                }
                """);
    }

    // With nothing to purge, there is nothing to ask.
    [Test]
    public async Task NothingToPurgeAsksNothing()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");
        var asked = false;

        var outcome = await GitPurge.Run(
            [repo],
            permanent: true,
            IntPtr.Zero,
            confirm: _ =>
            {
                asked = true;
                return true;
            });

        await Assert.That(asked).IsFalse();
        await Assert.That(outcome).IsEqualTo(new(true, "Nothing to purge", ""));
    }

    [Test]
    public async Task NothingToPurge()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = code.CreateRepo("Repo");

        var outcome = await GitPurge.Run([repo], permanent: true, IntPtr.Zero);

        await Assert.That(outcome).IsEqualTo(new(true, "Nothing to purge", ""));
    }

    // What Options keeps is left, as git clean -e leaves it, ignored or not, deep down or at the
    // top, the rest of a folder going around a file kept in it; the question says what is kept.
    // As Options has it at first: a .env and its siblings, Visual Studio's and Rider's folders,
    // and a project's .user file.
    [Test]
    public async Task WhatOptionsKeepsIsLeft()
    {
        using var code = new CodeCommandsTests.Scratch();
        var repo = Untidy(code, "Repo");
        Write(repo, ".env", "SECRET=1");
        Write(repo, ".env.local", "SECRET=2");
        Write(repo, ".vs/Repo/v17/.suo", "breakpoints");
        Write(repo, ".idea/workspace.xml", "run settings");
        Write(repo, "src/Repo.csproj.user", "launch arguments");
        Write(repo, "config/.env", "SECRET=3");
        Write(repo, "config/generated.json", "{}");
        var questions = new List<string>();

        var outcome = await GitPurge.Run(
            [repo],
            permanent: true,
            IntPtr.Zero,
            confirm: question =>
            {
                questions.Add(question);
                return true;
            },
            keep: new Settings().PurgeKeeps);

        await Verify(
            new
            {
                questions,
                outcome,
                left = Left(repo)
            })
            .Snapshot(
                """
                {
                  questions: [
                    Delete 7 items, 36 bytes, that git doesn't track, ignored ones included, in Repo?

                    Empty/
                    config/generated.json
                    src/Untracked.cs
                    src/build.log
                    src/ünï cödé.txt
                    bin/
                    src/obj/

                They are deleted permanently, as set in Options. This cannot be undone.

                Kept, as set in Options: .env .env.* .vs/ .idea/ *.user
                  ],
                  outcome: {
                    Succeeded: true,
                    Summary: 7 items deleted,
                    Details:
                Repo: 7 items
                  Empty/
                  bin/
                  config/generated.json
                  src/Untracked.cs
                  src/build.log
                  src/obj/
                  src/ünï cödé.txt
                  },
                  left: [
                    .env,
                    .env.local,
                    .gitignore,
                    .idea,
                    .idea/workspace.xml,
                    .vs,
                    .vs/Repo,
                    .vs/Repo/v17,
                    .vs/Repo/v17/.suo,
                    Nested,
                    Nested/Untracked.txt,
                    config,
                    config/.env,
                    src,
                    src/Repo.csproj.user,
                    src/Tracked.cs,
                    tracked.txt
                  ]
                }
                """);
    }

    // What someone might want, a .env say, where Options doesn't keep it, is named first, rather
    // than after however many bin folders git lists before it.
    [Test]
    public async Task TheQuestionNamesWhatIsntBuildOutputFirst()
    {
        var folder = @"C:\Code\Repo";
        var names = Enumerable.Range(1, 20)
            .Select(_ => $"Project{_:00}/bin/")
            .Append(".env")
            .Append("Project01/obj/")
            .Append("web/node_modules/")
            .Append("notes.txt");
        var found = new GitPurge.Found(folder, names.Select(_ => new GitPurge.Entry(_, Path.Combine(folder, _))).ToList(), null);

        var question = GitPurge.Question([found], 1024, permanent: false, keep: []);

        await Verify(question)
            .Snapshot(
                """
                Delete 24 items, 1.00 KB, that git doesn't track, ignored ones included, in Repo?

                    .env
                    notes.txt
                    Project01/bin/
                    Project02/bin/
                    Project03/bin/
                    Project04/bin/
                    Project05/bin/
                    Project06/bin/
                    Project07/bin/
                    Project08/bin/
                    and 14 more

                They go to the Recycle Bin.
                """);
    }

    // Git's complaint is passed on, and the other folders are still purged.
    [Test]
    public async Task AFolderGitCannotLookIn()
    {
        using var code = new CodeCommandsTests.Scratch();
        var notARepo = code.At("NotARepo");
        Directory.CreateDirectory(notARepo);
        var repo = code.CreateRepo("Repo");
        Write(repo, "Untracked.txt", "new");

        var outcome = await GitPurge.Run([notARepo, repo], permanent: true, IntPtr.Zero);

        await Verify(outcome)
            .Snapshot(
                """
                {
                  Succeeded: false,
                  Summary: 1 item deleted, git failed in 1 folder,
                  Details:
                NotARepo: FAILED
                  fatal: not a git repository (or any of the parent directories): .git

                Repo: 1 item
                  Untracked.txt
                }
                """);
    }

    // A repository with something of each kind: tracked, untracked, ignored, read-only, an empty
    // folder, a repository nested inside, and a name git quotes unless told not to.
    static string Untidy(CodeCommandsTests.Scratch code, string name)
    {
        var repo = code.CreateRepo(name);
        Write(repo, ".gitignore", "bin/\n*.log\n");
        Write(repo, "src/Tracked.cs", "class Tracked;");
        CodeCommandsTests.Scratch.Git(repo, "add", ".");
        CodeCommandsTests.Scratch.Git(repo, "commit", "-m", "Ignore builds");

        Write(repo, "bin/Debug/Repo.dll", "binary");
        Write(repo, "src/build.log", "log");
        Write(repo, "src/Untracked.cs", "class Untracked;");
        Write(repo, "src/obj/Repo.assets.json", "{}");
        Write(repo, "src/ünï cödé.txt", "unicode");
        Directory.CreateDirectory(Path.Combine(repo, "Empty"));

        var nested = Path.Combine(repo, "Nested");
        Directory.CreateDirectory(nested);
        CodeCommandsTests.Scratch.Git(nested, "init");
        Write(nested, "Untracked.txt", "kept");
        return repo;
    }

    static void Write(string folder, string relative, string content)
    {
        var path = Path.Combine(folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    // Everything under the repository but git's own folders.
    static List<string> Left(string repo) =>
        Directory.EnumerateFileSystemEntries(repo, "*", SearchOption.AllDirectories)
            .Select(_ => Path.GetRelativePath(repo, _).Replace('\\', '/'))
            .Where(_ => !IsGitFolder(_))
            .Order(StringComparer.Ordinal)
            .ToList();

    static bool IsGitFolder(string relative) =>
        relative == ".git" ||
        relative.StartsWith(".git/", StringComparison.Ordinal) ||
        relative.EndsWith("/.git", StringComparison.Ordinal) ||
        relative.Contains("/.git/", StringComparison.Ordinal);
}
