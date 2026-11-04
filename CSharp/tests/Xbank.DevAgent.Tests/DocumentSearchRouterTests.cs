using Xbank.DevAgent.Core;

namespace Xbank.DevAgent.Tests;

public sealed class DocumentSearchRouterTests
{
    [Theory]
    [InlineData("\"must use managed identity\"")]
    [InlineData("src/Services/SearchRouter.cs")]
    [InlineData("INC-1234")]
    [InlineData("9fceb02")]
    [InlineData("CreateCollectionAsync()")]
    public void RoutesPreciseLookupsToExactSearch(string query)
    {
        DocumentSearchRoute route = DocumentSearchRouter.Route(query);

        Assert.Equal(DocumentSearchMethod.Exact, route.Method);
    }

    [Theory]
    [InlineData("How are expired collections cleaned up?")]
    [InlineData("What is the recommended approach for temporary document storage?")]
    [InlineData("explain the authentication design")]
    public void RoutesNaturalLanguageQuestionsToSemanticSearch(string query)
    {
        DocumentSearchRoute route = DocumentSearchRouter.Route(query);

        Assert.Equal(DocumentSearchMethod.Semantic, route.Method);
    }
}
