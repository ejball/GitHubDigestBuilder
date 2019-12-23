using System.Collections.Generic;

namespace GitHubDigestBuilder.Models
{
	internal sealed class CommentedCommitData
	{
		public RepoData Repo { get; set; }

		public string Sha { get; set; }

		public List<ConversationData> Conversations { get; } = new List<ConversationData>();

		public string Url => $"{Repo.Report.WebBase}/{Repo.Name}/commit/{Sha}";
	}
}
