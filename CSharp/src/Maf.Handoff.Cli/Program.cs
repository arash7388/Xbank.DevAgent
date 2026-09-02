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
    HandoffWorkflowRunner runner = new(settings);
    string task = string.Join(' ', args);

    await foreach (RunEvent runEvent in runner.RunAsync(task))
    {
        if (runEvent.Type is "handoff" or "usage" or "run_finished" or "error")
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(runEvent));
        }
        else
        {
            Console.Write(runEvent.Text);
        }
    }

    return 0;
}
catch (ConfigurationException exception)
{
    Console.Error.WriteLine($"Configuration error: {exception.Message}");
    return 2;
}
