using System.Text.RegularExpressions;

namespace Xbank.DevAgent.Core;

public static partial class BuildFailureMessages
{
    public const string Marker = "WORKFLOW_BUILD_BLOCKED";
    public const string SkippedNotice = "Build verification was skipped at your request. The workflow can continue, but the proposed changes have not been confirmed to compile.";
    public const string NextStep = "The proposed changes are preserved but have not been approved. Fix the build environment, then start a new run.";

    // Only known restore/environment diagnostics stop remediation. Compiler errors
    // continue to the developer; an unavailable build never counts as approval.
    public static string? DescribeEnvironmentFailure(string evidence)
    {
        Match package = MissingPackage().Match(evidence);
        if (package.Success)
            return $"Build verification could not finish because NuGet could not restore package '{package.Groups[1].Value.Trim()}'. " +
                "Configure a package source containing this package and any required credentials. " + NextStep;
        if (RestoreSourceFailure().IsMatch(evidence))
            return "Build verification could not access a required NuGet package source. Check the feed URL, network access, and credentials. " + NextStep;
        if (SdkFailure().IsMatch(evidence))
            return "Build verification could not use the required .NET SDK. Install or configure the SDK required by the repository. " + NextStep;
        return null;
    }

    [GeneratedRegex(@"\berror NU110[012]\s*:\s*Unable to find package\s+([A-Za-z0-9_.-]+?)(?:\.\s|\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex MissingPackage();

    [GeneratedRegex(@"\berror NU130[12]\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex RestoreSourceFailure();

    [GeneratedRegex(@"\berror (?:NETSDK1045|MSB4236)\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex SdkFailure();
}
