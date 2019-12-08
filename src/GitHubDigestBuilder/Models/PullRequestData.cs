namespace GitHubDigestBuilder.Models
{
	internal sealed class PullRequestData
	{
		public int Number { get; set; }

		public string Title { get; set; }

		public string Body { get; set; }

		public string RepoName { get; set; }

		public bool IsClosed { get; set; }
	}
}
