namespace GitHubDigestBuilder.Models
{
	internal sealed class PullRequestEventData : EventData
	{
		public ConversationData Conversation { get; set; }

		public UserData SourceUser { get; set; }

		public UserData TargetUser { get; set; }

		public CommitData Commit { get; set; }

		public string LabelName { get; set; }

		public string RenameFrom { get; set; }

		public string RenameTo { get; set; }
	}
}
