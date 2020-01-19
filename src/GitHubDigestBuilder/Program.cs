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
			var authTokens = new Queue<string>();
			while (args.ReadOption("auth") is string authToken)
				authTokens.Enqueue(authToken);
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

			// determine culture
			var culture = settings.Culture == null ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(settings.Culture);

			// determine output file
			var outputFile = Path.Combine(configFileDirectory, settings.OutputDirectory ?? ".", $"{dateIso}.html");

			// get GitHub settings
			var githubs = new List<GitHubSettings>();
			if (settings.GitHub != null)
				githubs.Add(settings.GitHub);
			if (settings.GitHubs != null)
				githubs.AddRange(settings.GitHubs);
			if (githubs.Count == 0)
				throw new ApplicationException("Configuration file must specify at least one github.");

			// create report
			var report = new ReportData
			{
				Date = date,
				PreviousDate = date.AddDays(-1),
				Now = now,
			};

			try
			{
				foreach (var github in githubs)
				{
					// prepare HTTP client
					var httpClient = new HttpClient();
					httpClient.DefaultRequestHeaders.UserAgent.Add(ProductInfoHeaderValue.Parse("GitHubDigestBuilder"));

					var authToken = github.AuthToken;
					if (authToken is null)
						authTokens.TryDequeue(out authToken);
					if (!string.IsNullOrWhiteSpace(authToken))
						httpClient.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"token {authToken}");

					string webBase;
					string apiBase;
					if (github.Enterprise is string enterprise)
					{
						webBase = enterprise;
						if (!webBase.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !webBase.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
							webBase = "https://" + webBase.TrimEnd('/');
						apiBase = webBase + "/api/v3";
					}
					else
					{
						webBase = "https://github.com";
						apiBase = "https://api.github.com";
					}

					const int cacheVersion = 2;

					using var sha1 = SHA1.Create();
					var cacheDirectory = settings.CacheDirectory != null ? Path.GetFullPath(settings.CacheDirectory) : Path.Combine(Path.GetTempPath(), "GitHubDigestBuilderCache");
					cacheDirectory = Path.Combine(cacheDirectory, BitConverter.ToString(sha1.ComputeHash(Encoding.UTF8.GetBytes($"{cacheVersion} {apiBase} {authToken}"))).Replace("-", "")[..16]);
					Directory.CreateDirectory(cacheDirectory);

					// don't process the same event twice
					var handledEventIds = new HashSet<string>();

					// stop when rate limited
					var rateLimitResetUtc = default(DateTime?);

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

						if (rateLimitResetUtc is object)
							return new PagedDownloadResult(DownloadStatus.RateLimited, rateLimitResetUtc: rateLimitResetUtc.Value);

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

							if (response.StatusCode == HttpStatusCode.Forbidden &&
								response.Headers.TryGetValues("X-RateLimit-Remaining", out var rateLimitRemainingValues) &&
								rateLimitRemainingValues.FirstOrDefault() == "0" &&
								response.Headers.TryGetValues("X-RateLimit-Reset", out var rateLimitResetValues) &&
								int.TryParse(rateLimitResetValues.FirstOrDefault() ?? "", out var resetEpochSeconds))
							{
								rateLimitResetUtc = DateTime.UnixEpoch.AddSeconds(resetEpochSeconds);

								var message = "GitHub API rate limit exceeded. " +
									$"Try again at {new DateTimeOffset(rateLimitResetUtc.Value).ToOffset(timeZoneOffset).ToString("f", culture)}.";
								if (authToken is null)
									message += " Specify a GitHub personal access token for a much higher rate limit.";
								addWarning(message);

								return new PagedDownloadResult(DownloadStatus.RateLimited, rateLimitResetUtc: rateLimitResetUtc);
							}

							if (response.StatusCode == HttpStatusCode.Unauthorized)
								throw new ApplicationException("GitHub API returned 401 Unauthorized. Ensure that your auth token is set to a valid personal access token.");

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

					void addWarning(string text)
					{
						if (!report.Warnings.Contains(text))
							report.Warnings.Add(text);
					}

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

					async Task addReposForSource(string sourceKind, string sourceName, int sourceIndex, string? topic)
					{
						var orgRepoNames = new HashSet<string>();
						var result = await loadPagesAsync($"{sourceKind}/{sourceName}/repos?sort=updated&per_page=100",
							accepts: new[] { "application/vnd.github.mercy-preview+json" }, maxPageCount: 100);

						foreach (var repoElement in result.Elements)
						{
							if (!repoElement.GetProperty("private").GetBoolean() &&
								!repoElement.GetProperty("archived").GetBoolean() &&
								!repoElement.GetProperty("disabled").GetBoolean() &&
								(topic == null || repoElement.GetProperty("topics").EnumerateArray().Select(x => x.GetString()).Any(x => x == topic)))
							{
								orgRepoNames.Add(repoElement.GetProperty("full_name").GetString());
							}
						}

						foreach (var orgRepoName in orgRepoNames)
							addRepoForSource(orgRepoName, sourceIndex);

						if (result.Status == DownloadStatus.TooMuchActivity)
							addWarning($"Too many updated repositories found for {sourceName}.");
						else if (result.Status == DownloadStatus.NotFound)
							addWarning($"Failed to find repositories for {sourceName}.");
					}

					var settingsRepos = github.Repos ?? new List<RepoSettings>();
					var settingsUsers = github.Users ?? new List<UserSettings>();
					if (settingsRepos.Count == 0 && settingsUsers.Count == 0)
						throw new ApplicationException("No repositories or users specified in configuration.");

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
					foreach (var exclude in github.Excludes ?? new List<FilterSettings>())
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

					var pullRequestBranches = new Dictionary<(string SourceRepo, string SourceBranch), List<(string TargetRepo, int TargetNumber, DateTime When, string? Action)>>();

					async Task<(IReadOnlyList<RawEventData> Events, DownloadStatus Status)> loadEventsAsync(string sourceKind, string sourceName)
					{
						var isNetwork = sourceKind == "networks";

						var events = new List<RawEventData>();
						var result = await loadPagesAsync($"{sourceKind}/{sourceName}/events",
							accepts: new[] { "application/vnd.github.v3+json" }, maxPageCount: 10,
							isLastPage: page => page.EnumerateArray().Any(x => ParseDateTime(x.GetProperty("created_at").GetString()) < startDateTimeUtc));

						foreach (var eventElement in result.Elements)
						{
							var createdUtc = ParseDateTime(eventElement.GetProperty("created_at").GetString());
							if (createdUtc >= startDateTimeUtc)
							{
								var eventId = eventElement.GetProperty("id").GetString();
								var eventType = eventElement.GetProperty("type").GetString();
								var actorName = eventElement.GetProperty("actor", "login").GetString();
								var repoName = eventElement.GetProperty("repo", "name").GetString();
								var payload = eventElement.GetProperty("payload");

								if (!handledEventIds.Add(eventId))
									continue;

								if (usersToExclude.Contains(actorName))
									continue;

								if (!isNetwork)
								{
									var pullRequestElement = payload.TryGetProperty("pull_request");
									if (pullRequestElement != null)
									{
										var number = pullRequestElement.Value.TryGetProperty("number")?.GetInt32();
										var targetRepo = pullRequestElement.Value.TryGetProperty("base", "repo", "full_name")?.GetString();
										var sourceRepo = pullRequestElement.Value.TryGetProperty("head", "repo", "full_name")?.GetString();
										var sourceBranch = pullRequestElement.Value.TryGetProperty("head", "ref")?.GetString();
										var isOpen = pullRequestElement.Value.TryGetProperty("state")?.GetString() == "open";
										var action = eventType == "PullRequestEvent" ? payload.TryGetProperty("action")?.GetString() : null;
										if (number != null && targetRepo == repoName && sourceRepo != null && sourceBranch != null && (isOpen || action == "closed"))
										{
											if (!pullRequestBranches.TryGetValue((sourceRepo, sourceBranch), out var pullRequestInfo))
												pullRequestBranches[(sourceRepo, sourceBranch)] = pullRequestInfo = new List<(string, int, DateTime, string?)>();
											pullRequestInfo.Add((repoName, number.Value, createdUtc, action));
										}
									}
								}

								if (createdUtc >= endDateTimeUtc)
									continue;

								if (isNetwork && eventType != "CreateEvent" && eventType != "PushEvent")
									continue;

								events.Add(new RawEventData
								{
									EventId = eventId,
									EventType = eventType,
									ActorName = actorName,
									CreatedUtc = createdUtc,
									RepoName = repoName,
									IsNetwork = isNetwork,
									Payload = payload,
								});
							}
						}

						events.Reverse();
						return (events, result.Status);
					}

					async Task<(IReadOnlyList<RawEventData> Events, DownloadStatus Status)> loadIssueEventsAsync(string repoName)
					{
						var events = new List<RawEventData>();
						var result = await loadPagesAsync($"repos/{repoName}/issues/events?per_page=100",
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
									RepoName = repoName,
									Payload = eventElement,
								});
							}
						}

						events.Reverse();
						return (events, result.Status);
					}

					var rawEvents = new List<RawEventData>();

					foreach (var sourceRepoName in sourceRepoNames)
					{
						var (rawRepoEvents, repoStatus) = await loadEventsAsync("repos", sourceRepoName);
						rawEvents.AddRange(rawRepoEvents);
						if (repoStatus == DownloadStatus.TooMuchActivity)
							addWarning($"{sourceRepoName} repository had too much activity.");
						else if (repoStatus == DownloadStatus.NotFound)
							addWarning($"{sourceRepoName} repository not found.");

						// check repository network for push events if we know of any pull request branches
						if (repoStatus != DownloadStatus.NotFound && pullRequestBranches.Any(prb => prb.Value.Any(x => x.TargetRepo == sourceRepoName)))
						{
							var (rawNetworkEvents, networkStatus) = await loadEventsAsync("networks", sourceRepoName);
							rawEvents.AddRange(rawNetworkEvents);
							if (networkStatus == DownloadStatus.TooMuchActivity)
								addWarning($"{sourceRepoName} repository had too much network activity.");
							else if (networkStatus == DownloadStatus.NotFound)
								addWarning($"{sourceRepoName} repository network not found.");
						}

						var (rawRepoIssueEvents, repoIssueStatus) = await loadIssueEventsAsync(sourceRepoName);
						rawEvents.AddRange(rawRepoIssueEvents);
						if (repoIssueStatus == DownloadStatus.TooMuchActivity)
							addWarning($"{sourceRepoName} repository had too much issue activity.");
						else if (repoIssueStatus == DownloadStatus.NotFound)
							addWarning($"{sourceRepoName} repository not found.");
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
							WebBase = webBase,
							Name = name,
						};

					UserData createUser(string name) =>
						new UserData
						{
							WebBase = webBase,
							Name = name,
						};

					TeamData createTeam(string url)
					{
						var match = Regex.Match(url, @"/orgs/([^/]+)/teams/([^/]+)$");
						return new TeamData
						{
							WebBase = webBase,
							Org = match.Groups[1].Value,
							Name = match.Groups[2].Value,
						};
					}

					var repos = new List<RepoData>();

					foreach (var rawEvent in rawEvents)
					{
						var repoName = rawEvent.RepoName!;
						var actor = createUser(rawEvent.ActorName!);

						RepoData getOrAddRepo(string? actualRepoName = null)
						{
							actualRepoName ??= repoName;

							var repo = repos.SingleOrDefault(x => x.Name == actualRepoName);
							if (repo == null)
							{
								repo = createRepo(actualRepoName);
								repos.Add(repo);
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

						PullRequestData getOrAddPullRequest(int number, string? actualRepoName = null)
						{
							var repo = getOrAddRepo(actualRepoName);
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

						void setIssueBaseProperties(IssueBaseData data, JsonElement element)
						{
							var title = element.GetProperty("title").GetString();
							if (title != null)
								data.Title = title;

							var body = element.GetProperty("body").GetString();
							if (body != null)
								data.Body = body;
						}

						void setIssueBaseEventProperties(IssueBaseEventData eventData, JsonElement payloadElement)
						{
							if ((payloadElement.TryGetProperty("review_requester", "login")?.GetString() ??
								payloadElement.TryGetProperty("assigner", "login")?.GetString()) is string sourceUserName)
							{
								eventData.SourceUser = createUser(sourceUserName);
							}

							if ((payloadElement.TryGetProperty("requested_reviewer", "login")?.GetString() ??
								payloadElement.TryGetProperty("assignee", "login")?.GetString()) is string targetUserName)
							{
								eventData.TargetUser = createUser(targetUserName);
							}

							if (payloadElement.TryGetProperty("requested_team", "html_url")?.GetString() is string targetTeamUrl)
								eventData.TargetTeam = createTeam(targetTeamUrl);

							if (payloadElement.TryGetProperty("label", "name")?.GetString() is string labelName)
								eventData.LabelName = labelName;

							if (payloadElement.TryGetProperty("rename", "from")?.GetString() is string renameFrom)
								eventData.RenameFrom = renameFrom;

							if (payloadElement.TryGetProperty("rename", "to")?.GetString() is string renameTo)
								eventData.RenameTo = renameTo;
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

						List<(string RepoName, int Number)> getAssociatedPullRequests(string branchName)
						{
							var pullRequests = new HashSet<(string RepoName, int Number)>();
							if (pullRequestBranches.TryGetValue((repoName, branchName), out var pullRequestEvents))
							{
								foreach (var pullRequestEvent in pullRequestEvents
									.OrderByDescending(x => x.When)
									.TakeWhile(x => x.When >= rawEvent.CreatedUtc || x.Action != "closed"))
								{
									pullRequests.Add((pullRequestEvent.TargetRepo, pullRequestEvent.TargetNumber));
								}
							}

							return pullRequests.ToList();
						}

						var payload = rawEvent.Payload;
						var eventType = rawEvent.EventType;

						if (eventType == "PushEvent")
						{
							const string branchRefPrefix = "refs/heads/";
							var refName = payload.TryGetProperty("ref")?.GetString();
							var commitCount = payload.TryGetProperty("size")?.GetInt32() ?? 0;
							if (refName == null || !refName.StartsWith(branchRefPrefix, StringComparison.Ordinal))
							{
								addWarning($"Ignoring PushEvent to {refName} of {repoName}.");
							}
							else if (commitCount > 0)
							{
								var branchName = refName.Substring(branchRefPrefix.Length);

								var associatedPullRequests = getAssociatedPullRequests(branchName);
								if (associatedPullRequests.Count != 0)
								{
									foreach (var associatedPullRequest in associatedPullRequests)
										addPushEvent(null, getOrAddPullRequest(associatedPullRequest.Number, associatedPullRequest.RepoName));
								}
								else if (!rawEvent.IsNetwork)
								{
									addPushEvent(getOrAddBranch(branchName), null);
								}

								void addPushEvent(BranchData? branch, PullRequestData? pullRequest)
								{
									var events = branch?.Events ?? pullRequest?.Events ?? throw new InvalidOperationException();

									var beforeSha = payload.TryGetProperty("before")?.GetString();
									var afterSha = payload.TryGetProperty("head")?.GetString();
									var distinctCommitCount = payload.TryGetProperty("distinct_size")?.GetInt32() ?? 0;
									var commits = payload.TryGetProperty("commits")?.EnumerateArray().ToList() ?? new List<JsonElement>();
									var canMerge = commitCount == commits.Count;

									var pushRepo = branch?.Repo ?? createRepo(repoName);

									var push = events.LastOrDefault() as PushEventData;
									if (push == null ||
										commitCount > 5 ||
										push.Actor?.Name != actor.Name ||
										push.Branch?.Name != branch?.Name ||
										push.PullRequest?.Number != pullRequest?.Number ||
										push.AfterSha != beforeSha ||
										!push.CanMerge ||
										!canMerge)
									{
										push = new PushEventData
										{
											Kind = "push",
											Repo = pushRepo,
											Actor = actor,
											Branch = branch,
											PullRequest = pullRequest,
											BeforeSha = beforeSha,
											CanMerge = canMerge,
										};
										events.Add(push);
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
											Repo = pushRepo,
											Sha = sha,
											Subject = subject,
											Remarks = remarks,
										});
									}
								}
							}
						}
						else if (eventType == "CreateEvent")
						{
							var refType = payload.GetProperty("ref_type").GetString();
							var refName = payload.GetProperty("ref").GetString();

							if (refType == "tag")
							{
								if (!rawEvent.IsNetwork)
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
							}
							else if (refType == "branch")
							{
								var associatedPullRequests = getAssociatedPullRequests(refName);
								if (associatedPullRequests.Count != 0)
								{
									foreach (var associatedPullRequest in associatedPullRequests)
									{
										var pullRequest = getOrAddPullRequest(associatedPullRequest.Number, associatedPullRequest.RepoName);
										pullRequest.Events.Add(new BranchEventData
										{
											Kind = "create-branch",
											Repo = pullRequest.Repo,
											Actor = actor,
											Branch = new BranchData
											{
												Name = refName,
												Repo = createRepo(repoName),
											},
										});
									}
								}
								else if (!rawEvent.IsNetwork)
								{
									var branch = getOrAddBranch(refName);

									branch.Events.Add(new BranchEventData
									{
										Kind = "create-branch",
										Repo = branch.Repo,
										Actor = actor,
										Branch = branch,
									});
								}
							}
							else if (refType == "repository")
							{
								if (!rawEvent.IsNetwork)
								{
									var repo = getOrAddRepo();
									repo.RepoEvents.Add(new RepoEventData
									{
										Kind = "create-repo",
										Repo = repo,
										Actor = actor,
									});
								}
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
								repo.CommentedCommits.Add(commit = new CommentedCommitData
								{
									Repo = repo,
									Sha = sha
								});

							var conversation = commit.Conversations.SingleOrDefault(x => x.FilePath == filePath && x.Position == position?.ToString());
							if (conversation == null)
								commit.Conversations.Add(conversation = new ConversationData
								{
									Commit = commit,
									FilePath = filePath,
									Position = position.ToString()
								});

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

							setIssueBaseProperties(pullRequest, pullRequestElement);

							if (eventType == "PullRequestEvent" && action == "closed")
							{
								var eventData = addPullRequestEvent(pullRequest, pullRequestElement.GetProperty("merged").GetBoolean() ? "merged" : "closed");

								if (pullRequestElement.TryGetProperty("merge_commit_sha")?.GetString() is string commitId)
								{
									eventData.Commit = new CommitData
									{
										Repo = createRepo(repoName),
										Sha = commitId,
									};
								}
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
									conversation = new ConversationData
									{
										PullRequest = pullRequest,
										FilePath = filePath,
										Position = position
									};
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
							setIssueBaseProperties(issue, issueElement);

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
							if (action != "mentioned" && action != "subscribed" && action != "head_ref_deleted" && action != "referenced" && action != "deployed")
							{
								var issueElement = payload.GetProperty("issue");
								var number = issueElement.GetProperty("number").GetInt32();

								if (issueElement.TryGetProperty("pull_request") != null)
								{
									var pullRequest = getOrAddPullRequest(number);
									setIssueBaseProperties(pullRequest, issueElement);

									var eventData = addPullRequestEvent(pullRequest, action);
									setIssueBaseEventProperties(eventData, payload);

									if (payload.TryGetProperty("commit_id")?.GetString() is string commitId)
									{
										eventData.Commit = new CommitData
										{
											Repo = createRepo(repoName),
											Sha = commitId,
										};
									}
								}
								else
								{
									var issue = getOrAddIssue(number);
									setIssueBaseProperties(issue, issueElement);

									var eventData = addIssueEvent(issue, action);
									setIssueBaseEventProperties(eventData, payload);
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

					foreach (var repo in repos
						.OrderBy(x => sourceRepoIndices.TryGetValue(x.Name!, out var i) ? i : int.MaxValue)
						.ThenBy(x => x.Name, StringComparer.InvariantCulture))
					{
						foreach (var pullRequest in repo.PullRequests)
						{
							// remove redundant pull request close events
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
						}

						foreach (var issue in repo.Issues)
						{
							// remove redundant issue close events
							foreach (var redundantCloseEvent in issue.Events
								.OfType<IssueEventData>()
								.Where(x => x.Kind == "closed")
								.SkipLast(1)
								.ToList())
							{
								issue.Events.Remove(redundantCloseEvent);
							}
						}

						replaceList(repo.PullRequests, repo.PullRequests.OrderBy(x => x.Number));
						replaceList(repo.Issues, repo.Issues.OrderBy(x => x.Number));

						report.Repos.Add(repo);
					}
				}

				var templateText = GetEmbeddedResourceText("GitHubDigestBuilder.template.scriban-html");
				var template = Template.Parse(templateText);

				var templateContext = new TemplateContext { StrictVariables = true };
				templateContext.PushCulture(culture);

				var scriptObject = new ScriptObject();
				scriptObject.Import(report);
				scriptObject.Import(typeof(ReportFunctions));
				templateContext.PushGlobal(scriptObject);

				var reportHtml = template.Render(templateContext);

				Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
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
			RateLimited,
		}

		private sealed class PagedDownloadResult
		{
			public PagedDownloadResult(DownloadStatus status, IReadOnlyList<JsonElement>? elements = null, DateTime? rateLimitResetUtc = null)
			{
				Status = status;
				Elements = elements ?? Array.Empty<JsonElement>();
				RateLimitResetUtc = rateLimitResetUtc;
			}

			public DownloadStatus Status { get; }

			public IReadOnlyList<JsonElement> Elements { get; }

			public DateTime? RateLimitResetUtc { get; }
		}
	}
}
