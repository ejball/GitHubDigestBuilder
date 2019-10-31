namespace GitHubDigestBuilder
{
	internal sealed class PushData
	{
		public string RepoName { get; set; }

		public string ActorName { get; set; }

		public string BranchName { get; set; }

		public string BeforeSha { get; set; }

		public string AfterSha { get; set; }

		public int? CommitCount { get; set; }
	}
}
