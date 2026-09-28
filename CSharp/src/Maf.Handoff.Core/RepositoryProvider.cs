namespace Maf.Handoff.Core;

public enum RepositoryProvider
{
    GitHub,
    GitLab,
}

public static class RepositoryProviderExtensions
{
    public static string DisplayName(this RepositoryProvider provider) => provider switch
    {
        RepositoryProvider.GitHub => "GitHub",
        RepositoryProvider.GitLab => "GitLab",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };
}
