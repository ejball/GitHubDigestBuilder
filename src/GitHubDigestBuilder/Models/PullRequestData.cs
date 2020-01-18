using System.Collections.Generic;

namespace GitHubDigestBuilder.Models
{
	internal sealed class PullRequestData : IssueBaseData
	{
		public BranchData? FromBranch { get; set; }

		public BranchData? ToBranch { get; set; }

		public string Url => $"{Repo!.Report!.WebBase}/{Repo!.Name}/pull/{Number}";
	}
}
