using System;
using System.Collections.Generic;

namespace GitHubDigestBuilder.Models
{
	internal sealed class RepoData
	{
		public string? Name { get; set; }

		public string? WebBase { get; set; }

		public List<RepoEventData> RepoEvents { get; } = new();

		public List<PullRequestData> PullRequests { get; } = new();

		public List<BranchData> Branches { get; } = new();

		public List<CommentedCommitData> CommentedCommits { get; } = new();

		public List<TagEventData> TagEvents { get; } = new();

		public List<WikiEventData> WikiEvents { get; } = new();

		public List<IssueData> Issues { get; } = new();

		public string? Org => Name?[..Name.IndexOf('/', StringComparison.Ordinal)];

		public string Url => $"{WebBase}/{Name}";
	}
}
