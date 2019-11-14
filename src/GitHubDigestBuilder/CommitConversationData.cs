using System.Collections.Generic;

namespace GitHubDigestBuilder
{
	internal sealed class CommitConversationData
	{
		public string FilePath { get; set; }

		public int? FileLine { get; set; }

		public List<CommitCommentData> Comments { get; } = new List<CommitCommentData>();
	}
}
