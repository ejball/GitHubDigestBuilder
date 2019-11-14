using System.Collections.Generic;

namespace GitHubDigestBuilder
{
	internal sealed class CommentedCommitData
	{
		public string RepoName { get; set; }

		public string Sha { get; set; }

		public List<CommitConversationData> Conversations { get; } = new List<CommitConversationData>();
	}
}
