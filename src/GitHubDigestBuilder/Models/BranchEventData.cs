namespace GitHubDigestBuilder.Models
{
	internal sealed class BranchEventData : EventData
	{
		public BranchData BaseBranch { get; set; }

		public ConversationData Conversation { get; set; }
	}
}
