using System.Text.Json;

namespace GitHubDigestBuilder.Models;

internal sealed class RawEventData
{
	public string? EventId { get; set; }

	public string? EventType { get; set; }

	public string? ActorName { get; set; }

	public DateTime CreatedUtc { get; set; }

	public string? RepoName { get; set; }

	public bool IsNetwork { get; set; }

	public JsonElement Payload { get; set; }
}
