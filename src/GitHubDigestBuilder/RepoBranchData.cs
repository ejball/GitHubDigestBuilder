using System.Collections.Generic;

namespace GitHubDigestBuilder
{
	internal sealed class RepoBranchData
	{
		public string Name { get; set; }

		public string RepoName { get; set; }

		public List<EventData> Events { get; } = new List<EventData>();
	}
}
