using System.Collections.Generic;

namespace GitHubDigestBuilder.Models
{
	internal sealed class RepoData
	{
		public string Name { get; set; }

		public string Url { get; set; }

		public List<BranchData> Branches { get; } = new List<BranchData>();

		public List<TagEventData> TagEvents { get; } = new List<TagEventData>();

		public List<WikiEventData> WikiEvents { get; } = new List<WikiEventData>();

		public List<CommentedCommitData> CommentedCommits { get; } = new List<CommentedCommitData>();
	}
}
