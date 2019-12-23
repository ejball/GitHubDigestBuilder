namespace GitHubDigestBuilder.Models
{
	internal sealed class TagData
	{
		public string Name { get; set; }

		public RepoData Repo { get; set; }

		public string Url => $"{Repo.Report.WebBase}/{Repo.Name}/tree/{Name}";
	}
}
