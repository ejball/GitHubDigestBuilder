using System.Collections.Generic;

namespace GitHubDigestBuilder.Models
{
	internal sealed class ConversationData
	{
		public string FilePath { get; set; }

		public string Position { get; set; }

		public List<CommentData> Comments { get; } = new List<CommentData>();
	}
}