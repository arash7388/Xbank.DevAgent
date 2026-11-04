using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;

namespace XBank.CodeGraph;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return 1; }

        return await Run(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> Run(string[] args)
    {
        var db = Arg(args, "--db") ?? "codegraph.db";

        switch (args[0])
        {
            case "index":
            {
                RegisterMsBuild();
                var solutions = Args(args, "--solution").Concat(Args(args, "-s"))
                    .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (solutions.Count == 0) { Console.Error.WriteLine("--solution الزامی است"); return 1; }

                using var store = new Storage.GraphStore(db);
                var opt = new Indexing.IndexOptions
                {
                    Incremental     = args.Contains("--incremental"),
                    IncludeExternal = args.Contains("--include-external")
                };
                foreach (var sln in solutions)
                    await new Indexing.SolutionIndexer(store, opt).IndexAsync(sln);
                return 0;
            }

            case "query":
            {
                var q = Arg(args, "--q") ?? Arg(args, "--symbol");
                if (q is null) { Console.Error.WriteLine("--q الزامی است"); return 1; }

                using var store = new Storage.GraphStore(db);
                var ropt = new Retrieval.RetrieveOptions
                {
                    TokenBudget = int.TryParse(Arg(args, "--budget"), out var b) ? b : 6000,
                    MaxDistance = double.TryParse(Arg(args, "--depth"), out var d) ? d : 2.2
                };
                Console.WriteLine(new Retrieval.ContextRetriever(store, ropt).BuildContext(q));
                return 0;
            }

            case "search":
            {
                var q = Arg(args, "--q")!;
                using var store = new Storage.GraphStore(db);
                foreach (var n in store.Search(q, 20))
                    Console.WriteLine($"{n.Degree,4}  {n.Kind,-12} {n.Signature}");
                return 0;
            }

            case "serve":
            {
                var port = int.TryParse(Arg(args, "--port"), out var p) ? p : 5099;
                await Serving.HttpServer.RunAsync(db, port);
                return 0;
            }

            case "stats":
            {
                using var store = new Storage.GraphStore(db);
                var (n, e) = store.Stats();
                Console.WriteLine($"گره: {n}\nیال: {e}");
                return 0;
            }

            default: Usage(); return 1;
        }
    }

    private static string? Arg(string[] a, string key)
    {
        var i = Array.IndexOf(a, key);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
    }

    private static IEnumerable<string> Args(string[] args, string key)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == key) yield return args[++i];
    }

    private static void RegisterMsBuild()
    {
        // First try Visual Studio / Build Tools instances
        try
        {
            var vs = MSBuildLocator.RegisterDefaults();
            Console.WriteLine($"MSBuild: {vs.MSBuildPath}");
            return;
        }
        catch (InvalidOperationException) { /* fall through to SDK search */ }

        // Try to query all instances manually
        var instances = MSBuildLocator.QueryVisualStudioInstances().ToList();
        if (instances.Count > 0)
        {
            var inst = instances.OrderByDescending(i => i.Version).First();
            MSBuildLocator.RegisterInstance(inst);
            Console.WriteLine($"MSBuild: {inst.MSBuildPath}");
            return;
        }

        // Fallback: search .NET SDK directories
        var sdkRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet", "sdk");
        if (Directory.Exists(sdkRoot))
        {
            var sdkDirs = Directory.GetDirectories(sdkRoot);
            foreach (var dir in sdkDirs.OrderByDescending(d => d))
            {
                var msbuildDll = Path.Combine(dir, "MSBuild.dll");
                if (File.Exists(msbuildDll))
                {
                    MSBuildLocator.RegisterMSBuildPath(dir);
                    Console.WriteLine($"MSBuild (SDK): {dir}");
                    return;
                }
            }
        }

        Console.Error.WriteLine("No MSBuild instance found. Install Visual Studio Build Tools or .NET SDK.");
        Environment.Exit(1);
    }

    private static void Usage() => Console.WriteLine("""
        codegraph index  --solution <path.sln> [--solution <other.sln> ...] [--db codegraph.db] [--incremental] [--include-external]
        codegraph query  --q "<متن یا نام نماد>" [--db ...] [--budget 6000] [--depth 2.2]
        codegraph search --q "<نام>" [--db ...]
        codegraph serve  [--db ...] [--port 5099]
        codegraph stats  [--db ...]
        """);
}
