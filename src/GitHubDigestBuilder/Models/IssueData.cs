using System.Collections.Generic;

namespace GitHubDigestBuilder.Models
{
	internal sealed class IssueData
	{
		public RepoData Repo { get; set; }

		public int Number { get; set; }

		public string Title { get; set; }

		public string Body { get; set; }

		public List<EventData> Events { get; } = new List<EventData>();

		public string Url => $"{Repo.Report.WebBase}/{Repo.Name}/pull/{Number}";
	}
}
