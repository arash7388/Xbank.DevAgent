using Maf.Handoff.Core;

namespace Maf.Handoff.Tests;

public sealed class ModelProviderDefinitionTests
{
    [Fact]
    public void NormalizesMarkdownChatCompletionsUrlAndMaxReasoning()
    {
        ModelProviderDefinition provider = DeepSeekProvider(
            "[https://api.deepseek.com/chat/completions](https://api.deepseek.com/chat/completions)");

        ModelProviderDefinition normalized = provider.NormalizeAndValidate();

        Assert.Equal("https://api.deepseek.com", normalized.Url);
        Assert.Equal("max", normalized.ReasoningEffort);
        Assert.Contains("max", normalized.SupportsReasoningEffort!);
    }

    [Fact]
    public void PreservesVersionedBasePath()
    {
        ModelProviderDefinition provider = DeepSeekProvider("https://example.test/v1/chat/completions/");

        Assert.Equal("https://example.test/v1", provider.NormalizeAndValidate().Url);
    }

    [Fact]
    public void RejectsProviderWithoutToolCalling()
    {
        ModelProviderDefinition provider = DeepSeekProvider("https://api.deepseek.com") with
        {
            ToolCalling = false,
        };

        ConfigurationException exception = Assert.Throws<ConfigurationException>(provider.NormalizeAndValidate);

        Assert.Contains("tool calling", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectsUnlistedReasoningEffort()
    {
        ModelProviderDefinition provider = DeepSeekProvider("https://api.deepseek.com") with
        {
            ReasoningEffort = "medium",
            SupportsReasoningEffort = ["high", "max"],
        };

        ConfigurationException exception = Assert.Throws<ConfigurationException>(provider.NormalizeAndValidate);

        Assert.Contains("supportsReasoningEffort", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsNegativeTokenPricing()
    {
        ModelProviderDefinition provider = DeepSeekProvider("https://api.deepseek.com") with
        {
            InputCostPerMillionTokens = -0.01m,
        };

        ConfigurationException exception = Assert.Throws<ConfigurationException>(provider.NormalizeAndValidate);

        Assert.Contains("InputCostPerMillionTokens", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://api.deepseek.com")]
    [InlineData("https://user:secret@api.deepseek.com")]
    [InlineData("https://api.deepseek.com?key=secret")]
    public void RejectsUnsafeEndpoint(string url)
    {
        Assert.Throws<ConfigurationException>(() => DeepSeekProvider(url).NormalizeAndValidate());
    }

    private static ModelProviderDefinition DeepSeekProvider(string url) => new(
        "deepseek-v4-flash",
        "deepseek-v4-flash",
        url,
        ToolCalling: true,
        Vision: true,
        MaxInputTokens: 128_000,
        MaxOutputTokens: 160_000,
        Thinking: true,
        ReasoningEffort: "max",
        SupportsReasoningEffort: ["low", "medium", "high", "max"]);
}
