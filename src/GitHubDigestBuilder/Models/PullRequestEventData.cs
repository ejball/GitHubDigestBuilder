namespace GitHubDigestBuilder.Models;

internal sealed class PullRequestEventData : IssueBaseEventData
{
	public CommitData? Commit { get; set; }
}
