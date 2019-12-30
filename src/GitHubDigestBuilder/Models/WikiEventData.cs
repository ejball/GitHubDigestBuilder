namespace GitHubDigestBuilder.Models
{
	internal sealed class WikiEventData : EventData
	{
		public string? PageName { get; set; }

		public string? PageTitle { get; set; }

		public string Url => $"{Repo!.Report!.WebBase}/{Repo!.Name}/wiki/{PageName}";
	}
}
