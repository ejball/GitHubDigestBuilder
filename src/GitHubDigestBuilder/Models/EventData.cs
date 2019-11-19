namespace GitHubDigestBuilder.Models
{
	internal abstract class EventData
	{
		public string Kind { get; set; }

		public string RepoName { get; set; }

		public string ActorName { get; set; }
	}
}
