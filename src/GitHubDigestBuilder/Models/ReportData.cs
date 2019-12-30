using System;
using System.Collections.Generic;

namespace GitHubDigestBuilder.Models
{
	internal sealed class ReportData
	{
		public DateTime? Date { get; set; }

		public DateTime? PreviousDate { get; set; }

		public DateTimeOffset Now { get; set; }

		public string? WebBase { get; set; }

		public bool AutoRefresh { get; set; }

		public List<RepoData> Repos { get; } = new List<RepoData>();

		public List<string> Warnings { get; } = new List<string>();
	}
}
