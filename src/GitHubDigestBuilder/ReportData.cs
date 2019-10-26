using System.Collections.Generic;

namespace GitHubDigestBuilder
{
	public sealed class ReportData
	{
		public string LongDate { get; set; }

		public IReadOnlyList<ActivitySourceData> Repositories { get; set; }

		public IReadOnlyList<ActivitySourceData> Users { get; set; }
	}
}
