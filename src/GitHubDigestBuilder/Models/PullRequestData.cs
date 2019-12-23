using System.Collections.Generic;

namespace GitHubDigestBuilder.Models
{
	internal sealed class PullRequestData
	{
		public RepoData Repo { get; set; }

		public int Number { get; set; }

		public string Title { get; set; }

		public string Body { get; set; }

		public BranchData FromBranch { get; set; }

		public BranchData ToBranch { get; set; }

		public List<EventData> Events { get; } = new List<EventData>();

		public string Url => $"{Repo.Report.WebBase}/{Repo.Name}/pull/{Number}";
	}
}
