using System.Text.Json;

namespace GitHubDigestBuilder
{
	internal static class JsonElementUtility
	{
		public static JsonElement GetProperty(this JsonElement element, params string[] names)
		{
			foreach (var name in names)
				element = element.GetProperty(name);

			return element;
		}

		public static JsonElement? TryGetProperty(this JsonElement element, params string[] names)
		{
			foreach (var name in names)
			{
				if (!element.TryGetProperty(name, out element) || element.ValueKind == JsonValueKind.Null)
					return null;
			}

			return element;
		}

		public static int? GetNullOrInt32(this JsonElement element) => element.ValueKind == JsonValueKind.Null ? default(int?) : element.GetInt32();
	}
}
