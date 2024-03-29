namespace GitHubDigestBuilder.Models;

internal sealed class CommitData
{
	public RepoData? Repo { get; set; }

	public string? Sha { get; set; }

	public string? Subject { get; set; }

	public string? Remarks { get; set; }

	public string Url => $"{Repo!.Url}/commit/{Sha}";
}
