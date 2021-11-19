namespace GitHubDigestBuilder.Settings;

public sealed class GitHubSettings
{
	public string? Enterprise { get; set; }

	public string? AuthToken { get; set; }

	public string? AuthTokenEnv { get; set; }

	public List<RepoSettings>? Repos { get; set; }

	public List<UserSettings>? Users { get; set; }

	public List<FilterSettings>? Excludes { get; set; }
}
