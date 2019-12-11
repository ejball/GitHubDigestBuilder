using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
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
			// read command-line arguments
			var dateString = args.ReadOption("date");
			var autoRefresh = args.ReadFlag("auto-refresh");
			var authToken = args.ReadOption("auth");
			var configFilePath = args.ReadArgument();
			args.VerifyComplete();

			// find config file
			configFilePath = Path.GetFullPath(configFilePath);
			if (!File.Exists(configFilePath))
				throw new ApplicationException("Configuration file not found.");
			var configFileDirectory = Path.GetDirectoryName(configFilePath);

			// deserialize config file
			var settings = JsonSerializer.Deserialize<DigestSettings>(
				ConvertYamlToJson(File.ReadAllText(configFilePath)),
				new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

			// determine date/time range in UTC
			var timeZoneOffset = settings.TimeZoneOffsetHours != null ? TimeSpan.FromHours(settings.TimeZoneOffsetHours.Value) : DateTimeOffset.Now.Offset;
			var now = new DateTimeOffset(DateTime.UtcNow).ToOffset(timeZoneOffset);
			var todayIso = now.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			var date = dateString != null ? ParseDate(dateString) : now.Date.AddDays(-1.0);
			var dateIso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
			var startDateTimeUtc = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, timeZoneOffset).UtcDateTime;
			var endDateTimeUtc = startDateTimeUtc.AddDays(1.0);

			// determine output file
			var outputFile = Path.Combine(configFileDirectory, settings.OutputDirectory ?? ".", $"{dateIso}.html");
			Directory.CreateDirectory(Path.GetDirectoryName(outputFile));

			try
			{
				// prepare HTTP client
				var httpClient = new HttpClient();
				httpClient.DefaultRequestHeaders.UserAgent.Add(ProductInfoHeaderValue.Parse("GitHubDigestBuilder"));

				authToken ??= settings.GitHub?.AuthToken;
				if (!string.IsNullOrWhiteSpace(authToken))
					httpClient.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"token {authToken}");

				var apiBase = (settings.GitHub?.ApiUrl ?? "https://api.github.com").TrimEnd('/');
				var warnings = new List<string>();

				using var sha1 = SHA1.Create();
				var cacheDirectory = Path.Combine(Path.GetTempPath(), "GitHubDigestBuilder",
					BitConverter.ToString(sha1.ComputeHash(Encoding.UTF8.GetBytes($"{apiBase} {authToken}"))).Replace("-", "")[..16]);
				Directory.CreateDirectory(cacheDirectory);

				// don't process the same event twice
				var handledEventIds = new HashSet<string>();

				async Task<PagedDownloadResult> loadPagesAsync(string url, int maxPageCount, Func<JsonElement, bool> isLastPage = null)
				{
					var cacheName = Regex.Replace(Regex.Replace(url, @"\?.*$", ""), @"/+", "_").Trim('_');
					var cacheFile = Path.Combine(cacheDirectory, $"{cacheName}.json");
					var cacheElement = default(JsonElement);
					string etag = null;

					if (File.Exists(cacheFile))
					{
						await using var cacheStream = File.OpenRead(cacheFile);
						cacheElement = (await JsonDocument.ParseAsync(cacheStream)).RootElement;

						// if we're asking for the same date, and the date is not today, assume the data is still good
						var cacheDateIso = cacheElement.GetProperty("date").GetString();
						if (cacheDateIso == dateIso && dateIso != todayIso)
						{
							return new PagedDownloadResult(
								Enum.Parse<DownloadStatus>(cacheElement.GetProperty("status").GetString()),
								cacheElement.GetProperty("items").EnumerateArray().ToList());
						}

						// don't use the cache if we may have stopped early and we're asking for an older report
						if (isLastPage == null || string.CompareOrdinal(dateIso, cacheDateIso) >= 0)
							etag = cacheElement.GetProperty("etag").GetString();
					}

					var foundLastPage = false;
					var items = new List<JsonElement>();

					for (var pageNumber = 1; pageNumber <= maxPageCount && !foundLastPage; pageNumber++)
					{
						var pageParameter = pageNumber == 1 ? "" : $"{(url.Contains('?') ? '&' : '?')}page={pageNumber}";
						var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/{url}{pageParameter}");
						request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/vnd.github.v3+json"));
						if (pageNumber == 1 && etag != null)
							request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));

						var response = await httpClient.SendAsync(request);

						if (pageNumber == 1 && etag != null && response.StatusCode == HttpStatusCode.NotModified)
						{
							return new PagedDownloadResult(
								Enum.Parse<DownloadStatus>(cacheElement.GetProperty("status").GetString()),
								cacheElement.GetProperty("items").EnumerateArray().ToList());
						}

						if (response.StatusCode == HttpStatusCode.NotFound)
							return new PagedDownloadResult(DownloadStatus.NotFound);

						if (response.StatusCode != HttpStatusCode.OK)
							throw new InvalidOperationException($"Unexpected status code: {response.StatusCode}");

						if (pageNumber == 1)
							etag = response.Headers.ETag.Tag;

						await using var pageStream = await response.Content.ReadAsStreamAsync();
						var pageDocument = await JsonDocument.ParseAsync(pageStream);

						foundLastPage = pageDocument.RootElement.GetArrayLength() == 0 || isLastPage?.Invoke(pageDocument.RootElement) == true;
						items.AddRange(pageDocument.RootElement.EnumerateArray());
					}

					var status = foundLastPage ? DownloadStatus.Success : DownloadStatus.TooMuchActivity;
					{
						await using var cacheStream = File.Open(cacheFile, FileMode.Create, FileAccess.Write);
						await JsonSerializer.SerializeAsync(cacheStream, new
						{
							status = status.ToString(),
							etag,
							date = dateIso,
							items,
						}, new JsonSerializerOptions { WriteIndented = true });
					}

					return new PagedDownloadResult(status, items);
				}

				var baseUrl = (settings.GitHub?.WebUrl ?? "https://github.com").TrimEnd('/');

				var sourceRepoNames = new List<string>();
				var sourceRepoIndices = new Dictionary<string, int>();

				void addRepoForSource(string repoName, int sourceIndex)
				{
					if (!sourceRepoIndices.ContainsKey(repoName))
					{
						sourceRepoNames.Add(repoName);
						sourceRepoIndices.Add(repoName, sourceIndex);
					}
				}

				async Task addReposForSource(string sourceKind, string sourceName, int sourceIndex)
				{
					var orgRepoNames = new HashSet<string>();
					var result = await loadPagesAsync($"{sourceKind}/{sourceName}/repos?sort=updated&per_page=100", maxPageCount: 100);

					foreach (var repoElement in result.Elements)
					{
						if (!repoElement.GetProperty("private").GetBoolean() &&
							!repoElement.GetProperty("archived").GetBoolean() &&
							!repoElement.GetProperty("disabled").GetBoolean())
						{
							orgRepoNames.Add(repoElement.GetProperty("full_name").GetString());
						}
					}

					foreach (var orgRepoName in orgRepoNames)
						addRepoForSource(orgRepoName, sourceIndex);

					if (result.Status == DownloadStatus.TooMuchActivity)
						warnings.Add($"Too many updated repositories found for {sourceName}.");
					else if (result.Status == DownloadStatus.NotFound)
						warnings.Add($"Failed to find repositories for {sourceName}.");
				}

				var settingsRepos = settings.Repos ?? new List<RepoSettings>();
				foreach (var (settingsRepo, sourceIndex) in settingsRepos.Select((x, i) => (x, i)))
				{
					switch (settingsRepo)
					{
					case { Name: string name, User: null, Org: null }:
						addRepoForSource(name, sourceIndex);
						break;

					case { Name: null, User: string user, Org: null }:
						await addReposForSource("users", user, sourceIndex);
						break;

					case { Name: null, User: null, Org: string org }:
						await addReposForSource("orgs", org, sourceIndex);
						break;

					default:
						throw new ApplicationException("Invalid repo source: " + JsonSerializer.Serialize(settingsRepo));
					}
				}

				var sourceUserNames = new List<string>();

				var settingsUsers = settings.Users ?? new List<UserSettings>();
				foreach (var settingsUser in settingsUsers)
				{
					switch (settingsUser)
					{
					case { Name: string name }:
						if (!sourceUserNames.Contains(name))
							sourceUserNames.Add(name);
						break;

					default:
						throw new ApplicationException("Invalid user source: " + JsonSerializer.Serialize(settingsUser));
					}
				}

				var usersToExclude = new HashSet<string>();
				foreach (var exclude in settings.Excludes ?? new List<FilterSettings>())
				{
					switch (exclude)
					{
					case { User: string user }:
						usersToExclude.Add(user);
						break;

					default:
						throw new ApplicationException("Invalid exclude: " + JsonSerializer.Serialize(exclude));
					}
				}

				async Task<(IReadOnlyList<RawEventData> Events, DownloadStatus Status)> loadEventsAsync(string sourceKind, string sourceName)
				{
					var events = new List<RawEventData>();
					var result = await loadPagesAsync($"{sourceKind}/{sourceName}/events", maxPageCount: 10,
						isLastPage: page => page.EnumerateArray().Any(x => ParseDateTime(x.GetProperty("created_at").GetString()) < startDateTimeUtc));

					foreach (var eventElement in result.Elements)
					{
						var createdUtc = ParseDateTime(eventElement.GetProperty("created_at").GetString());
						if (createdUtc >= startDateTimeUtc && createdUtc < endDateTimeUtc)
						{
							var eventId = eventElement.GetProperty("id").GetString();
							var eventType = eventElement.GetProperty("type").GetString();
							var actorName = eventElement.GetProperty("actor", "login").GetString();
							var repoName = eventElement.GetProperty("repo", "name").GetString();
							var payload = eventElement.GetProperty("payload");

							// skip some events from network (but check event again)
							var shouldSkipFromNetwork = eventType switch
							{
								"CreateEvent" => payload.GetProperty("ref_type").GetString() == "tag",
								"DeleteEvent" => payload.GetProperty("ref_type").GetString() == "tag",
								"PullRequestEvent" => true,
								"PullRequestReviewCommentEvent" => true,
								_ => false,
							};
							if (shouldSkipFromNetwork && repoName != sourceName)
								continue;

							if (!handledEventIds.Add(eventId))
								continue;

							if (usersToExclude.Contains(actorName))
								continue;

							events.Add(new RawEventData
							{
								EventId = eventId,
								EventType = eventType,
								ActorName = actorName,
								CreatedUtc = createdUtc,
								RepoName = repoName,
								SourceRepoName = sourceKind == "users" ? repoName : sourceName,
								Payload = payload,
							});
						}
					}

					events.Reverse();
					return (events, result.Status);
				}

				var rawEvents = new List<RawEventData>();

				foreach (var sourceRepoName in sourceRepoNames)
				{
					var (rawNetworkEvents, networkStatus) = await loadEventsAsync("networks", sourceRepoName);
					rawEvents.AddRange(rawNetworkEvents);

					if (networkStatus != DownloadStatus.Success)
					{
						var (rawRepoEvents, repoStatus) = await loadEventsAsync("repos", sourceRepoName);
						rawEvents.AddRange(rawRepoEvents);
						if (repoStatus == DownloadStatus.TooMuchActivity)
							warnings.Add($"{sourceRepoName} repository had too much activity.");
						else if (networkStatus == DownloadStatus.TooMuchActivity)
							warnings.Add($"{sourceRepoName} repository network had too much activity.");
						else if (repoStatus == DownloadStatus.NotFound)
							warnings.Add($"{sourceRepoName} repository not found.");
					}
				}

				foreach (var sourceUserName in sourceUserNames)
				{
					var (rawUserEvents, userStatus) = await loadEventsAsync("users", sourceUserName);
					rawEvents.AddRange(rawUserEvents);
					if (userStatus == DownloadStatus.TooMuchActivity)
						warnings.Add($"{sourceUserName} user had too much activity.");
					else if (userStatus == DownloadStatus.NotFound)
						warnings.Add($"{sourceUserName} user not found.");
				}

				rawEvents = rawEvents.OrderBy(x => x.CreatedUtc).ToList();

				var branches = new List<BranchData>();
				var tagEvents = new List<TagEventData>();
				var wikiEvents = new List<WikiEventData>();
				var commentedCommits = new List<CommentedCommitData>();

				void updateBranch(BranchData branch, string branchName, string repoName, string sourceRepoName, int? pullRequestNumber = null, string pullRequestRepoName = null)
				{
					sourceRepoName ??= repoName;

					branch.Name ??= branchName;
					branch.RepoName ??= repoName;
					branch.SourceRepoName ??= sourceRepoName;
					branch.ForkOwner ??= repoName == null || repoName == sourceRepoName ? null : repoName.Substring(0, repoName.IndexOf('/'));

					if (pullRequestNumber != null)
					{
						branch.PullRequest ??= new PullRequestData
						{
							Number = pullRequestNumber.Value,
							RepoName = pullRequestRepoName,
						};
						branch.SourceRepoName = pullRequestRepoName;
					}
				}

				BranchData addBranch(string branchName, string repoName, string sourceRepoName, int? pullRequestNumber = null, string pullRequestRepoName = null)
				{
					var branch = new BranchData();
					updateBranch(branch, branchName, repoName, sourceRepoName, pullRequestNumber, pullRequestRepoName);
					branches.Add(branch);
					return branch;
				}

				BranchData getOrAddBranch(string branchName, string repoName, string sourceRepoName, int? pullRequestNumber = null, string pullRequestRepoName = null)
				{
					var branch = branches.LastOrDefault(x => (repoName != null && branchName != null && x.RepoName == repoName && x.Name == branchName) ||
						(pullRequestNumber != null && pullRequestRepoName != null && x.PullRequest?.Number == pullRequestNumber && x.PullRequest?.RepoName == pullRequestRepoName));

					if (pullRequestNumber != null && branch?.PullRequest != null && branch.PullRequest.Number != pullRequestNumber)
						branch = null;

					if (branch == null)
						branch = addBranch(branchName, repoName, sourceRepoName, pullRequestNumber, pullRequestRepoName);

					updateBranch(branch, branchName, repoName, sourceRepoName, pullRequestNumber, pullRequestRepoName);

					return branch;
				}

				foreach (var rawEvent in rawEvents)
				{
					var repoName = rawEvent.RepoName;
					var payload = rawEvent.Payload;
					var eventType = rawEvent.EventType;
					var sourceRepoName = rawEvent.SourceRepoName;
					var actorName = rawEvent.ActorName;

					if (eventType == "PushEvent")
					{
						const string branchRefPrefix = "refs/heads/";
						var refName = payload.TryGetProperty("ref")?.GetString();
						if (refName?.StartsWith(branchRefPrefix) == true)
						{
							var branchName = refName.Substring(branchRefPrefix.Length);
							var branch = getOrAddBranch(branchName, repoName, sourceRepoName);

							if (branch.PullRequest != null && branch.PullRequest.IsClosed)
								branch = addBranch(branchName, repoName, sourceRepoName);

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
					else if (eventType == "CreateEvent")
					{
						var refType = payload.GetProperty("ref_type").GetString();
						if (refType == "branch")
						{
							var branchName = payload.GetProperty("ref").GetString();
							var branch = getOrAddBranch(branchName, repoName, sourceRepoName);

							branch.Events.Add(new BranchEventData
							{
								Kind = "create-branch",
								RepoName = repoName,
								ActorName = actorName,
							});
						}
						else if (refType == "tag")
						{
							tagEvents.Add(new TagEventData
							{
								Kind = "create-tag",
								RepoName = repoName,
								ActorName = actorName,
								Tag = new TagData { Name = payload.GetProperty("ref").GetString(), RepoName = repoName },
							});
						}
					}
					else if (eventType == "GollumEvent")
					{
						foreach (var page in payload.GetProperty("pages").EnumerateArray())
						{
							var action = page.GetProperty("action").GetString();
							var pageName = page.GetProperty("page_name").GetString();
							var wikiEvent = wikiEvents.LastOrDefault();
							if (wikiEvent == null || wikiEvent.ActorName != actorName || wikiEvent.PageName != pageName)
							{
								wikiEvent = new WikiEventData
								{
									Kind = $"{action}-wiki-page",
									RepoName = repoName,
									ActorName = actorName,
									PageName = pageName,
								};
								wikiEvents.Add(wikiEvent);
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

						var commit = commentedCommits.SingleOrDefault(x => x.Sha == sha);
						if (commit == null)
							commentedCommits.Add(commit = new CommentedCommitData { RepoName = repoName, SourceRepoName = sourceRepoName, Sha = sha });

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
					else if (eventType == "PullRequestEvent" || eventType == "PullRequestReviewCommentEvent" || eventType == "IssueCommentEvent")
					{
						var pullRequest = payload.TryGetProperty("pull_request");
						var issue = payload.TryGetProperty("issue");
						var pullRequestOrIssue = pullRequest ?? issue ?? throw new InvalidOperationException("Missing pull request or issue.");
						var number = pullRequestOrIssue.GetProperty("number").GetInt32();
						var branchName = pullRequest?.GetProperty("head", "ref").GetString();
						var headRepoName = pullRequest?.TryGetProperty("head", "repo", "full_name")?.GetString();
						if (headRepoName == null)
							continue;
						var branch = getOrAddBranch(branchName, headRepoName, sourceRepoName, number, repoName);

						var title = pullRequestOrIssue.TryGetProperty("title")?.GetString();
						if (title != null)
							branch.PullRequest.Title = title;

						var body = pullRequestOrIssue.TryGetProperty("body")?.GetString();
						if (title != null)
							branch.PullRequest.Body = body;

						var eventKindPrefix = eventType switch
						{
							"PullRequestEvent" => "pull-request-",
							"PullRequestReviewCommentEvent" => "pull-request-review-comment-",
							"IssueCommentEvent" => "pull-request-comment-",
							_ => throw new InvalidOperationException(),
						};
						var eventKind = eventKindPrefix + payload.GetProperty("action").GetString();

						if (eventKind == "pull-request-opened" || eventKind == "pull-request-closed" ||
							eventKind == "pull-request-comment-created" || eventKind == "pull-request-review-comment-created")
						{
							BranchData baseBranch = null;

							if (eventKind == "pull-request-closed")
							{
								branch.PullRequest.IsClosed = true;

								if (pullRequest.Value.GetProperty("merged").GetBoolean())
								{
									eventKind = "pull-request-merged";

									var baseBranchName = pullRequest.Value.GetProperty("base", "ref").GetString();

									var baseRepoName = pullRequest.Value.GetProperty("base", "repo", "full_name").GetString();
									if (repoName != baseRepoName)
										throw new InvalidOperationException("Unexpected pull request base.");

									baseBranch = new BranchData { Name = baseBranchName, RepoName = repoName };
								}
							}

							if (eventKind == "pull-request-comment-created" || eventKind == "pull-request-review-comment-created")
							{
								var commentElement = payload.GetProperty("comment");
								var commentId = commentElement.GetProperty("id").GetInt32();
								var commentBody = commentElement.GetProperty("body").GetString();

								ConversationData conversation = null;
								string filePath = null;
								string position = null;

								if (eventType == "PullRequestReviewCommentEvent")
								{
									filePath = commentElement.GetProperty("path").GetString();
									var positionBefore = commentElement.GetProperty("original_position").GetNullOrInt32();
									var positionAfter = commentElement.GetProperty("position").GetNullOrInt32();
									position = positionBefore != null ? $"{positionBefore}" : $":{positionAfter}";

									conversation = branch.Events.OfType<BranchEventData>().Select(x => x.Conversation).Where(x => x != null)
										.SingleOrDefault(x => x.FilePath == filePath && x.Position == position);
								}

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
									Url = $"{baseUrl}/{repoName}/pull/{number}#{(eventType == "PullRequestReviewCommentEvent" ? "discussion_r" : "issuecomment-")}{commentId}",
									Body = commentBody,
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

				var repos = new List<RepoData>();

				RepoData getOrAddRepo(string n)
				{
					var repo = repos.LastOrDefault(x => x.Name == n);
					if (repo == null)
					{
						repo = new RepoData { Name = n };
						repos.Add(repo);
					}

					return repo;
				}

				foreach (var branch in branches
					.OrderBy(x => x.PullRequest != null ? 1 : x.ForkOwner == null ? 2 : 3)
					.ThenBy(x => x.PullRequest?.Number ?? 0)
					.ThenBy(x => x.ForkOwner ?? "", StringComparer.InvariantCulture))
				{
					getOrAddRepo(branch.SourceRepoName).Branches.Add(branch);
				}

				foreach (var tagEvent in tagEvents)
					getOrAddRepo(tagEvent.RepoName).TagEvents.Add(tagEvent);

				foreach (var wikiEvent in wikiEvents)
					getOrAddRepo(wikiEvent.RepoName).WikiEvents.Add(wikiEvent);

				foreach (var commentedCommit in commentedCommits)
					getOrAddRepo(commentedCommit.SourceRepoName).CommentedCommits.Add(commentedCommit);

				var report = new ReportData
				{
					Date = date,
					PreviousDate = date.AddDays(-1),
					BaseUrl = baseUrl,
					AutoRefresh = autoRefresh,
					Now = now,
				};

				report.Repos.AddRange(repos
					.OrderBy(x => sourceRepoIndices.TryGetValue(x.Name, out var i) ? i : int.MaxValue)
					.ThenBy(x => x.Name, StringComparer.InvariantCulture));
				report.Warnings.AddRange(warnings);

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

		private enum DownloadStatus
		{
			Success,
			NotFound,
			TooMuchActivity,
		}

		private sealed class PagedDownloadResult
		{
			public PagedDownloadResult(DownloadStatus status, IReadOnlyList<JsonElement> elements = null)
			{
				Status = status;
				Elements = elements ?? Array.Empty<JsonElement>();
			}

			public DownloadStatus Status { get; }

			public IReadOnlyList<JsonElement> Elements { get; }
		}
	}
}
