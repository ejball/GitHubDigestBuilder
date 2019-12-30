using System.Collections.Generic;

namespace GitHubDigestBuilder.Settings
{
	public sealed class DigestSettings
	{
		public GitHubSettings? GitHub { get; set; }

		public List<RepoSettings>? Repos { get; set; }

		public List<UserSettings>? Users { get; set; }

		public List<FilterSettings>? Excludes { get; set; }

		public string? OutputDirectory { get; set; }

		public double? TimeZoneOffsetHours { get; set; }

		public string? Culture { get; set; }
	}
}
