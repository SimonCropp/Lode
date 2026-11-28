/// <summary>
/// The colours of a diff's code, over a file of 100,000 lines, made of a block of code as it is
/// written, comments, strings and all, again and again. States is what is done off the UI thread
/// as a diff is laid out, given the file's lines: the state each of them starts in, gone through
/// from the first. Runs is every line's runs worked out, as drawing every line would; drawing
/// works out only the rows showing, and keeps them. Paint draws a screen of the diff of the C#
/// file added, half way down, with its colours and without, each line's runs already worked out
/// as they are once the screen has been drawn; painted to a bitmap, as the canvas is never shown.
/// </summary>
[MemoryDiagnoser]
public class SyntaxBenchmarks
{
    const int lineCount = 100_000;

    [Params("Greeter.cs", "app.ts", "MainWindow.xaml", "build.py")]
    public string File { get; set; } = "Greeter.cs";

    SyntaxLanguage language = null!;
    string[] lines = [];
    List<SyntaxRun> runs = [];

    [GlobalSetup]
    public void Setup()
    {
        language = SyntaxLanguage.For(File)!;
        lines = Lines(Block(File));
    }

    [Benchmark]
    public int States() =>
        DiffSyntax.StatesOf(language, lines)[^1];

    [Benchmark]
    public int Runs()
    {
        var state = 0;
        var count = 0;
        foreach (var line in lines)
        {
            runs.Clear();
            state = language.Tokenize(line, state, runs);
            count += runs.Count;
        }

        return count;
    }

    // The block, again and again, to as many lines as the file has.
    internal static string[] Lines(string[] block) =>
        Enumerable.Range(0, lineCount)
            .Select(_ => block[_ % block.Length])
            .ToArray();

    internal static string[] Block(string file) =>
        Path.GetExtension(file) switch
        {
            ".cs" =>
            [
                "/// <summary>",
                "/// Says hello, as many times as asked.",
                "/// </summary>",
                "sealed class Greeter(string name) : IGreeter",
                "{",
                "    const int times = 3;",
                "    static readonly string path = @\"C:\\Greetings\\",
                "hello.txt\";",
                "",
                "    /* The greeting, its name quoted:",
                "       \"Ada\" says hello. */",
                "    public async Task<string> Greet(CancellationToken cancel)",
                "    {",
                "        var builder = new StringBuilder();",
                "        for (var index = 0; index < times; index++)",
                "        {",
                "            builder.Append($\"\\\"{name}\\\" says hello\\t#{index + 1}\\n\");",
                "            await Task.Delay(1_000, cancel).ConfigureAwait(false); // wait a second",
                "        }",
                "",
                "        return builder.Length > 0 ? builder.ToString() : null ?? 'x'.ToString();",
                "    }",
                "}"
            ],
            ".ts" =>
            [
                "import { Greeter } from './greeter';",
                "",
                "/**",
                " * Says hello, as many times as asked.",
                " */",
                "export interface Options { name: string; times?: number }",
                "",
                "export async function greet({ name, times = 3 }: Options): Promise<string> {",
                "    const pattern = /^[A-Z][a-z]+$/g; // a proper name",
                "    let said = '';",
                "    for (let index = 0; index < times; index++) {",
                "        said += `\"${name}\" says hello #${index + 1}",
                "`;",
                "        await new Promise(resolve => setTimeout(resolve, 1000 / times));",
                "    }",
                "",
                "    return pattern.test(name) ? said : undefined ?? null;",
                "}"
            ],
            ".xaml" =>
            [
                "<Window x:Class=\"Greeter.MainWindow\"",
                "        xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"",
                "        xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"",
                "        Title=\"Greeter\" Height=\"450\" Width=\"800\">",
                "    <!-- The greeting, and the button",
                "         that says it. -->",
                "    <Grid Margin=\"8\">",
                "        <TextBlock Text=\"{Binding Greeting}\" FontSize=\"14\" />",
                "        <Button Content=\"Say hello &amp; wave\"",
                "                Click=\"OnClick\"",
                "                HorizontalAlignment=\"Right\" />",
                "    </Grid>",
                "</Window>"
            ],
            _ =>
            [
                "import asyncio",
                "",
                "",
                "class Greeter:",
                "    \"\"\"Says hello,",
                "    as many times as asked.\"\"\"",
                "",
                "    def __init__(self, name: str, times: int = 3) -> None:",
                "        self.name = name  # who to greet",
                "        self.times = times",
                "",
                "    @property",
                "    async def greeting(self) -> str | None:",
                "        said = [f'\"{self.name}\" says hello #{index + 1}\\n' for index in range(self.times)]",
                "        await asyncio.sleep(1.5e-1)",
                "        return ''.join(said) if said is not None else None"
            ]
        };
}

/// <summary>
/// A screen of the diff of a C# file of 100,000 lines added, half way down, painted with its
/// colours and without them, as it is painted again for every step it scrolls: each line's runs
/// already worked out, as they are, kept, once the screen has been drawn.
/// </summary>
[MemoryDiagnoser]
public class SyntaxPaintBenchmarks
{
    [Params(false, true)]
    public bool Coloured { get; set; }

    DiffCanvas canvas = null!;
    Bitmap bitmap = null!;

    [GlobalSetup]
    public void Setup()
    {
        var lines = SyntaxBenchmarks.Lines(SyntaxBenchmarks.Block("Greeter.cs"));
        var patch = new StringBuilder($"--- /dev/null\n+++ b/Greeter.cs\n@@ -0,0 +1,{lines.Length} @@\n");
        foreach (var line in lines)
        {
            patch.Append('+').Append(line).Append('\n');
        }

        var diff = (TextDiff) DiffParser.Parse(patch.ToString(), evenIfLarge: true);
        var layout = DiffLayout.Of(diff, lines, split: false);
        if (Coloured)
        {
            layout = layout with
            {
                Syntax = DiffSyntax.Of("Greeter.cs", layout.Hunks, lines)
            };
        }

        canvas = new()
        {
            Size = new(1000, 800)
        };
        canvas.CreateControl();
        canvas.Show(layout, sideBySide: false, null, null, keepPlace: false);
        canvas.TopRow = lines.Length / 2;
        bitmap = new(canvas.Width, canvas.Height);
        Paint();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        bitmap.Dispose();
        canvas.Dispose();
    }

    [Benchmark]
    public void Paint() =>
        canvas.DrawToBitmap(bitmap, new(0, 0, canvas.Width, canvas.Height));
}
