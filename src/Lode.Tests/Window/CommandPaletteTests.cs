public class CommandPaletteTests
{
    static readonly CommandPalette.Command[] commands =
    [
        new("Back", () => { }),
        new("New tab", () => { }),
        new("Close tab", () => { }),
        new("Close tabs to the right", () => { }),
        new("Stable build", () => { }),
        new("Reopen closed tab", () => { })
    ];

    [Test]
    public Task Match() =>
        Verify(
            new
            {
                empty = Names(""),
                tab = Names("tab"),
                closeRight = Names("close right"),
                caseIgnored = Names("NEW"),
                nothing = Names("zzz")
            });

    static IEnumerable<string> Names(string search) =>
        CommandPalette.Match(commands, search).Select(_ => _.Name);
}
