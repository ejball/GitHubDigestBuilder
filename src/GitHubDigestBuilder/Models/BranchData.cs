using System.Collections.Generic;

namespace GitHubDigestBuilder.Models
{
	internal sealed class BranchData
	{
		public string Name { get; set; }

		public string RepoName { get; set; }

		public string Url { get; set; }

		public List<EventData> Events { get; } = new List<EventData>();

		public PullRequestData PullRequest { get; set; }
	}
}
