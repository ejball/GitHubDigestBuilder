using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using HandlebarsDotNet;
using YamlDotNet.Serialization;

namespace GitHubDigestBuilder
{
	public static class Program
	{
		public static async Task RunAsync(IReadOnlyList<string> args)
		{
			if (args.Count != 1)
				throw new ApplicationException("Usage: GitHubDigestBuilder configuration-file");

			var configFilePath = Path.GetFullPath(args[0]);
			if (!File.Exists(configFilePath))
				throw new ApplicationException("Configuration file not found.");
			var configFileDirectory = Path.GetDirectoryName(configFilePath);

			var settings = JsonSerializer.Deserialize<DigestSettings>(
				ConvertYamlToJson(File.ReadAllText(configFilePath)),
				new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

			var timeZoneOffset = settings.TimeZoneOffsetHours != null ? TimeSpan.FromHours(settings.TimeZoneOffsetHours.Value) : DateTimeOffset.Now.Offset;
			var now = new DateTimeOffset(DateTime.UtcNow).ToOffset(timeZoneOffset);
			var endDate = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, timeZoneOffset);
			var endDateUtc = endDate.UtcDateTime;
			var startDate = endDate.AddDays(-1.0);
			var startDateUtc = startDate.UtcDateTime;
			var startDateIso = startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

			string dumpDirectory = null;
			if (settings.DumpDirectory != null)
			{
				dumpDirectory = Path.Combine(configFileDirectory, settings.DumpDirectory, startDateIso);
				Directory.CreateDirectory(dumpDirectory);
			}

			var httpClient = new HttpClient();
			httpClient.DefaultRequestHeaders.UserAgent.Add(ProductInfoHeaderValue.Parse("GitHubDigestBuilder"));
			httpClient.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/vnd.github.v3+json"));

			if (settings.GitHub?.AuthToken is string token)
				httpClient.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"token {token}");

			var apiBase = (settings.GitHub?.ApiUrl ?? "https://api.github.com").TrimEnd('/');
			var webBase = (settings.GitHub?.WebUrl ?? "https://github.com").TrimEnd('/');

			var repositories = new List<ActivitySourceData>();
			var users = new List<ActivitySourceData>();

