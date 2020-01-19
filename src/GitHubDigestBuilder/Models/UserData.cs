namespace GitHubDigestBuilder.Models
{
	internal sealed class UserData
	{
		public string? WebBase { get; set; }

		public string? Name { get; set; }

		public string Url => $"{WebBase}/{Name}";
	}
}
