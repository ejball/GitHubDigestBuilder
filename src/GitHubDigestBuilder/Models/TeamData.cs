namespace GitHubDigestBuilder.Models
{
	internal sealed class TeamData
	{
		public ReportData? Report { get; set; }

		public string? Org { get; set; }

		public string? Name { get; set; }

		public string Url => $"{Report!.WebBase}/orgs/{Org}/teams/{Name}";
	}
}
