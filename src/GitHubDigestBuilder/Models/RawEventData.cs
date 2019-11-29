using System;
using System.Text.Json;

namespace GitHubDigestBuilder.Models
{
	internal sealed class RawEventData
	{
		public string EventId { get; set; }

		public string EventType { get; set; }

		public string ActorName { get; set; }

		public DateTime CreatedUtc { get; set; }

		public string RepoName { get; set; }

		public string SourceRepoName { get; set; }

		public JsonElement Payload { get; set; }
	}
}
