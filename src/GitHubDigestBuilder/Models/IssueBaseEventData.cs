namespace GitHubDigestBuilder.Models;

internal class IssueBaseEventData : EventData
{
	public ConversationData? Conversation { get; set; }

	public UserData? SourceUser { get; set; }

	public UserData? TargetUser { get; set; }

	public TeamData? TargetTeam { get; set; }

	public string? LabelName { get; set; }

	public string? MilestoneTitle { get; set; }

	public string? RenameFrom { get; set; }

	public string? RenameTo { get; set; }
}
