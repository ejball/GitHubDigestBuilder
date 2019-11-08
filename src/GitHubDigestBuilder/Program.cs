using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Scriban;
using Scriban.Runtime;
using YamlDotNet.Serialization;

namespace GitHubDigestBuilder
{
	public static class Program
	{
		public static async Task RunAsync(IReadOnlyList<string> args)
		{
			if (args.Count < 1 || args.Count > 2)
				throw new ApplicationException("Usage: GitHubDigestBuilder configuration-file [date]");

			var configFilePath = Path.GetFullPath(args[0]);
			if (!File.Exists(configFilePath))
				throw new ApplicationException("Configuration file not found.");
			var configFileDirectory = Path.GetDirectoryName(configFilePath);

			var settings = JsonSerializer.Deserialize<DigestSettings>(
				ConvertYamlToJson(File.ReadAllText(configFilePath)),
				new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

			var timeZoneOffset = settings.TimeZoneOffsetHours != null ? TimeSpan.FromHours(settings.TimeZoneOffsetHours.Value) : DateTimeOffset.Now.Offset;
			var date = args.Count >= 2 ? ParseDate(args[1]) : new DateTimeOffset(DateTime.UtcNow).ToOffset(timeZoneOffset).Date.AddDays(-1.0);
			var dateIso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			var startDateTime = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, timeZoneOffset);
			var startDateTimeUtc = startDateTime.UtcDateTime;
			var endDateTime = startDateTime.AddDays(1.0);
			var endDateTimeUtc = endDateTime.UtcDateTime;

			string dumpDirectory = null;
			if (settings.DumpDirectory != null)
			{
				dumpDirectory = Path.Combine(configFileDirectory, settings.DumpDirectory, dateIso);
				Directory.CreateDirectory(dumpDirectory);
			}

			var httpClient = new HttpClient();
			httpClient.DefaultRequestHeaders.UserAgent.Add(ProductInfoHeaderValue.Parse("GitHubDigestBuilder"));
			httpClient.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/vnd.github.v3+json"));

			if (settings.GitHub?.AuthToken is string token)
				httpClient.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"token {token}");

			var apiBase = (settings.GitHub?.ApiUrl ?? "https://api.github.com").TrimEnd('/');
			var handledEventIds = new HashSet<string>();

			async Task<(IReadOnlyList<JsonElement> Events, DateTime? PreviousDate, bool Failed)> loadEventsAsync(string sourceKind, string sourceName)
			{
				var eventElements = new List<JsonElement>();
				DateTime? previousDate = null;

				var foundLastPage = false;
				for (var pageNumber = 1; pageNumber <= 10 && !foundLastPage; pageNumber++)
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

					foreach (var eventElement in pageDocument.RootElement.EnumerateArray())
					{
						var eventId = eventElement.GetProperty("id").GetString();
						if (!handledEventIds.Add(eventId))
							continue;

						var createdUtc = ParseDateTime(eventElement.GetProperty("created_at").GetString());
						if (createdUtc < startDateTimeUtc)
						{
							foundLastPage = true;

							if (previousDate is null)
								previousDate = new DateTimeOffset(createdUtc).ToOffset(timeZoneOffset).Date;
						}
						else if (createdUtc < endDateTimeUtc)
						{
							eventElements.Add(eventElement);
						}
					}
				}

				eventElements.Reverse();

				return (eventElements, previousDate, foundLastPage);
			}

			var report = new ReportData
			{
				Date = date,
				Url = (settings.GitHub?.WebUrl ?? "https://github.com").TrimEnd('/'),
			};

			var repoSources = settings.Repos ?? new List<RepoSettings>();

