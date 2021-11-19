namespace GitHubDigestBuilder.Models;

internal abstract class EventData
{
	public string? Kind { get; set; }

	public RepoData? Repo { get; set; }

	public UserData? Actor { get; set; }
}
