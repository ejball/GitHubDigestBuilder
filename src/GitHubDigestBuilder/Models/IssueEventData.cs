namespace GitHubDigestBuilder.Models
{
	internal sealed class IssueEventData : EventData
	{
		public ConversationData Conversation { get; set; }
	}
}
