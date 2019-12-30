using System.Collections.Generic;

namespace GitHubDigestBuilder.Models
{
	internal sealed class RepoData
	{
		public ReportData? Report { get; set; }

		public string? Name { get; set; }

		public List<PullRequestData> PullRequests { get; } = new List<PullRequestData>();

		public List<BranchData> Branches { get; } = new List<BranchData>();

		public List<CommentedCommitData> CommentedCommits { get; } = new List<CommentedCommitData>();

		public List<TagEventData> TagEvents { get; } = new List<TagEventData>();

		public List<WikiEventData> WikiEvents { get; } = new List<WikiEventData>();

		public List<IssueData> Issues { get; } = new List<IssueData>();

		public string Url => $"{Report!.WebBase}/{Name}";
	}
}
