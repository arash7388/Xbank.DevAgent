using System.Text.RegularExpressions;

namespace Xbank.DevAgent.Api;

public static class TaskBranchName
{
    public static string FromTask(string? jiraIssueKey, string task)
    {
        if (!string.IsNullOrWhiteSpace(jiraIssueKey))
        {
            Match key = Regex.Match(jiraIssueKey.Trim(), @"^[A-Za-z][A-Za-z0-9_]*-[0-9]+$");
            if (!key.Success)
                throw new ArgumentException("The selected Jira task must have a valid issue key, for example PROJECT-12345.");
            return $"user/{key.Value.ToUpperInvariant()}";
        }

        MatchCollection keys = Regex.Matches(task, @"\b[A-Za-z][A-Za-z0-9_]*-[0-9]+\b");
        IEnumerable<string> numbers = keys.Select(match => match.Value.ToUpperInvariant());
        if (keys.Count == 0)
        {
            numbers = Regex.Matches(task,
                @"\btask\s*(?:(?:number|id)\s*)?[:#-]?\s*([0-9]+)\b", RegexOptions.IgnoreCase)
                .Select(match => $"TASK-{match.Groups[1].Value}");
        }

        string[] distinct = numbers.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0)
            throw new ArgumentException("Task number is required. Select a Jira task or include PROJECT-12345 or task number 12345 in the description.");
        if (distinct.Length > 1)
            throw new ArgumentException("Multiple task numbers were found. Select the intended Jira task or include only one task number in the description.");
        return $"user/{distinct[0]}";
    }

    public static string ForApproval(string? jiraIssueKey, string task, string? requestedBranch)
    {
        return string.IsNullOrWhiteSpace(requestedBranch)
            ? FromTask(jiraIssueKey, task)
            : requestedBranch.Trim();
    }
}
