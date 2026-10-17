using System.Text.Json;
using System.Text.RegularExpressions;
using Maf.Handoff.Core;

namespace Maf.Handoff.Api;

public sealed partial class WorkflowJiraContext(
    IJiraIssueSearch reads, JiraService writer, string? issueKey, Action<RunEvent> notify,
    IWorkflowUserInput? userInput = null) : IJiraIssueSearch, IJiraProblemReporter, IJiraScopeChangeReporter, IWorkflowUserInput
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, JiraProblemReport> _results = new(StringComparer.Ordinal);

    public bool IsAvailable => userInput?.IsAvailable is true;
    public Task<string> AskAsync(string questions, CancellationToken cancellationToken = default) =>
        userInput?.AskAsync(questions, cancellationToken) ?? throw new InvalidOperationException("Interactive clarification is unavailable.");

    public Task<JiraProblemReport> ReportScopeChangeAsync(string changesInPersian, CancellationToken cancellationToken = default) =>
        ReportAsync(changesInPersian, true, cancellationToken);

    public Task<JiraProblemReport> ReportProblemAsync(string problemInPersian, CancellationToken cancellationToken = default) =>
        ReportAsync(problemInPersian, false, cancellationToken);

    private async Task<JiraProblemReport> ReportAsync(string problemInPersian, bool scopeChange, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // A provider retry, repeated tool call, or final-assessment fallback must not duplicate a write.
            string problem = problemInPersian?.Trim() ?? "";
            string reportKey = scopeChange ? "scope:" + problem : "problem";
            if (_results.TryGetValue(reportKey, out JiraProblemReport? cached)) return cached;
            if (problem.Length is 0 or > 12000 || !PersianLetter().IsMatch(problem))
                return Publish(new(issueKey, "failed", "متن گزارش باید به فارسی و بین ۱ تا ۱۲۰۰۰ نویسه باشد."), scopeChange);
            if (issueKey is null)
            {
                JiraProblemReport missing = new(null, "not_posted", "نظر ثبت نشد؛ تسک مرتبط مشخص نیست. لطفاً تسک Jira را انتخاب کنید.\n\n" + problem);
                _results[reportKey] = missing;
                return Publish(missing, scopeChange);
            }

            string body = JiraCommentHandler.Format(problem, scopeChange);
            string? url = null;
            JiraProblemReport report;
            try
            {
                url = writer.GetIssueUrl(issueKey);
                JsonElement comment = await writer.PostCommentAsync(issueKey, body, cancellationToken);
                string? id = comment.TryGetProperty("id", out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                string title = scopeChange ? "گزارش تغییر دامنهٔ محصول" : "گزارش محصول و ملاحظات فنی";
                report = new(issueKey, "posted", $"{title} به‌صورت نظر فارسی در تسک {issueKey} ثبت شد.\n\n{problem}", id, writer.GetIssueUrl(issueKey, id));
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or OperationCanceledException or JsonException)
            {
                // Network interruption can happen after Jira accepted the comment. Never retry an uncertain POST.
                string message = exception is HttpRequestException { StatusCode: not null } http
                    ? $"ثبت نظر در Jira ناموفق بود (HTTP {(int)http.StatusCode.Value}). گزارش مشکل در این صفحه محفوظ است."
                    : "ثبت نظر در Jira تأیید نشد؛ ممکن است درخواست به سرور رسیده باشد. برای جلوگیری از نظر تکراری، ابتدا تسک را بررسی کنید.";
                report = new(issueKey, "failed", message + "\n\n" + problem, Url: url);
                _results[reportKey] = report;
                Publish(report, scopeChange);
                cancellationToken.ThrowIfCancellationRequested();
                return report;
            }
            _results[reportKey] = report;
            return Publish(report, scopeChange);
        }
        finally { _gate.Release(); }
    }

    private JiraProblemReport Publish(JiraProblemReport report, bool scopeChange = false)
    {
        notify(scopeChange
            ? RunEvent.JiraScopeChange(report.IssueKey, report.Message, report.Status, report.Url)
            : RunEvent.JiraProblem(report.IssueKey, report.Message, report.Status, report.Url));
        return report;
    }

    // Prefer the explicit UI selection. A manual request may identify one unambiguous full issue key.
    public static string? ResolveIssueKey(string? selectedKey, string task)
    {
        if (!string.IsNullOrWhiteSpace(selectedKey)) return selectedKey;
        string[] keys = IssueKeyPattern().Matches(task).Select(match => match.Value.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal).ToArray();
        return keys.Length == 1 ? keys[0] : null;
    }

    [GeneratedRegex(@"[\u0621-\u063A\u0641-\u064A\u067E\u0686\u0698\u06A9\u06AF\u06CC]")]
    private static partial Regex PersianLetter();
    [GeneratedRegex(@"\b[A-Za-z][A-Za-z0-9_]*-\d+\b")]
    private static partial Regex IssueKeyPattern();

    public async Task<JsonElement> GetIssueDetailsAsync(string key, CancellationToken cancellationToken = default) =>
        JiraCommentHandler.Annotate(await reads.GetIssueDetailsAsync(key, cancellationToken));
    public async Task<JsonElement> GetIssueCommentsAsync(string key, int startAt = 0, int maxResults = 50, CancellationToken cancellationToken = default) =>
        JiraCommentHandler.Annotate(await reads.GetIssueCommentsAsync(key, startAt, maxResults, cancellationToken));
    public Task<IReadOnlyList<JiraAttachmentInfo>> GetAttachmentsAsync(string key, CancellationToken cancellationToken = default) => reads.GetAttachmentsAsync(key, cancellationToken);
    public Task<JiraAttachmentText> ReadAttachmentAsync(string key, string id, int offset = 0, CancellationToken cancellationToken = default) => reads.ReadAttachmentAsync(key, id, offset, cancellationToken);
    public Task<IReadOnlyList<JiraIssueInfo>> SearchIssuesAsync(string jql, int maxResults = 10, CancellationToken cancellationToken = default) => reads.SearchIssuesAsync(jql, maxResults, cancellationToken);
    public Task<string?> GetIssueTitleByKeyAsync(string key, CancellationToken cancellationToken = default) => reads.GetIssueTitleByKeyAsync(key, cancellationToken);
}
