using System.Collections.Generic;

namespace GitHubDigestBuilder.Models
{
	internal sealed class BranchData
	{
		public string Name { get; set; }

		public RepoData Repo { get; set; }

		public List<EventData> Events { get; } = new List<EventData>();

		public string Url => $"{Repo.Report.WebBase}/{Repo.Name}/tree/{Name}";
	}
}
