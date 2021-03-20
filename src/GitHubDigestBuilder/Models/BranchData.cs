using System.Collections.Generic;

namespace GitHubDigestBuilder.Models
{
	internal sealed class BranchData
	{
		public string? Name { get; set; }

		public RepoData? Repo { get; set; }

		public List<EventData> Events { get; } = new();

		public string Url => $"{Repo!.Url}/tree/{Name}";
	}
}
