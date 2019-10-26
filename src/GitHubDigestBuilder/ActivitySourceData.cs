using System.Collections.Generic;
using System.Linq;

namespace GitHubDigestBuilder
{
	public sealed class ActivitySourceData
	{
		public string Name { get; set; }

		public string WebUrl { get; set; }

		public IReadOnlyList<ActivityData> Activities { get; set; }

		public IReadOnlyList<BranchActivityData> Branches =>
			Activities.Where(x => x.RefType == "branch").GroupBy(x => x.RefName).Select(x => new BranchActivityData { Activities = x.ToList() }).ToList();

		public IReadOnlyList<ActivityData> Pushes => Activities.Where(x => x.Kind == "PushEvent").ToList();

		public IReadOnlyList<ActivityData> CreatedBranches => Activities.Where(x => x.Kind == "CreateEvent" && x.RefType == "branch").ToList();

		public IReadOnlyList<ActivityData> DeletedBranches => Activities.Where(x => x.Kind == "DeleteEvent" && x.RefType == "branch").ToList();

		public IReadOnlyList<ActivityData> CreatedTags => Activities.Where(x => x.Kind == "CreateEvent" && x.RefType == "tag").ToList();

		public IReadOnlyList<ActivityData> DeletedTags => Activities.Where(x => x.Kind == "DeleteEvent" && x.RefType == "tag").ToList();
	}
}
