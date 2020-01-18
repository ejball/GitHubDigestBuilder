namespace GitHubDigestBuilder.Models
{
	internal sealed class IssueData : IssueBaseData
	{
		public string Url => $"{Repo!.Report!.WebBase}/{Repo!.Name}/issues/{Number}";
	}
}
