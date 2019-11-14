using System;
using System.Collections.Generic;

namespace GitHubDigestBuilder
{
	internal sealed class RepoData
	{
		public string Name { get; set; }

		public DateTime? PreviousDate { get; set; }

		public bool IsPartial { get; set; }

		public List<RepoBranchData> Branches { get; } = new List<RepoBranchData>();

		public List<TagEventData> TagEvents { get; } = new List<TagEventData>();

		public List<WikiEventData> WikiEvents { get; } = new List<WikiEventData>();

		public List<CommentedCommitData> CommentedCommits { get; } = new List<CommentedCommitData>();
	}
}
