using Scriban;

namespace GitHubDigestBuilder;

internal static class ReportFunctions
{
	public static string Format(TemplateContext context, object value, string format) =>
		((IFormattable) value).ToString(format, context.CurrentCulture);
}
