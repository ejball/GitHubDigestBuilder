namespace GitHubDigestBuilder.Models
{
	internal sealed class UserData
	{
		public ReportData Report { get; set; }

		public string Name { get; set; }

		public string Url => $"{Report.WebBase}/{Name}";
	}
}
