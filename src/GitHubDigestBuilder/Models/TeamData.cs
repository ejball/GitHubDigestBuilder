namespace GitHubDigestBuilder.Models
{
	internal sealed class TeamData
	{
		public string? WebBase { get; set; }

		public string? Org { get; set; }

		public string? Name { get; set; }

		public string Url => $"{WebBase}/orgs/{Org}/teams/{Name}";
	}
}
