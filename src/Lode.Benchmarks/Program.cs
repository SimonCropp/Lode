// In process: BenchmarkDotNet 0.15 doesn't know .NET 11, so can't build the separate process it
// would otherwise run each benchmark in.
if (!args.Contains("--inProcess", StringComparer.OrdinalIgnoreCase))
{
    args = [.. args, "--inProcess"];
}

BenchmarkSwitcher.FromAssembly(typeof(SearchMergeBenchmarks).Assembly).Run(args);
