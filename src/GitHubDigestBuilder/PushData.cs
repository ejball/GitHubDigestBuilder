using System.Collections.Generic;

namespace GitHubDigestBuilder
{
	internal sealed class PushData
	{
		public string RepoName { get; set; }

		public string ActorName { get; set; }

		public string BranchName { get; set; }

		public string BeforeSha { get; set; }

		public string AfterSha { get; set; }

		public int CommitCount { get; set; }

		public int NewCommitCount { get; set; }

		public List<CommitData> NewCommits { get; } = new List<CommitData>();

		public bool CanMerge { get; set; }
	}
}
