namespace GitHubDigestBuilder.Settings
{
	public sealed class DigestSettings
	{
		public GitHubSettings? GitHub { get; set; }

		public List<GitHubSettings>? GitHubs { get; set; }

		public double? TimeZoneOffsetHours { get; set; }

		public string? Culture { get; set; }
	}
}
