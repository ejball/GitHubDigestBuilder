using System.Collections.Generic;

namespace GitHubDigestBuilder.Models
{
	internal sealed class PushEventData : EventData
	{
		public string BranchName { get; set; }

		public string BeforeSha { get; set; }

		public string AfterSha { get; set; }

		public int CommitCount { get; set; }

		public int NewCommitCount { get; set; }

		public List<CommitData> NewCommits { get; } = new List<CommitData>();

		public bool CanMerge { get; set; }
	}
}
