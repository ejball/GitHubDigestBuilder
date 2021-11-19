namespace GitHubDigestBuilder.Models;

internal sealed class TagEventData : EventData
{
	public TagData? Tag { get; set; }
}
