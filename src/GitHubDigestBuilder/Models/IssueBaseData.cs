namespace GitHubDigestBuilder.Models;

internal class IssueBaseData
{
	public RepoData? Repo { get; set; }

	public int Number { get; set; }

	public string? Title { get; set; }

	public string? Body { get; set; }

	public List<EventData> Events { get; } = new();
}
