namespace GitHubDigestBuilder.Models;

internal sealed class IssueData : IssueBaseData
{
	public string Url => $"{Repo!.Url}/issues/{Number}";
}
