using System.Collections.Generic;

namespace GitHubDigestBuilder.Models
{
	internal sealed class ConversationData
	{
		public CommentedCommitData? Commit { get; set; }

		public PullRequestData? PullRequest { get; set; }

		public IssueData? Issue { get; set; }

		public string? FilePath { get; set; }

		public string? Position { get; set; }

		public List<CommentData> Comments { get; } = new List<CommentData>();
	}
}
