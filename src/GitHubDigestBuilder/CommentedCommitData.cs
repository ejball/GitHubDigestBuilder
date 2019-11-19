using System.Collections.Generic;

namespace GitHubDigestBuilder
{
	internal sealed class CommentedCommitData
	{
		public string RepoName { get; set; }

		public string Sha { get; set; }

		public List<ConversationData> Conversations { get; } = new List<ConversationData>();
	}
}
