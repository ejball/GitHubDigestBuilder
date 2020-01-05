using System.Collections.Generic;

namespace GitHubDigestBuilder.Models
{
	internal sealed class PushEventData : EventData
	{
		public BranchData? Branch { get; set; }

		public PullRequestData? PullRequest { get; set; }

		public string? BeforeSha { get; set; }

		public string? AfterSha { get; set; }

		public int CommitCount { get; set; }

		public int NewCommitCount { get; set; }

		public List<CommitData> NewCommits { get; } = new List<CommitData>();

		public bool CanMerge { get; set; }

		public string Url => CommitCount == 1 ? $"{Repo!.Report!.WebBase}/{Repo!.Name}/commit/{AfterSha}" : $"{Repo!.Report!.WebBase}/{Repo!.Name}/compare/{BeforeSha}...{AfterSha}";
	}
}
