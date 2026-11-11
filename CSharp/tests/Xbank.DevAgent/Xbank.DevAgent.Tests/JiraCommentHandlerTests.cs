using System.Text.Json;
using Xbank.DevAgent.Api;

namespace Xbank.DevAgent.Tests;

public sealed class JiraCommentHandlerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UsesNewHeadingAndReformatsPreviousSignatureWithoutDuplicatingIt(bool scopeChange)
    {
        string formatted = JiraCommentHandler.Format("Product point", scopeChange);
        Assert.StartsWith("Developer Agent Points\n", formatted);
        string previous = formatted.Replace("Developer Agent Points\n", "[MAF-AI:v1]\n", StringComparison.Ordinal);
        Assert.Equal(formatted, JiraCommentHandler.Format(previous, scopeChange));
    }

    [Theory]
    [InlineData("Developer Agent Points\n", "agent")]
    [InlineData("Developer Agent Points\r\n", "agent")]
    [InlineData("[MAF-AI:v1]\n", "agent")]
    [InlineData("[MAF-AI:v1]\r\n", "agent")]
    [InlineData("Other comment\n", "external")]
    public void RecognizesBothCommentSignatures(string heading, string origin)
    {
        string body = heading + "Comment text";
        JsonElement response = JsonSerializer.SerializeToElement(new { comments = new[] { new { body } } });
        JsonElement comment = JiraCommentHandler.Annotate(response).GetProperty("comments")[0];
        Assert.Equal(origin, comment.GetProperty("mafOrigin").GetString());
        Assert.Equal(body, comment.GetProperty("body").GetString());
    }

    [Theory]
    [InlineData(false, 2000, 2000)]
    [InlineData(true, 2000, 2000)]
    [InlineData(false, 2000, 20)]
    [InlineData(true, 20, 2000)]
    public void CapsFinalCommentIncludingSignatureAndBothSections(bool scopeChange, int productLength, int technicalLength)
    {
        string text = $"{JiraCommentHandler.ProductHeading}\ncritical product: {new string('p', productLength)}\n\n{JiraCommentHandler.TechnicalHeading}\ncritical technical: {new string('t', technicalLength)}";
        string formatted = JiraCommentHandler.Format(text, scopeChange);

        Assert.True(formatted.Length <= JiraCommentHandler.MaxCommentLength);
        Assert.StartsWith(JiraCommentHandler.Signature + "\n", formatted);
        Assert.Contains(JiraCommentHandler.ProductHeading + "\ncritical product:", formatted);
        Assert.Contains(JiraCommentHandler.TechnicalHeading + "\ncritical technical:", formatted);
        Assert.Equal(formatted, JiraCommentHandler.Format(formatted, scopeChange));
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReservesSpaceForReporterMentionWithoutTruncatingIt(bool scopeChange)
    {
        const string mention = "[~accountid:abc:123]";
        string text = JiraCommentHandler.ProductHeading + "\n" + new string('p', 2000)
            + "\n\n" + JiraCommentHandler.TechnicalHeading + "\n" + new string('t', 2000);
        string formatted = JiraCommentHandler.Format(text, scopeChange, mention);
        Assert.True(formatted.Length <= JiraCommentHandler.MaxCommentLength);
        Assert.EndsWith("\n\n" + mention, formatted);
        Assert.Equal(formatted, JiraCommentHandler.Format(formatted, scopeChange, mention));
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
