namespace GitHubDigestBuilder.Settings
{
	public sealed class DigestSettings
	{
		public GitHubSettings? GitHub { get; set; }

		public string? OutputDirectory { get; set; }

		public double? TimeZoneOffsetHours { get; set; }

		public string? Culture { get; set; }

		public string? CacheDirectory { get; set; }
	}
}
