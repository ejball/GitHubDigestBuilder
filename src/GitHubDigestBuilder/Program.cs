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
using ArgsReading;
using GitHubDigestBuilder.Models;
using GitHubDigestBuilder.Settings;
using Scriban;
using Scriban.Runtime;
using YamlDotNet.Serialization;

namespace GitHubDigestBuilder
{
	public static class Program
	{
		public static async Task RunAsync(ArgsReader args)
		{
			var dateString = args.ReadOption("date");
			var autoRefresh = args.ReadFlag("auto-refresh");
			var authToken = args.ReadOption("auth");
			var configFilePath = args.ReadArgument();
			args.VerifyComplete();

			configFilePath = Path.GetFullPath(configFilePath);
			if (!File.Exists(configFilePath))
				throw new ApplicationException("Configuration file not found.");
			var configFileDirectory = Path.GetDirectoryName(configFilePath);

			var settings = JsonSerializer.Deserialize<DigestSettings>(
				ConvertYamlToJson(File.ReadAllText(configFilePath)),
				new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

			var timeZoneOffset = settings.TimeZoneOffsetHours != null ? TimeSpan.FromHours(settings.TimeZoneOffsetHours.Value) : DateTimeOffset.Now.Offset;
			var date = dateString != null ? ParseDate(dateString) : new DateTimeOffset(DateTime.UtcNow).ToOffset(timeZoneOffset).Date.AddDays(-1.0);
			var dateIso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			var startDateTime = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, timeZoneOffset);
			var startDateTimeUtc = startDateTime.UtcDateTime;
			var endDateTime = startDateTime.AddDays(1.0);
			var endDateTimeUtc = endDateTime.UtcDateTime;

			var outputFile = Path.Combine(configFileDirectory, settings.OutputDirectory ?? ".", $"{dateIso}.html");
			Directory.CreateDirectory(Path.GetDirectoryName(outputFile));

			try
			{
				string dumpDirectory = null;
				if (settings.DumpDirectory != null)
				{
					dumpDirectory = Path.Combine(configFileDirectory, settings.DumpDirectory, dateIso);
					Directory.CreateDirectory(dumpDirectory);
				}

				var httpClient = new HttpClient();
				httpClient.DefaultRequestHeaders.UserAgent.Add(ProductInfoHeaderValue.Parse("GitHubDigestBuilder"));
				httpClient.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/vnd.github.v3+json"));

				authToken ??= settings.GitHub?.AuthToken;
				if (!string.IsNullOrWhiteSpace(authToken))
					httpClient.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"token {authToken}");

				var apiBase = (settings.GitHub?.ApiUrl ?? "https://api.github.com").TrimEnd('/');
				var handledEventIds = new HashSet<string>();

				async Task loadPagesAsync(string url, Func<JsonElement, bool> processPage)
				{
					var dumpName = Regex.Replace(Regex.Replace(url, @"\?.*$", ""), @"/+", "_").Trim('_');

					var foundLastPage = false;
					for (var pageNumber = 1; pageNumber <= 10 && !foundLastPage; pageNumber++)
					{
						JsonDocument pageDocument;

						var dumpFile = dumpDirectory != null ? Path.Combine(dumpDirectory, $"{dumpName}_{pageNumber}.json") : null;
						if (dumpFile != null && File.Exists(dumpFile))
						{
							await using var dumpStream = File.OpenRead(dumpFile);
							pageDocument = await JsonDocument.ParseAsync(dumpStream);
						}
						else
						{
							var urlHasParams = url.Contains('?');
							var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/{url}{(urlHasParams ? '&' : '?')}page={pageNumber}");
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

						foundLastPage = processPage(pageDocument.RootElement);
					}
				}

				var baseUrl = (settings.GitHub?.WebUrl ?? "https://github.com").TrimEnd('/');
				var report = new ReportData
				{
					Date = date,
					PreviousDate = date.AddDays(-1),
					BaseUrl = baseUrl,
					AutoRefresh = autoRefresh,
				};

				var sourceRepos = settings.Repos ?? new List<RepoSettings>();
				var sourceRepoNames = new List<string>();

				async Task addReposForSource(string sourceKind, string sourceName)
				{
					var orgRepoNames = new List<string>();
					await loadPagesAsync($"{sourceKind}/{sourceName}/repos?sort=updated", pageElement =>
					{
						var foundLastPage = false;

						foreach (var repoElement in pageElement.EnumerateArray())
						{
							var updatedUtc = ParseDateTime(repoElement.GetProperty("updated_at").GetString());
							if (updatedUtc < startDateTimeUtc)
								foundLastPage = true;
							else
								orgRepoNames.Add(repoElement.GetProperty("full_name").GetString());
						}

						return foundLastPage;
					});
					orgRepoNames.Sort(StringComparer.InvariantCulture);
					sourceRepoNames.AddRange(orgRepoNames);
				}

				foreach (var sourceRepo in sourceRepos)
				{
					switch (sourceRepo)
					{
					case { Name: var name, User: null, Org: null }:
						sourceRepoNames.Add(name);
						break;

					case { Name: null, User: var user, Org: null }:
						await addReposForSource("users", user);
						break;

					case { Name: null, User: null, Org: var org }:
						await addReposForSource("orgs", org);
						break;

					default:
						throw new ApplicationException("Invalid repo source: " + JsonSerializer.Serialize(sourceRepo));
					}
				}

				var usersToExclude = new HashSet<string>();
				foreach (var exclude in settings.Excludes ?? new List<FilterSettings>())
				{
					switch (exclude)
					{
					case { User: var user }:
						usersToExclude.Add(user);
						break;

					default:
						throw new ApplicationException("Invalid exclude: " + JsonSerializer.Serialize(exclude));
					}
				}

				async Task<(IReadOnlyList<JsonElement> Events, DateTime? PreviousDate, bool IsPartial)> loadEventsAsync(string sourceKind, string sourceName)
				{
					var eventElements = new List<JsonElement>();
					DateTime? previousDate = null;
					var foundLastPage = true;

					await loadPagesAsync($"{sourceKind}/{sourceName}/events", pageElement =>
					{
						if (pageElement.GetArrayLength() != 0)
						{
							foreach (var eventElement in pageElement.EnumerateArray())
							{
								var createdUtc = ParseDateTime(eventElement.GetProperty("created_at").GetString());
								if (createdUtc < startDateTimeUtc)
								{
									foundLastPage = true;
									if (previousDate is null)
										previousDate = new DateTimeOffset(createdUtc).ToOffset(timeZoneOffset).Date;
								}
								else if (createdUtc < endDateTimeUtc)
								{
									var eventId = eventElement.GetProperty("id").GetString();
									var actorName = eventElement.GetProperty("actor", "login").GetString();
									if (handledEventIds.Add(eventId) && !usersToExclude.Contains(actorName))
										eventElements.Add(eventElement);
								}
							}
						}
						else
						{
							foundLastPage = true;
						}

						return foundLastPage;
					});

					eventElements.Reverse();
					return (eventElements, previousDate, !foundLastPage);
				}

				foreach (var sourceRepoName in sourceRepoNames)
				{
					var (eventElements, previousDate, isPartial) = await loadEventsAsync("networks", sourceRepoName);

					var repo = new RepoData { Name = sourceRepoName, PreviousDate = previousDate, IsPartial = isPartial };

					foreach (var eventElement in eventElements)
					{
						var repoName = eventElement.GetProperty("repo", "name").GetString();

						string getForkOwner(string r) => sourceRepoName == r ? null : r.Substring(0, r.IndexOf('/'));

						BranchData addBranch(string n, string r)
						{
							var branch = new BranchData
							{
								Name = n,
								RepoName = r,
								ForkOwner = getForkOwner(r)
							};
							repo.Branches.Add(branch);
							return branch;
						}

						BranchData getOrAddBranch(string n, string r) =>
							repo.Branches.LastOrDefault(x => x.Name == n && x.RepoName == r) ?? addBranch(n, r);

						var actorName = eventElement.GetProperty("actor", "login").GetString();
						var payload = eventElement.GetProperty("payload");

						var eventType = eventElement.GetProperty("type").GetString();
						if (eventType == "PushEvent")
						{
							const string branchRefPrefix = "refs/heads/";
							var refName = payload.TryGetProperty("ref")?.GetString();
							if (refName?.StartsWith(branchRefPrefix) == true)
							{
								var branchName = refName.Substring(branchRefPrefix.Length);
								var branch = getOrAddBranch(branchName, repoName);

								var beforeSha = payload.TryGetProperty("before")?.GetString();
								var afterSha = payload.TryGetProperty("head")?.GetString();
								var commitCount = payload.TryGetProperty("size")?.GetInt32() ?? 0;
								var distinctCommitCount = payload.TryGetProperty("distinct_size")?.GetInt32() ?? 0;
								var commits = payload.TryGetProperty("commits")?.EnumerateArray().ToList() ?? new List<JsonElement>();
								var canMerge = commitCount == commits.Count;

								var push = branch.Events.LastOrDefault() as PushEventData;
								if (push == null ||
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
								var branch = getOrAddBranch(branchName, repoName);

								branch.Events.Add(new BranchEventData
								{
									Kind = isDelete ? "delete-branch" : "create-branch",
									RepoName = repoName,
									ActorName = actorName,
								});
							}
							else if (refType == "tag")
							{
								repo.TagEvents.Add(new TagEventData
								{
									Kind = isDelete ? "delete-tag" : "create-tag",
									RepoName = repoName,
									ActorName = actorName,
									TagName = payload.GetProperty("ref").GetString(),
								});
							}
						}
						else if (eventType == "GollumEvent")
						{
							foreach (var page in payload.GetProperty("pages").EnumerateArray())
							{
								var action = page.GetProperty("action").GetString();
								var pageName = page.GetProperty("page_name").GetString();
								var wikiEvent = repo.WikiEvents.LastOrDefault();
								if (wikiEvent == null || wikiEvent.ActorName != actorName || wikiEvent.PageName != pageName)
								{
									wikiEvent = new WikiEventData
									{
										Kind = $"{action}-wiki-page",
										RepoName = repoName,
										ActorName = actorName,
										PageName = pageName,
									};
									repo.WikiEvents.Add(wikiEvent);
								}

								// leave created kind, but use last title
								wikiEvent.PageTitle = page.GetProperty("title").GetString();
							}
						}
						else if (eventType == "CommitCommentEvent")
						{
							var data = payload.GetProperty("comment");
							var commentId = data.GetProperty("id").GetInt32();
							var sha = data.GetProperty("commit_id").GetString();
							var filePath = data.TryGetProperty("path")?.GetString();
							var position = data.TryGetProperty("position")?.GetNullOrInt32();

							var commit = repo.CommentedCommits.SingleOrDefault(x => x.Sha == sha);
							if (commit == null)
								repo.CommentedCommits.Add(commit = new CommentedCommitData { RepoName = repoName, Sha = sha });

							var conversation = commit.Conversations.SingleOrDefault(x => x.FilePath == filePath && x.Position == position?.ToString());
							if (conversation == null)
								commit.Conversations.Add(conversation = new ConversationData { FilePath = filePath, Position = position.ToString() });

							conversation.Comments.Add(new CommentData
							{
								ActorName = actorName,
								Url = $"{baseUrl}/{repoName}/commit/{sha}#{(filePath == null ? "commitcomment-" : "r")}{commentId}",
								Body = data.GetProperty("body").GetString(),
							});
						}
						else if (eventType == "PullRequestEvent" || eventType == "PullRequestReviewCommentEvent")
						{
							var pullRequest = payload.GetProperty("pull_request");
							var number = pullRequest.GetProperty("number").GetInt32();
							var branchName = pullRequest.GetProperty("head", "ref").GetString();
							var headRepoName = pullRequest.GetProperty("head", "repo", "full_name").GetString();
							var branch = getOrAddBranch(branchName, headRepoName);

							if (branch.PullRequest != null && branch.PullRequest.Number != number)
								branch = addBranch(branchName, headRepoName);

							branch.PullRequest = new PullRequestData
							{
								Number = number,
								RepoName = repoName,
							};

							var title = pullRequest.TryGetProperty("title")?.GetString();
							if (title != null)
								branch.PullRequest.Title = title;

							var eventKindPrefix = eventType switch
							{
								"PullRequestEvent" => "pull-request-",
								"PullRequestReviewCommentEvent" => "pull-request-review-comment-",
								_ => throw new InvalidOperationException(),
							};
							var eventKind = eventKindPrefix + payload.GetProperty("action").GetString();

							if (eventKind == "pull-request-opened" || eventKind == "pull-request-closed" ||
								eventKind == "pull-request-review-comment-created" || eventKind == "pull-request-review-comment-edited")
							{
								BranchData baseBranch = null;

								if (eventKind == "pull-request-closed" && pullRequest.GetProperty("merged").GetBoolean())
								{
									eventKind = "pull-request-merged";

									var baseBranchName = pullRequest.GetProperty("base", "ref").GetString();

									var baseRepoName = pullRequest.GetProperty("base", "repo", "full_name").GetString();
									if (repoName != baseRepoName)
										throw new InvalidOperationException("Unexpected pull request base.");

									baseBranch = new BranchData { Name = baseBranchName, RepoName = repoName };
								}

								if (eventType == "PullRequestReviewCommentEvent")
								{
									var commentElement = payload.GetProperty("comment");
									var commentId = commentElement.GetProperty("id").GetInt32();

									var filePath = commentElement.GetProperty("path").GetString();
									var positionBefore = commentElement.GetProperty("original_position").GetNullOrInt32();
									var positionAfter = commentElement.GetProperty("position").GetNullOrInt32();
									var position = $"{positionBefore}:{positionAfter}";

									var conversation = branch.Events.OfType<BranchEventData>().Select(x => x.Conversation).Where(x => x != null)
										.SingleOrDefault(x => x.FilePath == filePath && x.Position == position);
									if (conversation == null)
									{
										conversation = new ConversationData { FilePath = filePath, Position = position };

										var branchEvent = new BranchEventData
										{
											Kind = eventKind,
											RepoName = branch.RepoName,
											ActorName = actorName,
											BaseBranch = baseBranch,
											Conversation = conversation,
										};
										branch.Events.Add(branchEvent);
									}
									conversation.Comments.Add(new CommentData
									{
										ActorName = actorName,
										Url = $"{baseUrl}/{repoName}/pull/{number}#discussion_r{commentId}",
										Body = commentElement.GetProperty("body").GetString(),
									});
								}
								else
								{
									branch.Events.Add(new BranchEventData
									{
										Kind = eventKind,
										RepoName = branch.RepoName,
										ActorName = actorName,
										BaseBranch = baseBranch,
									});
								}
							}
						}
					}

					if (repo.Branches.Count != 0)
					{
						var sortedBranches = repo.Branches
							.OrderBy(x => x.PullRequest != null ? 1 : x.ForkOwner == null ? 2 : 3)
							.ThenBy(x => x.PullRequest?.Number ?? 0)
							.ThenBy(x => x.ForkOwner ?? "", StringComparer.InvariantCulture)
							.ToList();
						repo.Branches.Clear();
						repo.Branches.AddRange(sortedBranches);

						report.Repos.Add(repo);
					}
				}

				var culture = settings.Culture == null ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(settings.Culture);

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
			catch (Exception exception)
			{
				var templateText = GetEmbeddedResourceText("GitHubDigestBuilder.exception.scriban-html");
				var template = Template.Parse(templateText);

				var templateContext = new TemplateContext { StrictVariables = true };

				var scriptObject = new ScriptObject();
				scriptObject.Import(typeof(ReportFunctions));
				scriptObject.Import(new { message = exception.ToString() });
				templateContext.PushGlobal(scriptObject);

				var reportHtml = template.Render(templateContext);

				await File.WriteAllTextAsync(outputFile, reportHtml);

				throw;
			}
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
				await RunAsync(new ArgsReader(args));
				return 0;
			}
			catch (ArgsReaderException exception)
			{
				Console.Error.WriteLine(exception.Message);
				return 1;
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
