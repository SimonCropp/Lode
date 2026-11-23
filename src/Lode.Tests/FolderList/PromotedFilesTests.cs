public class PromotedFilesTests
{
    [Test]
    public Task Matching()
    {
        (string Pattern, string Name)[] cases =
        [
            ("*.slnx", "Lode.slnx"),
            ("*.slnx", "Lode.sln"),
            ("*.slnx", "LODE.SLNX"),
            ("Directory.*", "Directory.Build.props"),
            ("Directory.*", "Directory"),
            ("Directory.*.props", "Directory.Packages.props"),
            ("Directory.*.props", "Directory.props"),
            ("Directory.*.props", "Directory.Build.targets"),
            ("Directory.Build.props", "directory.build.props"),
            ("Directory.Build.props", "Directory.Build.props.user"),
            ("  *.slnx  ", "Lode.slnx"),
            ("*", "anything"),
            ("*Build*", "Directory.Build.props"),
            ("", "Lode.slnx")
        ];
        return Verify(cases.Select(_ => $"'{_.Pattern}' {_.Name}: {PromotedFiles.Parse([_.Pattern]).Matches(_.Name)}"))
            .Snapshot(
                """
                [
                  '*.slnx' Lode.slnx: True,
                  '*.slnx' Lode.sln: False,
                  '*.slnx' LODE.SLNX: True,
                  'Directory.*' Directory.Build.props: True,
                  'Directory.*' Directory: False,
                  'Directory.*.props' Directory.Packages.props: True,
                  'Directory.*.props' Directory.props: False,
                  'Directory.*.props' Directory.Build.targets: False,
                  'Directory.Build.props' directory.build.props: True,
                  'Directory.Build.props' Directory.Build.props.user: False,
                  '  *.slnx  ' Lode.slnx: True,
                  '*' anything: True,
                  '*Build*' Directory.Build.props: False,
                  '' Lode.slnx: False
                ]
                """);
    }

    [Test]
    public async Task NoPatternsIsEmpty()
    {
        await Assert.That(PromotedFiles.Parse([]).IsEmpty).IsTrue();
        await Assert.That(PromotedFiles.Parse(["", "  ", "*a*b"]).IsEmpty).IsTrue();
        await Assert.That(PromotedFiles.Parse(["*.slnx"]).IsEmpty).IsFalse();
    }

    // Only a file in a folder of the root's own, that a pattern names, is a change to what the
    // root promotes. The paths are written as they are: where the tests are checked out at
    // C:\Code\Lode, Verify would write some as {SolutionDirectory}, and elsewhere not.
    [Test]
    public Task Promotes()
    {
        var patterns = PromotedFiles.Parse(["*.slnx"]);
        string[] paths =
        [
            @"C:\Code\Lode\src\Lode.slnx",
            @"C:\Code\Lode\src\Lode.csproj",
            @"C:\Code\Lode\Lode.slnx",
            @"C:\Code\Lode\src\Lode\Lode.slnx",
            @"C:\Code\Other\src\Lode.slnx"
        ];
        return Verify(
            new
            {
                fromAFolder = paths.Select(_ => $"{_}: {patterns.Promotes(@"C:\Code\Lode", _)}"),
                fromADrive = patterns.Promotes(@"D:\", @"D:\src\Lode.slnx")
            })
            .DisableScrubbers()
            .Snapshot(
                """
                {
                  fromAFolder: [
                    C:\Code\Lode\src\Lode.slnx: True,
                    C:\Code\Lode\src\Lode.csproj: False,
                    C:\Code\Lode\Lode.slnx: False,
                    C:\Code\Lode\src\Lode\Lode.slnx: False,
                    C:\Code\Other\src\Lode.slnx: False
                  ],
                  fromADrive: true
                }
                """);
    }
}
