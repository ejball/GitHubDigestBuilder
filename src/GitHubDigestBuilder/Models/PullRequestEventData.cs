namespace GitHubDigestBuilder.Models
{
	internal sealed class PullRequestEventData : EventData
	{
		public ConversationData Conversation { get; set; }
	}
}
