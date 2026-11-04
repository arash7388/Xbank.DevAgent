using System.Reflection;
using Xbank.DevAgent.Api;
using Microsoft.Extensions.Configuration;

namespace Xbank.DevAgent.Tests;

public sealed class CodeGraphOutputTests
{
    [Fact]
    public async Task CapturesUnicodeFromWindowlessIndexerAndItsChildOnBothStreams()
    {
        string root = Path.Combine(Path.GetTempPath(), "maf-codegraph-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // No explicit encoding in this stand-in indexer: the launcher must configure it.
            await File.WriteAllTextAsync(Path.Combine(root, "Probe.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), """
                using System;
                using System.Diagnostics;
                using System.Reflection;
                Console.WriteLine("stdout: در حال بارگذاری ...");
                Console.Error.WriteLine("stderr: 100 فایل ...");
                Console.WriteLine("encoding: " + Console.OutputEncoding.CodePage);
                if (args.Length == 0)
                {
                    var start = new ProcessStartInfo("dotnet")
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8
                    };
                    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                    start.ArgumentList.Add("child");
                    using var child = Process.Start(start);
                    var stdout = child.StandardOutput.ReadToEndAsync();
                    var stderr = child.StandardError.ReadToEndAsync();
                    await child.WaitForExitAsync();
                    Console.Write(await stdout);
                    Console.Error.Write(await stderr);
                    return child.ExitCode;
                }
                return 0;
                """);

            var service = new CodeGraphService(new ConfigurationBuilder().Build());
            var run = typeof(CodeGraphService).GetMethod("RunProcessAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var result = (Task<int>)run.Invoke(service,
                [root, new string[] { "run", "--project", Path.Combine(root, "Probe.csproj"), "--no-launch-profile" }])!;

            Assert.Equal(0, await result.WaitAsync(TimeSpan.FromMinutes(2)));
            var log = service.GetIndexStatus().Log;
            string output = string.Join(Environment.NewLine, log);
            Assert.True(log.Count(line => line == "stdout: در حال بارگذاری ...") == 2, output);
            Assert.True(log.Count(line => line == "stderr: 100 فایل ...") == 2, output);
            Assert.True(log.Count(line => line == "encoding: 65001") == 2, output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
