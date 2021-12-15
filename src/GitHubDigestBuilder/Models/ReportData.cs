using System.Globalization;

namespace GitHubDigestBuilder.Models;

internal sealed class ReportData
{
	public DateTime? Date { get; set; }

	public DateTime? PreviousDate { get; set; }

	public DateTimeOffset Now { get; set; }

	public bool IsEmail { get; set; }

	public List<RepoData> Repos { get; } = new();

	public List<string> Warnings { get; } = new();

	public CultureInfo? Culture { get; set; }

	public string Format(object value, string format) =>
		((IFormattable) value).ToString(format, Culture ?? CultureInfo.InvariantCulture);
}
