using System.Collections.Generic;

namespace GitHubDigestBuilder
{
	public sealed class DigestSettings
	{
		public GitHubSettings GitHub { get; set; }

		public List<RepoSettings> Repos { get; set; }

		public string OutputDirectory { get; set; }

		public double? TimeZoneOffsetHours { get; set; }

		public string Culture { get; set; }

		public string DumpDirectory { get; set; }
	}
}