			foreach (var repoSource in repoSources)
			{
				if (repoSource.Name is string repoName)
				{
					var (eventElements, previousDate, foundLastPage) = await loadEventsAsync("repos", repoName);

					var repo = new RepoData { Name = repoName, PreviousDate = previousDate, IsPartial = !foundLastPage };

					foreach (var eventElement in eventElements)
					{
						var eventId = eventElement.GetProperty("id").GetString();

						var actorName = eventElement.GetProperty("actor", "login").GetString();
						if (repoName != eventElement.GetProperty("repo", "name").GetString())
							throw new InvalidOperationException($"Unexpected repository in event {eventId}.");
						var payload = eventElement.GetProperty("payload");

						var eventType = eventElement.GetProperty("type").GetString();
						if (eventType == "PushEvent")
						{
							const string branchRefPrefix = "refs/heads/";
							var refName = payload.TryGetProperty("ref")?.GetString();
							if (refName?.StartsWith(branchRefPrefix) == true)
							{
								var branchName = refName.Substring(branchRefPrefix.Length);
								var branch = repo.Branches.SingleOrDefault(x => x.Name == branchName);
								if (branch == null)
									repo.Branches.Add(branch = new RepoBranchData { Name = branchName, RepoName = repoName });

								var beforeSha = payload.TryGetProperty("before")?.GetString();
								var afterSha = payload.TryGetProperty("head")?.GetString();
								var commitCount = payload.TryGetProperty("size")?.GetInt32() ?? 0;
								var distinctCommitCount = payload.TryGetProperty("distinct_size")?.GetInt32() ?? 0;
								var commits = payload.TryGetProperty("commits")?.EnumerateArray().ToList() ?? new List<JsonElement>();
								var canMerge = commitCount == commits.Count;

								var push = branch.Events.LastOrDefault() as PushEventData;
								if (push == null ||
									push.RepoName != repoName ||
									push.ActorName != actorName ||
									push.BranchName != branchName ||
									push.AfterSha != beforeSha ||
									!push.CanMerge ||
									!canMerge)
								{
									push = new PushEventData
									{
										Kind = "push",
										RepoName = repoName,
										ActorName = actorName,
										BranchName = branchName,
										BeforeSha = beforeSha,
										CanMerge = canMerge,
									};
									branch.Events.Add(push);
								}

								push.AfterSha = afterSha;
								push.CommitCount += commitCount;
								push.NewCommitCount += distinctCommitCount;

								foreach (var commit in commits.Where(x => x.TryGetProperty("distinct")?.GetBoolean() == true))
								{
									var message = commit.TryGetProperty("message")?.GetString() ?? "";
									var messageMatch = Regex.Match(message, @"^([^\r\n]*)(.*)$", RegexOptions.Singleline);
									var subject = messageMatch.Groups[1].Value.Trim();
									var remarks = Regex.Replace(messageMatch.Groups[2].Value.Trim(), @"\s+", " ");

									push.NewCommits.Add(new CommitData
									{
										RepoName = repoName,
										Sha = commit.GetProperty("sha").GetString(),
										Subject = subject,
										Remarks = remarks,
									});
								}
							}
						}
						else if (eventType == "CreateEvent" || eventType == "DeleteEvent")
						{
							var isDelete = eventType == "DeleteEvent";

							var refType = payload.GetProperty("ref_type").GetString();
							if (refType == "branch")
							{
								var branchName = payload.GetProperty("ref").GetString();
								var branch = repo.Branches.SingleOrDefault(x => x.Name == branchName);
								if (branch == null)
									repo.Branches.Add(branch = new RepoBranchData { Name = branchName, RepoName = repoName });

								branch.Events.Add(new BranchEventData
								{
									Kind = isDelete ? "delete-branch" : "create-branch",
									RepoName = repoName,
									ActorName = actorName,
									BranchName = branchName,
								});
							}
						}
					}

					if (repo.Branches.Count != 0)
						report.Repos.Add(repo);
				}
			}

			var culture = settings.Culture == null ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(settings.Culture);

			var outputFile = Path.Combine(configFileDirectory, settings.OutputDirectory ?? ".", $"{dateIso}.html");
			Directory.CreateDirectory(Path.GetDirectoryName(outputFile));

			var templateText = GetEmbeddedResourceText("GitHubDigestBuilder.template.scriban-html");
			var template = Template.Parse(templateText);

			var templateContext = new TemplateContext { StrictVariables = true };
			templateContext.PushCulture(culture);

			var scriptObject = new ScriptObject();
			scriptObject.Import(report);
			scriptObject.Import(typeof(ReportFunctions));
			templateContext.PushGlobal(scriptObject);

			var reportHtml = template.Render(templateContext);

			await File.WriteAllTextAsync(outputFile, reportHtml);
		}

		private static string GetEmbeddedResourceText(string name)
		{
			using var reader = new StreamReader(typeof(Program).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException());
			return reader.ReadToEnd();
		}

		private static DateTime ParseDate(string value) =>
			DateTime.ParseExact(value, "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

		private static DateTime ParseDateTime(string value) =>
			DateTime.ParseExact(value, "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

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
