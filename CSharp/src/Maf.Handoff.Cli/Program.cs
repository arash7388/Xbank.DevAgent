using System.Text.Json;
using Maf.Handoff.Core;

if (args.Length == 0 || args is ["--help"] or ["-h"])
{
    Console.WriteLine("Usage: dotnet run --project src/Maf.Handoff.Cli -- \"TASK\"");
    return args.Length == 0 ? 2 : 0;
}

try
{
    WorkflowSettings settings = WorkflowSettings.FromEnvironment();
    settings.Validate();
    using HttpClient connectivityClient = new();
    await new RunConnectivityCheck(connectivityClient).CheckAsync([settings.ModelEndpoint]);
    HandoffWorkflowRunner runner = new(settings);
    string task = string.Join(' ', args);
    Dictionary<string, RunEvent> summaries = new(StringComparer.Ordinal);

    await foreach (RunEvent runEvent in runner.RunAsync(task))
    {
        if (runEvent.Type == "agent_summary" && runEvent.Agent is string agent)
        {
            summaries[agent] = runEvent;
        }
        else if (runEvent.Type is "handoff" or "usage" or "run_finished" or "error")
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(runEvent));
        }
        else
        {
            Console.Write(runEvent.Text);
        }
    }

    Console.WriteLine();
    Console.WriteLine("Agent summaries:");
    foreach (RunEvent summary in summaries.Values)
        Console.WriteLine($"{summary.Agent}: {summary.Text}");

    return 0;
}
catch (ConfigurationException exception)
{
    Console.Error.WriteLine($"Configuration error: {exception.Message}");
    return 2;
}
catch (RunConnectivityException exception)
{
    Console.Error.WriteLine($"Cannot start the run: {exception.Message}");
    return 2;
}
