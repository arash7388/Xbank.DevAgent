using System.Text.Json;
using Maf.Handoff.Api;

namespace Maf.Handoff.Tests;

public sealed class JiraCommentHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FormatsBothSectionsOnceWithoutMixingTheirContent(bool scopeChange)
    {
        string text = $"## {JiraCommentHandler.ProductHeading}\n- مقدار مورد انتظار تأیید شود.\n\n**{JiraCommentHandler.TechnicalHeading}**\n- پکیج داخلی در دسترس نیست.";
        string formatted = JiraCommentHandler.Format(text, scopeChange);

        Assert.StartsWith(JiraCommentHandler.Signature + "\n", formatted);
        Assert.Contains(JiraCommentHandler.ProductHeading + "\n- مقدار مورد انتظار تأیید شود.", formatted);
        Assert.Contains(JiraCommentHandler.TechnicalHeading + "\n- پکیج داخلی در دسترس نیست.", formatted);
        Assert.Equal(formatted, JiraCommentHandler.Format(formatted, scopeChange));
        Assert.Equal(formatted, JiraCommentHandler.Format(formatted.Replace("\n", "\r\n"), scopeChange));
    }

    [Fact]
    public void TechnicalOnlyCommentDoesNotInventAProductIssue()
    {
        string formatted = JiraCommentHandler.Format(JiraCommentHandler.TechnicalHeading + "\nفید بسته‌ها در دسترس نیست.", false);
        Assert.Contains(JiraCommentHandler.ProductHeading + "\nموردی گزارش نشده است.", formatted);
        Assert.Contains(JiraCommentHandler.TechnicalHeading + "\nفید بسته‌ها در دسترس نیست.", formatted);
    }

    [Fact]
    public void ExcludedCommentsAreNotRestoredByAnnotation()
    {
        JsonElement response = JsonSerializer.SerializeToElement(new { startAt = 0, total = 0, comments = Array.Empty<object>(), commentsExcludedByUser = true });
        JsonElement result = JiraCommentHandler.Annotate(response);
        Assert.True(result.GetProperty("commentsExcludedByUser").GetBoolean());
        Assert.Equal(0, result.GetProperty("comments").GetArrayLength());
        Assert.Equal(0, result.GetProperty("total").GetInt32());
    }
}
