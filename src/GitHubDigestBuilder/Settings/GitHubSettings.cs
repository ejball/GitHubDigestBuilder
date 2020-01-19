using System.Collections.Generic;

namespace GitHubDigestBuilder.Settings
{
	public sealed class GitHubSettings
	{
		public string? WebUrl { get; set; }

		public string? ApiUrl { get; set; }

		public string? AuthToken { get; set; }

		public List<RepoSettings>? Repos { get; set; }

		public List<UserSettings>? Users { get; set; }

		public List<FilterSettings>? Excludes { get; set; }
	}
}
