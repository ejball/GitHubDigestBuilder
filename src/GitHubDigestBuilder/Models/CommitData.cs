namespace GitHubDigestBuilder.Models
{
	internal sealed class CommitData
	{
		public BranchData Branch { get; set; }

		public string Sha { get; set; }

		public string Subject { get; set; }

		public string Remarks { get; set; }

		public string Url => $"{Branch.Repo.Report.WebBase}/{Branch.Repo.Name}/commit/{Sha}";
	}
}
