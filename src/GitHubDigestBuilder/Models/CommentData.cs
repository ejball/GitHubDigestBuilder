using System;

namespace GitHubDigestBuilder.Models
{
	internal sealed class CommentData
	{
		public ConversationData? Conversation { get; set; }

		public UserData? Actor { get; set; }

		public int CommentId { get; set; }

		public string? Body { get; set; }

		public string Url
		{
			get
			{
				if (Conversation!.Commit is CommentedCommitData commit)
				{
					return $"{commit.Repo!.Report!.WebBase}/{commit.Repo.Name}/commit/{commit.Sha}#{(Conversation.FilePath == null ? "commitcomment-" : "r")}{CommentId}";
				}
				else if (Conversation.PullRequest is PullRequestData pullRequest)
				{
					return $"{pullRequest.Repo!.Report!.WebBase}/{pullRequest.Repo.Name}/pull/{pullRequest.Number}#{(Conversation.FilePath == null ? "issuecomment-" : "discussion_r")}{CommentId}";
				}
				else if (Conversation.Issue is IssueData issue)
				{
					return $"{issue.Repo!.Report!.WebBase}/{issue.Repo.Name}/pull/{issue.Number}#{(Conversation.FilePath == null ? "issuecomment-" : "discussion_r")}{CommentId}";
				}
				else
				{
					throw new InvalidOperationException();
				}
			}
		}
	}
}
