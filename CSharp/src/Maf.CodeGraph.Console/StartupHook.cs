using System.Text;

// The runtime invokes this hook before the CLI or indexer can initialize its writers.
// Keep this assembly independent of the API and its dependencies.
internal static class StartupHook
{
    public static void Initialize() => Console.OutputEncoding = new UTF8Encoding(false);
}

namespace Maf.CodeGraph.ConsoleSupport
{
    public static class Utf8ConsoleHook
    {
        public static string AssemblyPath => typeof(Utf8ConsoleHook).Assembly.Location;
    }
}