			foreach (var activitySource in settings.Include ?? throw new ApplicationException("Configuration: 'include' is missing."))
			{
				var (sourceKind, sourceName, isUser) = activitySource switch
				{
					{ Repo: var repoName, User: null } => ("repos", repoName, false),
					{ Repo: null, User: var userName } => ("users", userName, true),
					_ => throw new ArgumentException("Configuration: 'include' is invalid.''"),
				};

				var activities = new List<ActivityData>();

				var isLastPage = false;
				for (var pageNumber = 1; pageNumber <= 10 && !isLastPage; pageNumber++)
				{
					JsonDocument pageDocument;

					var dumpFile = dumpDirectory != null ? Path.Combine(dumpDirectory, $"{sourceName.Replace('/', '_')}_{pageNumber}.json") : null;
					if (dumpFile != null && File.Exists(dumpFile))
					{
						await using var dumpStream = File.OpenRead(dumpFile);
						pageDocument = await JsonDocument.ParseAsync(dumpStream);
					}
					else
					{
						var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/{sourceKind}/{sourceName}/events?page={pageNumber}");
						var response = await httpClient.SendAsync(request);

						if (response.StatusCode != System.Net.HttpStatusCode.OK)
							throw new InvalidOperationException($"Unexpected status code: {response.StatusCode}");

						await using var pageStream = await response.Content.ReadAsStreamAsync();
						pageDocument = await JsonDocument.ParseAsync(pageStream);

						if (dumpFile != null)
						{
							await using var dumpStream = File.Open(dumpFile, FileMode.CreateNew, FileAccess.Write);
							await using var jsonWriter = new Utf8JsonWriter(dumpStream, new JsonWriterOptions { Indented = true });
							pageDocument.WriteTo(jsonWriter);
						}
					}

					foreach (var activity in pageDocument.RootElement.EnumerateArray())
					{
						var createdUtc = ParseDateTime(activity.GetProperty("created_at").GetString());
						if (createdUtc < startDateUtc)
						{
							isLastPage = true;
						}
						else if (createdUtc < endDateUtc)
						{
							var payload = activity.GetProperty("payload");

							var actor = activity.GetProperty("actor", "login").GetString();
							var repo = activity.GetProperty("repo", "name").GetString();

							var refType = payload.TryGetProperty("ref_type")?.GetString();
							var refName = payload.TryGetProperty("ref")?.GetString();

							const string branchRefPrefix = "refs/heads/";
							if (refType == null && refName?.StartsWith(branchRefPrefix) == true)
							{
								refType = "branch";
								refName = refName.Substring(branchRefPrefix.Length);
							}

							const string tagRefPrefix = "refs/tags/";
							if (refType == null && refName?.StartsWith(tagRefPrefix) == true)
							{
								refType = "tag";
								refName = refName.Substring(tagRefPrefix.Length);
							}

							activities.Add(
								new ActivityData
								{
									EventId = activity.GetProperty("id").GetString(),
									Kind = activity.GetProperty("type").GetString(),
									Actor = actor,
									ActorUrl = $"{webBase}/{actor}",
									Repo = repo,
									RepoUrl = $"{webBase}/{repo}",
									RefType = refType,
									RefName = refName,
									BeforeSha = payload.TryGetProperty("before")?.GetString(),
									AfterSha = payload.TryGetProperty("head")?.GetString(),
									CommitCount = payload.TryGetProperty("size")?.GetInt32(),
								});
						}
					}
				}

				var source = new ActivitySourceData
				{
					Name = sourceName,
					WebUrl = $"{webBase}/{sourceName}",
					Activities = activities,
				};
				(isUser ? users : repositories).Add(source);
			}

			var culture = settings.Culture == null ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(settings.Culture);

			var report = new ReportData
			{
				LongDate = startDate.ToString("D", culture),
				Repositories = repositories,
				Users = users,
			};

			var outputFile = Path.Combine(configFileDirectory, settings.OutputDirectory ?? ".", $"{startDateIso}.html");
			Directory.CreateDirectory(Path.GetDirectoryName(outputFile));

			var handlebars = Handlebars.Create(
				new HandlebarsConfiguration
				{
					BlockHelpers = { ["ifPlural"] = IfPluralBlockHelper },
				});
			var templateText = GetEmbeddedResourceText("GitHubDigestBuilder.template.html");
			var template = handlebars.Compile(templateText);

			string reportHtml = template(report);

			await File.WriteAllTextAsync(outputFile, reportHtml);
		}

		private static void IfPluralBlockHelper(TextWriter output, HelperOptions options, object context, object[] arguments)
		{
			if (arguments.Length != 1)
				throw new HandlebarsException("ifPlural must have one argument.");

			if (Convert.ToInt32(arguments[0], CultureInfo.InvariantCulture) > 1)
				options.Template(output, null);
			else
				options.Inverse(output, null);
		}

		private static string GetEmbeddedResourceText(string name)
		{
			using var reader = new StreamReader(typeof(Program).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException());
			return reader.ReadToEnd();
		}

		private static DateTime ParseDateTime(string value)
		{
			return DateTime.ParseExact(value,
				"yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'",
				CultureInfo.InvariantCulture,
				DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
		}

		public static async Task<int> Main(string[] args)
		{
			try
			{
				await RunAsync(args);
				return 0;
			}
			catch (ApplicationException exception)
			{
				Console.Error.WriteLine(exception.Message);
				return 1;
			}
			catch (Exception exception)
			{
				Console.Error.WriteLine(exception);
				return 1;
			}
		}

		private static string ConvertYamlToJson(string yaml)
		{
			var deserializer = new DeserializerBuilder().Build();
			var serializer = new SerializerBuilder().JsonCompatible().Build();
			return serializer.Serialize(deserializer.Deserialize(new StringReader(yaml)));
		}
	}
}
