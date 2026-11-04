using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xbank.DevAgent.Api;

namespace Xbank.DevAgent.Tests;

public sealed class CodeGraphConfigurationTests
{
    [Fact]
    public void DefaultIndexerResolvesToBundledProject()
    {
        var service = new CodeGraphService(new ConfigurationBuilder().Build());
        JsonElement configuration = JsonSerializer.SerializeToElement(service.GetConfiguration());

        string project = configuration.GetProperty("indexerProject").GetString()!;
        Assert.True(File.Exists(project), $"Bundled indexer was not found: {project}");
        Assert.EndsWith(Path.Combine("src", "Xbank.CodeGraph", "XBank.CodeGraph", "XBank.CodeGraph.csproj"), project);
    }

    [Fact]
    public void DefaultDatabaseResolvesUnderApiContentRoot()
    {
        var environment = new TestEnvironment();
        var service = new CodeGraphService(new ConfigurationBuilder().Build(), environment);
        JsonElement configuration = JsonSerializer.SerializeToElement(service.GetConfiguration());

        Assert.Equal(Path.Combine(environment.ContentRootPath, "data", "codegraph.db"),
            configuration.GetProperty("defaultDatabasePath").GetString());
    }

    [Fact]
    public void ConfiguredPathsResolveRelativeToContentRoot()
    {
        var environment = new TestEnvironment();
        IConfiguration settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CodeGraph:RootPath"] = "custom-indexer",
            ["CodeGraph:DatabasePath"] = "custom.db"
        }).Build();
        var service = new CodeGraphService(settings, environment);
        JsonElement configuration = JsonSerializer.SerializeToElement(service.GetConfiguration());

        Assert.Equal(Path.Combine(environment.ContentRootPath, "custom-indexer"),
            configuration.GetProperty("rootPath").GetString());
        Assert.Equal(Path.Combine(environment.ContentRootPath, "custom.db"),
            configuration.GetProperty("defaultDatabasePath").GetString());
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Xbank.DevAgent.Api";
        public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "codegraph-config-tests");
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
