namespace GitHubDigestBuilder.Models;

internal sealed class BranchEventData : EventData
{
	public BranchData? Branch { get; set; }
}
