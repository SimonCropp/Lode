static partial class ModuleInitializer
{
    [ModuleInitializer]
    public static void Initialize()
    {
        VerifyDiffPlex.Initialize();
        VerifyXaml.Initialize();

        // WPF XamlWriter non-deterministically adds a Baml2006 namespace.
        // Scrub it globally so snapshot tests are stable.
        VerifierSettings.AddScrubber(
            builder =>
            {
                var text = builder.ToString();
                var cleaned = BamlNamespaceRegex().Replace(text, "");
                if (text != cleaned)
                {
                    builder.Clear();
                    builder.Append(cleaned);
                }
            });
    }

    [GeneratedRegex("""
                    \s*xmlns:swb="clr-namespace:System\.Windows\.Baml2006;assembly=PresentationFramework"
                    """)]
    private static partial Regex BamlNamespaceRegex();
}
