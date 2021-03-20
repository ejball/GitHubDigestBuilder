using System.Collections.Generic;

namespace GitHubDigestBuilder.Models
{
	internal sealed class CommentedCommitData
	{
		public RepoData? Repo { get; set; }

		public string? Sha { get; set; }

		public List<ConversationData> Conversations { get; } = new();

		public string Url => $"{Repo!.Url}/commit/{Sha}";
	}
}
