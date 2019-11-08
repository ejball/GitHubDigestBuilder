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

		public List<EventData> TagEvents { get; } = new List<EventData>();
	}
}
