using System;
using System.Collections.Generic;

namespace GitHubDigestBuilder
{
	internal sealed class ReportData
	{
		public DateTime? Date { get; set; }

		public string Url { get; set; }

		public List<RepoData> Repos { get; } = new List<RepoData>();
	}
}
