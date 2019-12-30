namespace GitHubDigestBuilder.Models
{
	internal sealed class BranchEventData : EventData
	{
		public ConversationData? Conversation { get; set; }
	}
}
