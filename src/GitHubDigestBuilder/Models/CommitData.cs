namespace GitHubDigestBuilder.Models
{
	internal sealed class CommitData
	{
		public string RepoName { get; set; }

		public string Sha { get; set; }

		public string Subject { get; set; }

		public string Remarks { get; set; }
	}
}
