using System;
using System.Collections.Generic;

namespace GitHubDigestBuilder.Models
{
	internal sealed class ReportData
	{
		public DateTime? Date { get; set; }

		public DateTime? PreviousDate { get; set; }

		public string BaseUrl { get; set; }

		public bool AutoRefresh { get; set; }

		public List<RepoData> Repos { get; } = new List<RepoData>();
	}
}