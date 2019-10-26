using System.Collections.Generic;
using System.Linq;

namespace GitHubDigestBuilder
{
	public sealed class BranchActivityData
	{
		public IReadOnlyList<ActivityData> Activities { get; set; }

		public IReadOnlyList<ActivityData> Pushes => Activities.Where(x => x.Kind == "PushEvent").ToList();
	}
}
