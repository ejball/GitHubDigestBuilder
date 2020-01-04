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
			if (configFilePath == null)
				throw new ApplicationException("Missing configuration file.");
			configFilePath = Path.GetFullPath(configFilePath);
			if (!File.Exists(configFilePath))
				throw new ApplicationException("Configuration file not found.");
			var configFileDirectory = Path.GetDirectoryName(configFilePath)!;

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
				const int cacheVersion = 2;

				using var sha1 = SHA1.Create();
				var cacheDirectory = Path.Combine(Path.GetTempPath(), "GitHubDigestBuilder",
					BitConverter.ToString(sha1.ComputeHash(Encoding.UTF8.GetBytes($"{cacheVersion} {apiBase} {authToken}"))).Replace("-", "")[..16]);
				Directory.CreateDirectory(cacheDirectory);

				// don't process the same event twice
				var handledEventIds = new HashSet<string>();

				async Task<PagedDownloadResult> loadPagesAsync(string url, string[] accepts, int maxPageCount, Func<JsonElement, bool>? isLastPage = null)
				{
					var cacheName = Regex.Replace(Regex.Replace(url, @"\?.*$", ""), @"/+", "_").Trim('_');
					var cacheFile = Path.Combine(cacheDirectory, $"{cacheName}.json");
					var cacheElement = default(JsonElement);
					string? etag = null;

					if (File.Exists(cacheFile))
					{
						await using var cacheReadStream = File.OpenRead(cacheFile);
						cacheElement = (await JsonDocument.ParseAsync(cacheReadStream)).RootElement;

						// if we're asking for the same date, and the cache was generated for a previous day, assume it is still good
						var cacheDateIso = cacheElement.GetProperty("date").GetString();
						var cacheTodayIso = cacheElement.TryGetProperty("today")?.GetString();
						if (cacheDateIso == dateIso && string.CompareOrdinal(cacheDateIso, cacheTodayIso) < 0)
						{
							return new PagedDownloadResult(
								Enum.Parse<DownloadStatus>(cacheElement.GetProperty("status").GetString()),
								cacheElement.GetProperty("items").EnumerateArray().ToList());
						}

						// don't use the cache if we may have stopped early and we're asking for an older report
						if (isLastPage == null || string.CompareOrdinal(dateIso, cacheDateIso) >= 0)
							etag = cacheElement.GetProperty("etag").GetString();
					}

					var status = DownloadStatus.TooMuchActivity;
					var items = new List<JsonElement>();

					for (var pageNumber = 1; pageNumber <= maxPageCount; pageNumber++)
					{
						var pageParameter = pageNumber == 1 ? "" : $"{(url.Contains('?') ? '&' : '?')}page={pageNumber}";
						var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/{url}{pageParameter}");
						foreach (var accept in accepts)
							request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(accept));
						if (pageNumber == 1 && etag != null)
							request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));

						var response = await httpClient.SendAsync(request);
						Console.WriteLine($"{request.RequestUri.AbsoluteUri} [{response.StatusCode}]");

						if (pageNumber == 1 && etag != null && response.StatusCode == HttpStatusCode.NotModified)
						{
							status = Enum.Parse<DownloadStatus>(cacheElement.GetProperty("status").GetString());
							items.AddRange(cacheElement.GetProperty("items").EnumerateArray());
							break;
						}

						if (response.StatusCode == HttpStatusCode.NotFound)
							return new PagedDownloadResult(DownloadStatus.NotFound);

						if (response.StatusCode != HttpStatusCode.OK)
							throw new InvalidOperationException($"Unexpected status code: {response.StatusCode}");

						if (pageNumber == 1)
							etag = response.Headers.ETag.Tag;

						await using var pageStream = await response.Content.ReadAsStreamAsync();
						var pageDocument = await JsonDocument.ParseAsync(pageStream);

						items.AddRange(pageDocument.RootElement.EnumerateArray());

						if (pageDocument.RootElement.GetArrayLength() == 0 || isLastPage?.Invoke(pageDocument.RootElement) == true)
						{
							status = DownloadStatus.Success;
							break;
						}
					}

					await using var cacheWriteStream = File.Open(cacheFile, FileMode.Create, FileAccess.Write);
					await JsonSerializer.SerializeAsync(cacheWriteStream, new
					{
						status = status.ToString(),
						etag,
						date = dateIso,
						today = todayIso,
						items,
					}, new JsonSerializerOptions { WriteIndented = true });

					return new PagedDownloadResult(status, items);
				}

				var webBase = (settings.GitHub?.WebUrl ?? "https://github.com").TrimEnd('/');

				var report = new ReportData
				{
					Date = date,
					PreviousDate = date.AddDays(-1),
					WebBase = webBase,
					AutoRefresh = autoRefresh,
					Now = now,
				};

				void addWarning(string text)
				{
					if (!report.Warnings.Contains(text))
						report.Warnings.Add(text);
				}

				var sourceGitHubDigestBuilders = new List<string>();
				var sourceRepoIndices = new Dictionary<string, int>();

				void addRepoForSource(string GitHubDigestBuilder, int sourceIndex)
				{
					if (!sourceRepoIndices.ContainsKey(GitHubDigestBuilder))
					{
						sourceGitHubDigestBuilders.Add(GitHubDigestBuilder);
						sourceRepoIndices.Add(GitHubDigestBuilder, sourceIndex);
					}
				}

				async Task addReposForSource(string sourceKind, string sourceName, int sourceIndex, string? topic)
				{
					var orgGitHubDigestBuilders = new HashSet<string>();
					var result = await loadPagesAsync($"{sourceKind}/{sourceName}/repos?sort=updated&per_page=100",
						accepts: new[] { "application/vnd.github.mercy-preview+json" }, maxPageCount: 100);

					foreach (var repoElement in result.Elements)
					{
						if (!repoElement.GetProperty("private").GetBoolean() &&
							!repoElement.GetProperty("archived").GetBoolean() &&
							!repoElement.GetProperty("disabled").GetBoolean() &&
							(topic == null || repoElement.GetProperty("topics").EnumerateArray().Select(x => x.GetString()).Any(x => x == topic)))
						{
							orgGitHubDigestBuilders.Add(repoElement.GetProperty("full_name").GetString());
						}
					}

					foreach (var orgGitHubDigestBuilder in orgGitHubDigestBuilders)
						addRepoForSource(orgGitHubDigestBuilder, sourceIndex);

					if (result.Status == DownloadStatus.TooMuchActivity)
						addWarning($"Too many updated repositories found for {sourceName}.");
					else if (result.Status == DownloadStatus.NotFound)
						addWarning($"Failed to find repositories for {sourceName}.");
				}

				var settingsRepos = settings.Repos ?? new List<RepoSettings>();
				foreach (var (settingsRepo, sourceIndex) in settingsRepos.Select((x, i) => (x, i)))
				{
					switch (settingsRepo)
					{
					case { Name: string name, User: null, Org: null, Topic: null }:
						addRepoForSource(name, sourceIndex);
						break;

					case { Name: null, User: string user, Org: null }:
						await addReposForSource("users", user, sourceIndex, topic: settingsRepo.Topic);
						break;

					case { Name: null, User: null, Org: string org }:
						await addReposForSource("orgs", org, sourceIndex, topic: settingsRepo.Topic);
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
					var result = await loadPagesAsync($"{sourceKind}/{sourceName}/events",
						accepts: new[] { "application/vnd.github.v3+json" }, maxPageCount: 10,
						isLastPage: page => page.EnumerateArray().Any(x => ParseDateTime(x.GetProperty("created_at").GetString()) < startDateTimeUtc));

					foreach (var eventElement in result.Elements)
					{
						var createdUtc = ParseDateTime(eventElement.GetProperty("created_at").GetString());
						if (createdUtc >= startDateTimeUtc && createdUtc < endDateTimeUtc)
						{
							var eventId = eventElement.GetProperty("id").GetString();
							var eventType = eventElement.GetProperty("type").GetString();
							var actorName = eventElement.GetProperty("actor", "login").GetString();
							var GitHubDigestBuilder = eventElement.GetProperty("repo", "name").GetString();
							var payload = eventElement.GetProperty("payload");

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
								RepoName = GitHubDigestBuilder,
								Payload = payload,
							});
						}
					}

					events.Reverse();
					return (events, result.Status);
				}

				async Task<(IReadOnlyList<RawEventData> Events, DownloadStatus Status)> loadIssueEventsAsync(string GitHubDigestBuilder)
				{
					var events = new List<RawEventData>();
					var result = await loadPagesAsync($"repos/{GitHubDigestBuilder}/issues/events?per_page=100",
						accepts: new[] { "application/vnd.github.v3+json" }, maxPageCount: 10,
						isLastPage: page => page.EnumerateArray().Any(x => ParseDateTime(x.GetProperty("created_at").GetString()) < startDateTimeUtc));

					foreach (var eventElement in result.Elements)
					{
						var createdUtc = ParseDateTime(eventElement.GetProperty("created_at").GetString());
						if (createdUtc >= startDateTimeUtc && createdUtc < endDateTimeUtc)
						{
							var eventId = "i" + eventElement.GetProperty("id").GetInt64();
							var actorName = eventElement.GetProperty("actor", "login").GetString();

							if (!handledEventIds.Add(eventId))
								continue;

							if (usersToExclude.Contains(actorName))
								continue;

							events.Add(new RawEventData
							{
								EventId = eventId,
								EventType = "IssuesApiEvent",
								ActorName = actorName,
								CreatedUtc = createdUtc,
								RepoName = GitHubDigestBuilder,
								Payload = eventElement,
							});
						}
					}

					events.Reverse();
					return (events, result.Status);
				}

				var rawEvents = new List<RawEventData>();

				foreach (var sourceGitHubDigestBuilder in sourceGitHubDigestBuilders)
				{
					var (rawRepoEvents, repoStatus) = await loadEventsAsync("repos", sourceGitHubDigestBuilder);
					rawEvents.AddRange(rawRepoEvents);
					if (repoStatus == DownloadStatus.TooMuchActivity)
						addWarning($"{sourceGitHubDigestBuilder} repository had too much activity.");
					else if (repoStatus == DownloadStatus.NotFound)
						addWarning($"{sourceGitHubDigestBuilder} repository not found.");

					var (rawRepoIssueEvents, repoIssueStatus) = await loadIssueEventsAsync(sourceGitHubDigestBuilder);
					rawEvents.AddRange(rawRepoIssueEvents);
					if (repoIssueStatus == DownloadStatus.TooMuchActivity)
						addWarning($"{sourceGitHubDigestBuilder} repository had too much issue activity.");
					else if (repoIssueStatus == DownloadStatus.NotFound)
						addWarning($"{sourceGitHubDigestBuilder} repository not found.");
				}

				foreach (var sourceUserName in sourceUserNames)
				{
					var (rawUserEvents, userStatus) = await loadEventsAsync("users", sourceUserName);
					rawEvents.AddRange(rawUserEvents);
					if (userStatus == DownloadStatus.TooMuchActivity)
						addWarning($"{sourceUserName} user had too much activity.");
					else if (userStatus == DownloadStatus.NotFound)
						addWarning($"{sourceUserName} user not found.");
				}

				rawEvents = rawEvents.OrderBy(x => x.CreatedUtc).ToList();

				RepoData createRepo(string name) =>
					new RepoData
					{
						Report = report,
						Name = name,
					};

				UserData createUser(string name) =>
					new UserData
					{
						Report = report,
						Name = name,
					};

				TeamData createTeam(string url)
				{
					var match = Regex.Match(url, @"/orgs/([^/]+)/teams/([^/]+)$");
					return new TeamData
					{
						Report = report,
						Org = match.Groups[1].Value,
						Name = match.Groups[2].Value,
					};
				}

				foreach (var rawEvent in rawEvents)
				{
					var repoName = rawEvent.RepoName!;
					var actor = createUser(rawEvent.ActorName!);

					RepoData getOrAddRepo()
					{
						var repo = report.Repos.SingleOrDefault(x => x.Name == repoName);
						if (repo == null)
						{
							repo = createRepo(repoName);
							report.Repos.Add(repo);
						}

						return repo;
					}

					BranchData getOrAddBranch(string name)
					{
						if (name == null)
							throw new InvalidOperationException();

						var repo = getOrAddRepo();
						var branch = repo.Branches.SingleOrDefault(x => x.Name == name);
						if (branch == null)
						{
							branch = new BranchData
							{
								Name = name,
								Repo = repo,
							};
							repo.Branches.Add(branch);
						}

						return branch;
					}

					PullRequestData getOrAddPullRequest(int number)
					{
						var repo = getOrAddRepo();
						var pullRequest = repo.PullRequests.SingleOrDefault(x => x.Number == number);
						if (pullRequest == null)
						{
							pullRequest = new PullRequestData
							{
								Repo = repo,
								Number = number,
							};
							repo.PullRequests.Add(pullRequest);
						}

						return pullRequest;
					}

					IssueData getOrAddIssue(int number)
					{
						var repo = getOrAddRepo();
						var issue = repo.Issues.SingleOrDefault(x => x.Number == number);
						if (issue == null)
						{
							issue = new IssueData
							{
								Repo = repo,
								Number = number,
							};
							repo.Issues.Add(issue);
						}

						return issue;
					}

					PullRequestEventData addPullRequestEvent(PullRequestData pullRequest, string kind)
					{
						var eventData = new PullRequestEventData
						{
							Kind = kind,
							Repo = pullRequest.Repo,
							Actor = actor,
						};
						pullRequest.Events.Add(eventData);
						return eventData;
					}

					IssueEventData addIssueEvent(IssueData issue, string kind)
					{
						var eventData = new IssueEventData
						{
							Kind = kind,
							Repo = issue.Repo,
							Actor = actor,
						};
						issue.Events.Add(eventData);
						return eventData;
					}

					var payload = rawEvent.Payload;
					var eventType = rawEvent.EventType;

					if (eventType == "PushEvent")
					{
						const string branchRefPrefix = "refs/heads/";
						var refName = payload.TryGetProperty("ref")?.GetString();
						if (refName?.StartsWith(branchRefPrefix, StringComparison.Ordinal) == true)
						{
							var branchName = refName.Substring(branchRefPrefix.Length);
							var branch = getOrAddBranch(branchName);

							var beforeSha = payload.TryGetProperty("before")?.GetString();
							var afterSha = payload.TryGetProperty("head")?.GetString();
							var commitCount = payload.TryGetProperty("size")?.GetInt32() ?? 0;
							var distinctCommitCount = payload.TryGetProperty("distinct_size")?.GetInt32() ?? 0;
							var commits = payload.TryGetProperty("commits")?.EnumerateArray().ToList() ?? new List<JsonElement>();
							var canMerge = commitCount == commits.Count;

							var push = branch.Events.LastOrDefault() as PushEventData;
							if (push == null ||
								push.Actor?.Name != actor.Name ||
								push.Branch?.Name != branchName ||
								push.AfterSha != beforeSha ||
								!push.CanMerge ||
								!canMerge)
							{
								push = new PushEventData
								{
									Kind = "push",
									Repo = branch.Repo,
									Actor = actor,
									Branch = branch,
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
								var sha = commit.GetProperty("sha").GetString();

								push.NewCommits.Add(new CommitData
								{
									Branch = branch,
									Repo = branch.Repo,
									Sha = sha,
									Subject = subject,
									Remarks = remarks,
								});
							}
						}
						else
						{
							addWarning($"Ignoring PushEvent to {refName} of {repoName}.");
						}
					}
					else if (eventType == "CreateEvent")
					{
						var refType = payload.GetProperty("ref_type").GetString();
						var refName = payload.GetProperty("ref").GetString();

						if (refType == "tag")
						{
							var repo = getOrAddRepo();
							repo.TagEvents.Add(new TagEventData
							{
								Kind = "create-tag",
								Repo = repo,
								Actor = actor,
								Tag = new TagData
								{
									Name = refName,
									Repo = repo,
								},
							});
						}
						else if (refType == "branch")
						{
							// ignore created branches
						}
						else
						{
							addWarning($"CreateEvent ref type {refType} not supported to {repoName}.");
						}
					}
					else if (eventType == "DeleteEvent")
					{
						// ignore deleted branches and tags
					}
					else if (eventType == "GollumEvent")
					{
						foreach (var page in payload.GetProperty("pages").EnumerateArray())
						{
							var action = page.GetProperty("action").GetString();
							var pageName = page.GetProperty("page_name").GetString();
							var repo = getOrAddRepo();
							var wikiEvent = repo.WikiEvents.LastOrDefault();
							if (wikiEvent == null || wikiEvent.Actor?.Name != actor.Name || wikiEvent.PageName != pageName)
							{
								wikiEvent = new WikiEventData
								{
									Kind = $"{action}-wiki-page",
									Repo = repo,
									Actor = actor,
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

						var repo = getOrAddRepo();
						var commit = repo.CommentedCommits.SingleOrDefault(x => x.Sha == sha);
						if (commit == null)
							repo.CommentedCommits.Add(commit = new CommentedCommitData { Repo = repo, Sha = sha });

						var conversation = commit.Conversations.SingleOrDefault(x => x.FilePath == filePath && x.Position == position?.ToString());
						if (conversation == null)
							commit.Conversations.Add(conversation = new ConversationData { Commit = commit, FilePath = filePath, Position = position.ToString() });

						conversation.Comments.Add(new CommentData
						{
							Conversation = conversation,
							Actor = actor,
							CommentId = commentId,
							Body = data.GetProperty("body").GetString(),
						});
					}
					else if (eventType == "PullRequestEvent" || eventType == "PullRequestReviewCommentEvent")
					{
						var action = payload.GetProperty("action").GetString();
						var pullRequestElement = payload.GetProperty("pull_request");
						var number = pullRequestElement.GetProperty("number").GetInt32();
						var pullRequest = getOrAddPullRequest(number);

						pullRequest.FromBranch = new BranchData
						{
							Name = pullRequestElement.GetProperty("head", "ref").GetString(),
							Repo = createRepo(pullRequestElement.GetProperty("head", "repo", "full_name").GetString()),
						};

						pullRequest.ToBranch = new BranchData
						{
							Name = pullRequestElement.GetProperty("base", "ref").GetString(),
							Repo = createRepo(pullRequestElement.GetProperty("base", "repo", "full_name").GetString()),
						};

						var title = pullRequestElement.GetProperty("title").GetString();
						if (title != null)
							pullRequest.Title = title;

						var body = pullRequestElement.GetProperty("body").GetString();
						if (body != null)
							pullRequest.Body = body;

						if (eventType == "PullRequestEvent" && action == "closed")
						{
							addPullRequestEvent(pullRequest, pullRequestElement.GetProperty("merged").GetBoolean() ? "merged" : "closed");
						}
						else if (eventType == "PullRequestReviewCommentEvent" && action == "created")
						{
							var commentElement = payload.GetProperty("comment");
							var commentId = commentElement.GetProperty("id").GetInt32();
							var commentBody = commentElement.GetProperty("body").GetString();

							var filePath = commentElement.GetProperty("path").GetString();
							var positionBefore = commentElement.GetProperty("original_position").GetNullOrInt32();
							var positionAfter = commentElement.GetProperty("position").GetNullOrInt32();
							var position = positionBefore != null ? $"{positionBefore}" : $":{positionAfter}";

							var conversation = pullRequest.Events.OfType<PullRequestEventData>().Select(x => x.Conversation).Where(x => x != null)
								.SingleOrDefault(x => x!.FilePath == filePath && x.Position == position);
							if (conversation == null)
							{
								conversation = new ConversationData { PullRequest = pullRequest, FilePath = filePath, Position = position };
								addPullRequestEvent(pullRequest, "review-comment-created").Conversation = conversation;
							}

							conversation.Comments.Add(new CommentData
							{
								Conversation = conversation,
								Actor = actor,
								CommentId = commentId,
								Body = commentBody,
							});
						}
						else
						{
							addPullRequestEvent(pullRequest, action);
						}
					}
					else if (eventType == "IssuesEvent")
					{
						var action = payload.GetProperty("action").GetString();
						var issueElement = payload.GetProperty("issue");
						var number = issueElement.GetProperty("number").GetInt32();
						var issue = getOrAddIssue(number);

						var title = issueElement.GetProperty("title").GetString();
						if (title != null)
							issue.Title = title;

						var body = issueElement.GetProperty("body").GetString();
						if (body != null)
							issue.Body = body;

						addIssueEvent(issue, action);
					}
					else if (eventType == "IssueCommentEvent")
					{
						var action = payload.GetProperty("action").GetString();
						var issueElement = payload.GetProperty("issue");
						var number = issueElement.GetProperty("number").GetInt32();

						if (issueElement.TryGetProperty("pull_request") != null)
						{
							var pullRequest = getOrAddPullRequest(number);

							if (action == "created")
							{
								var commentElement = payload.GetProperty("comment");
								var commentId = commentElement.GetProperty("id").GetInt32();
								var commentBody = commentElement.GetProperty("body").GetString();

								var conversation = new ConversationData
								{
									PullRequest = pullRequest,
								};
								addPullRequestEvent(pullRequest, "comment-created").Conversation = conversation;

								conversation.Comments.Add(new CommentData
								{
									Conversation = conversation,
									Actor = actor,
									CommentId = commentId,
									Body = commentBody,
								});
							}
							else
							{
								addWarning($"{eventType} action {action} not supported to {repoName}.");
							}
						}
						else
						{
							var issue = getOrAddIssue(number);

							if (action == "created")
							{
								var commentElement = payload.GetProperty("comment");
								var commentId = commentElement.GetProperty("id").GetInt32();
								var commentBody = commentElement.GetProperty("body").GetString();

								var conversation = new ConversationData
								{
									Issue = issue,
								};
								addIssueEvent(issue, "comment-created").Conversation = conversation;

								conversation.Comments.Add(new CommentData
								{
									Conversation = conversation,
									Actor = actor,
									CommentId = commentId,
									Body = commentBody,
								});
							}
							else
							{
								addWarning($"{eventType} action {action} not supported to {repoName}.");
							}
						}
					}
					else if (eventType == "WatchEvent")
					{
						// ignore watch events
					}
					else if (eventType == "MemberEvent")
					{
						// ignore membership events
					}
					else if (eventType == "ForkEvent")
					{
						// ignore fork events
					}
					else if (eventType == "IssuesApiEvent")
					{
						var action = payload.GetProperty("event").GetString();
						if (action != "mentioned" && action != "subscribed" && action != "head_ref_deleted")
						{
							var issueElement = payload.GetProperty("issue");
							var number = issueElement.GetProperty("number").GetInt32();

							if (issueElement.TryGetProperty("pull_request") != null)
							{
								var pullRequest = getOrAddPullRequest(number);

								var title = issueElement.GetProperty("title").GetString();
								if (title != null)
									pullRequest.Title = title;

								var body = issueElement.GetProperty("body").GetString();
								if (body != null)
									pullRequest.Body = body;

								var eventData = addPullRequestEvent(pullRequest, action);

								if ((payload.TryGetProperty("review_requester", "login")?.GetString() ??
									payload.TryGetProperty("assigner", "login")?.GetString()) is string sourceUserName)
								{
									eventData.SourceUser = createUser(sourceUserName);
								}

								if ((payload.TryGetProperty("requested_reviewer", "login")?.GetString() ??
									payload.TryGetProperty("assignee", "login")?.GetString()) is string targetUserName)
								{
									eventData.TargetUser = createUser(targetUserName);
								}

								if (payload.TryGetProperty("requested_team", "html_url")?.GetString() is string targetTeamUrl)
									eventData.TargetTeam = createTeam(targetTeamUrl);

								if (payload.TryGetProperty("commit_id")?.GetString() is string commitId)
								{
									eventData.Commit = new CommitData
									{
										Repo = createRepo(repoName),
										Sha = commitId,
									};
								}

								if (payload.TryGetProperty("label", "name")?.GetString() is string labelName)
									eventData.LabelName = labelName;

								if (payload.TryGetProperty("rename", "from")?.GetString() is string renameFrom)
									eventData.RenameFrom = renameFrom;

								if (payload.TryGetProperty("rename", "to")?.GetString() is string renameTo)
									eventData.RenameTo = renameTo;
							}
							else
							{
								var issue = getOrAddIssue(number);

								var title = issueElement.GetProperty("title").GetString();
								if (title != null)
									issue.Title = title;

								var body = issueElement.GetProperty("body").GetString();
								if (body != null)
									issue.Body = body;

								addIssueEvent(issue, action);
							}
						}
					}
					else
					{
						addWarning($"Unexpected {eventType} for {repoName}.");
					}
				}

				void replaceList<T>(List<T> list, IEnumerable<T> items)
				{
					var newList = items.ToList();
					list.Clear();
					list.AddRange(newList);
				}

				replaceList(report.Repos, report.Repos
					.OrderBy(x => sourceRepoIndices.TryGetValue(x.Name!, out var i) ? i : int.MaxValue)
					.ThenBy(x => x.Name, StringComparer.InvariantCulture));

				foreach (var repo in report.Repos)
				{
					foreach (var pullRequest in repo.PullRequests)
					{
						// find redundant pull request close events
						foreach (var redundantCloseEvent in pullRequest.Events
							.OfType<PullRequestEventData>()
							.Where(x => x.Kind == "closed" || x.Kind == "merged")
							.OrderBy(x => x.Kind == "merged")
							.ThenBy(x => x.Commit != null)
							.SkipLast(1)
							.ToList())
						{
							pullRequest.Events.Remove(redundantCloseEvent);
						}

						// remove reference to pull request in merge commit
						var mergeCommit = pullRequest.Events
							.OfType<PullRequestEventData>()
							.Where(x => x.Kind == "merged" && x.Commit != null)
							.Select(x => x.Commit)
							.FirstOrDefault();
						if (mergeCommit != null)
						{
							var mergeReference = pullRequest.Events
								.OfType<PullRequestEventData>()
								.FirstOrDefault(x => x.Kind == "referenced" && x.Commit?.Sha == mergeCommit.Sha);
							if (mergeReference != null)
								pullRequest.Events.Remove(mergeReference);
						}
					}

					replaceList(repo.PullRequests, repo.PullRequests.OrderBy(x => x.Number));
					replaceList(repo.Issues, repo.Issues.OrderBy(x => x.Number));
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
			return serializer.Serialize(deserializer.Deserialize(new StringReader(yaml))!);
		}

		private enum DownloadStatus
		{
			Success,
			NotFound,
			TooMuchActivity,
		}

		private sealed class PagedDownloadResult
		{
			public PagedDownloadResult(DownloadStatus status, IReadOnlyList<JsonElement>? elements = null)
			{
				Status = status;
				Elements = elements ?? Array.Empty<JsonElement>();
			}

			public DownloadStatus Status { get; }

			public IReadOnlyList<JsonElement> Elements { get; }
		}
	}
}
