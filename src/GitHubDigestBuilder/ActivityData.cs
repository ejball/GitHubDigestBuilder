namespace GitHubDigestBuilder
{
	public sealed class ActivityData
	{
		public string EventId { get; set; }

		public string Kind { get; set; }

		public string Repo { get; set; }

		public string RepoUrl { get; set; }

		public string Actor { get; set; }

		public string ActorUrl { get; set; }

		public string RefType { get; set; }

		public string RefName { get; set; }

		public string BeforeSha { get; set; }

		public string AfterSha { get; set; }

		public int? CommitCount { get; set; }
	}
}
