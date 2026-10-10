using System.Text.RegularExpressions;

namespace Maf.Handoff.Api;

public static class TaskBranchName
{
    public static string FromTask(string? jiraIssueKey, string task)
    {
        if (!string.IsNullOrWhiteSpace(jiraIssueKey))
        {
            Match key = Regex.Match(jiraIssueKey.Trim(), @"^WEPOD-([0-9]+)$", RegexOptions.IgnoreCase);
            if (!key.Success)
                throw new ArgumentException("The selected Jira task must have a WEPOD task number, for example WEPOD-12345.");
            return $"user/WEPOD-{key.Groups[1].Value}";
        }

        MatchCollection keys = Regex.Matches(task, @"\bWEPOD-([0-9]+)\b", RegexOptions.IgnoreCase);
        IEnumerable<string> numbers = keys.Select(match => match.Groups[1].Value);
        if (keys.Count == 0)
        {
            numbers = Regex.Matches(task,
                @"\btask\s*(?:(?:number|id)\s*)?[:#-]?\s*([0-9]+)\b", RegexOptions.IgnoreCase)
                .Select(match => match.Groups[1].Value);
        }

        string[] distinct = numbers.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0)
            throw new ArgumentException("Task number is required. Select a Jira task or include WEPOD-12345 or task number 12345 in the description.");
        if (distinct.Length > 1)
            throw new ArgumentException("Multiple task numbers were found. Select the intended Jira task or include only one task number in the description.");
        return $"user/WEPOD-{distinct[0]}";
    }

    public static string ForApproval(string? jiraIssueKey, string task, string? requestedBranch)
    {
        return string.IsNullOrWhiteSpace(requestedBranch)
            ? FromTask(jiraIssueKey, task)
            : requestedBranch.Trim();
    }
}
