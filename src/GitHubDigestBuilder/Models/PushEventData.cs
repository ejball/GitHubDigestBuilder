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

		public List<CommitData> NewCommits { get; } = new();

		public bool CanMerge { get; set; }

		public string Url => CommitCount == 1 ? $"{Repo!.Url}/commit/{AfterSha}" : $"{Repo!.Url}/compare/{BeforeSha}...{AfterSha}";
	}
}
