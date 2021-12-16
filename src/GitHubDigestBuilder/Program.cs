using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeGenCore;
using GitHubDigestBuilder.Models;
using GitHubDigestBuilder.Settings;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using MimeKit.Text;
using YamlDotNet.Serialization;

namespace GitHubDigestBuilder;

public static class Program
{
	public static async Task RunAsync(ArgsReader args)
	{
		// read command-line arguments
		var dateString = args.ReadOption("date");
		var authTokens = new Queue<string>();
		while (args.ReadOption("auth") is { } authToken)
			authTokens.Enqueue(authToken);
		var isQuiet = args.ReadFlag("quiet");
		var isVerbose = args.ReadFlag("verbose") && !isQuiet;
		var outputDirectory = args.ReadOption("output");
		var rootCacheDirectory = args.ReadOption("cache");
		var emailFrom = args.ReadOption("email-from");
		var emailTo = args.ReadOption("email-to");
		var emailSubject = args.ReadOption("email-subject");
		var emailSmtp = args.ReadOption("email-smtp");
		var emailUsername = args.ReadOption("email-user");
		var emailPassword = args.ReadOption("email-pwd");
		var newline = args.ReadOption("newline") switch
		{
			"lf" => "\n",
			"crlf" => "\r\n",
			null => null,
			_ => throw new ArgsReaderException("Invalid --newline (use lf or crlf)."),
		};
		var configFilePath = args.ReadArgument();
		args.VerifyComplete();

		// find config file
		if (configFilePath is null)
			throw new ApplicationException(GetUsage());
		configFilePath = Path.GetFullPath(configFilePath);
		if (!File.Exists(configFilePath))
			throw new ApplicationException("Configuration file not found.");

		// deserialize config file
		var settings = JsonSerializer.Deserialize<DigestSettings>(
			ConvertYamlToJson(await File.ReadAllTextAsync(configFilePath)),
			new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new ApplicationException("Invalid configuration.");

		// determine date/time range in UTC
		var timeZoneOffset = settings.TimeZoneOffsetHours is not null ? TimeSpan.FromHours(settings.TimeZoneOffsetHours.Value) : DateTimeOffset.Now.Offset;
		var now = new DateTimeOffset(DateTime.UtcNow).ToOffset(timeZoneOffset);
		var todayIso = now.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		var date = ParseDateArgument(dateString, now.Date);
		var dateIso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		var startDateTimeUtc = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, timeZoneOffset).UtcDateTime;
		var endDateTimeUtc = startDateTimeUtc.AddDays(1.0);

		// determine culture
		var culture = settings.Culture is null ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(settings.Culture);

		// create code generation settings
		var codeGenSettings = new CodeGenSettings
		{
			Culture = culture,
			NewLine = newline,
			SingleFileName = "report.html",
			UseSnakeCase = true,
		};

		// determine output file or email
		string? outputFile = null;
		if (emailTo is not null)
		{
			if (outputDirectory is not null)
				throw new ApplicationException("Cannot use both --email-to and --output.");
			if (emailFrom is null)
				throw new ApplicationException("Missing required --email-from.");
			if (emailSmtp is null)
				throw new ApplicationException("Missing required --email-smtp.");
		}
		else
		{
			// determine output file
			outputDirectory = Path.GetFullPath(outputDirectory ?? ".");
			outputFile = Path.Combine(outputDirectory, $"{dateIso}.html");
		}

		// get GitHub settings
		var githubs = new List<GitHubSettings>();
		if (settings.GitHub is not null)
			githubs.Add(settings.GitHub);
		if (settings.GitHubs is not null)
			githubs.AddRange(settings.GitHubs);
		if (githubs.Count == 0)
			throw new ApplicationException("Configuration file must specify at least one github.");

		// create report
		var report = new ReportData
		{
			Date = date,
			PreviousDate = date.AddDays(-1),
			Now = now,
			IsEmail = emailTo is not null,
			Culture = culture,
		};

		try
		{
			foreach (var github in githubs)
			{
				// prepare HTTP client
				var httpClient = new HttpClient();
				httpClient.DefaultRequestHeaders.UserAgent.Add(ProductInfoHeaderValue.Parse("GitHubDigestBuilder"));

				var authToken = (github.AuthTokenEnv is null ? null : Environment.GetEnvironmentVariable(github.AuthTokenEnv)) ?? github.AuthToken;
				if (authToken is null)
					authTokens.TryDequeue(out authToken);
				if (!string.IsNullOrWhiteSpace(authToken))
					httpClient.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse($"token {authToken}");

				string webBase;
				string apiBase;
				if (github.Enterprise is { } enterprise)
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
				var cacheDirectory = rootCacheDirectory is not null ? Path.GetFullPath(rootCacheDirectory) : Path.Combine(Path.GetTempPath(), "GitHubDigestBuilderCache");
				cacheDirectory = Path.Combine(cacheDirectory, BitConverter.ToString(sha1.ComputeHash(Encoding.UTF8.GetBytes($"{cacheVersion} {apiBase} {authToken}"))).Replace("-", "")[..16]);
				Directory.CreateDirectory(cacheDirectory);

				// don't process the same event twice
				var handledEventIds = new HashSet<string>();

				// stop when rate limited
				var rateLimitResetUtc = default(DateTime?);

				async Task<PagedDownloadResult> LoadPagesAsync(string url, string[] accepts, int maxPageCount, Func<JsonElement, bool>? isLastPage = null)
				{
					var cacheName = Regex.Replace(Regex.Replace(url, @"\?.*$", ""), @"/+", "_").Trim('_');
					var cacheFile = Path.Combine(cacheDirectory, $"{cacheName}.json");
					var cacheElement = default(JsonElement);
					string? etag = null;
					string etagMessage;

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
								Enum.Parse<DownloadStatus>(cacheElement.GetProperty("status").GetString() ?? throw new InvalidOperationException("Missing status.")),
								cacheElement.GetProperty("items").EnumerateArray().ToList());
						}

						// don't use the cache if we may have stopped early and we're asking for an older report
						if (isLastPage is null || string.CompareOrdinal(dateIso, cacheDateIso) >= 0)
						{
							etag = cacheElement.GetProperty("etag").GetString();
							etagMessage = etag is null ? "no etag" : "with etag";
						}
						else
						{
							etagMessage = "old report";
						}
					}
					else
					{
						etagMessage = "no cache";
					}

					if (rateLimitResetUtc is not null)
						return new PagedDownloadResult(DownloadStatus.RateLimited);

					var status = DownloadStatus.TooMuchActivity;
					var items = new List<JsonElement>();

					for (var pageNumber = 1; pageNumber <= maxPageCount; pageNumber++)
					{
						var pageParameter = pageNumber == 1 ? "" : $"{(url.Contains('?') ? '&' : '?')}page={pageNumber}";

						HttpResponseMessage response;
						bool shouldRetry;
						do
						{
							shouldRetry = false;

							var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/{url}{pageParameter}");
							foreach (var accept in accepts)
								request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(accept));
							if (pageNumber == 1 && etag is not null)
								request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
							response = await httpClient!.SendAsync(request);
							if (isVerbose)
								Console.WriteLine($"{request.RequestUri!.AbsoluteUri} ({etagMessage}) [{response.StatusCode}]");

							if (response.StatusCode == HttpStatusCode.Forbidden &&
								response.Headers.TryGetValues("Retry-After", out var retryAfterValues) &&
								int.TryParse(retryAfterValues.FirstOrDefault() ?? "", out var retryAfterSeconds))
							{
								if (isVerbose)
									Console.WriteLine($"Waiting {retryAfterSeconds}s for secondary rate limit.");
								await Task.Delay(TimeSpan.FromSeconds(retryAfterSeconds));
								shouldRetry = true;
							}
						}
						while (shouldRetry);

						if (pageNumber == 1 && etag is not null && response.StatusCode == HttpStatusCode.NotModified)
						{
							status = Enum.Parse<DownloadStatus>(cacheElement.GetProperty("status").GetString() ?? throw new InvalidOperationException("Missing status."));
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
							AddWarning(message);

							return new PagedDownloadResult(DownloadStatus.RateLimited);
						}

						if (response.StatusCode == HttpStatusCode.Unauthorized)
							throw new ApplicationException("GitHub API returned 401 Unauthorized. Ensure that your auth token is set to a valid personal access token.");

						if (response.StatusCode != HttpStatusCode.OK)
						{
							throw new InvalidOperationException(string.Join(Environment.NewLine,
								$"Unexpected status code: {response.StatusCode}",
								$"Headers: {string.Join("; ", response.Headers.Select(x => $"{x.Key}: {string.Join(", ", x.Value)}"))}",
								$"Body: {await response.Content.ReadAsStringAsync()}"));
						}

						if (pageNumber == 1)
							etag = (response.Headers.ETag ?? throw new InvalidOperationException("Missing ETag.")).Tag;

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

				void AddWarning(string text)
				{
					if (!report!.Warnings.Contains(text))
					{
						report.Warnings.Add(text);
						Console.Error.WriteLine(text);
					}
				}

				var sourceRepoNames = new List<string>();
				var sourceRepoIndices = new Dictionary<string, int>();

				void AddRepoForSource(string repoName, int sourceIndex)
				{
					if (!sourceRepoIndices.ContainsKey(repoName))
					{
						sourceRepoNames.Add(repoName);
						sourceRepoIndices.Add(repoName, sourceIndex);
					}
				}

				async Task AddReposForSource(string sourceKind, string sourceName, int sourceIndex, string? topic)
				{
					var orgRepoNames = new HashSet<string>();
					var result = await LoadPagesAsync($"{sourceKind}/{sourceName}/repos?sort=updated&per_page=100",
						accepts: new[] { "application/vnd.github.mercy-preview+json" }, maxPageCount: 100);

					foreach (var repoElement in result.Elements)
					{
						if (!repoElement.GetProperty("private").GetBoolean() &&
						    !repoElement.GetProperty("archived").GetBoolean() &&
						    !repoElement.GetProperty("disabled").GetBoolean() &&
						    (topic is null || repoElement.GetProperty("topics").EnumerateArray().Select(x => x.GetString()).Any(x => x == topic)))
						{
							orgRepoNames.Add(repoElement.GetProperty("full_name").GetString() ?? throw new InvalidOperationException("Missing full_name."));
						}
					}

					foreach (var orgRepoName in orgRepoNames)
						AddRepoForSource(orgRepoName, sourceIndex);

					if (result.Status == DownloadStatus.TooMuchActivity)
						AddWarning($"Too many updated repositories found for {sourceName}.");
					else if (result.Status == DownloadStatus.NotFound)
						AddWarning($"Failed to find repositories for {sourceName}.");
				}

				var settingsRepos = github.Repos ?? new List<RepoSettings>();
				var settingsUsers = github.Users ?? new List<UserSettings>();
				if (settingsRepos.Count == 0 && settingsUsers.Count == 0)
					throw new ApplicationException("No repositories or users specified in configuration.");

				foreach (var (settingsRepo, sourceIndex) in settingsRepos.Select((x, i) => (x, i)))
				{
					switch (settingsRepo)
					{
						case { Name: { } name, User: null, Org: null, Topic: null }:
							AddRepoForSource(name, sourceIndex);
							break;

						case { Name: null, User: { } user, Org: null }:
							await AddReposForSource("users", user, sourceIndex, topic: settingsRepo.Topic);
							break;

						case { Name: null, User: null, Org: { } org }:
							await AddReposForSource("orgs", org, sourceIndex, topic: settingsRepo.Topic);
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
						case { Name: { } name }:
							if (!sourceUserNames.Contains(name))
								sourceUserNames.Add(name);
							break;

						default:
							throw new ApplicationException("Invalid user source: " + JsonSerializer.Serialize(settingsUser));
					}
				}

				var usersToExclude = new List<Regex>();
				var reposToExclude = new List<Regex>();
				foreach (var exclude in github.Excludes ?? new List<FilterSettings>())
				{
					Regex CreateRegex(string value) =>
						new("^" + Regex.Escape(value).Replace(@"\*", ".*") + "$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

					switch (exclude)
					{
						case { User: { } user, Repo: null }:
							usersToExclude.Add(CreateRegex(user));
							break;

						case { User: null, Repo: { } repo }:
							reposToExclude.Add(CreateRegex(repo));
							break;

						default:
							throw new ApplicationException("Invalid exclude: " + JsonSerializer.Serialize(exclude));
					}
				}

				var pullRequestBranches = new Dictionary<(string SourceRepo, string SourceBranch), List<(string TargetRepo, int TargetNumber, DateTime When, string? Action)>>();

				async Task<(IReadOnlyList<RawEventData> Events, DownloadStatus Status)> LoadEventsAsync(string sourceKind, string sourceName)
				{
					var isNetwork = sourceKind == "networks";

					var events = new List<RawEventData>();
					var result = await LoadPagesAsync($"{sourceKind}/{sourceName}/events",
						accepts: new[] { "application/vnd.github.v3+json" }, maxPageCount: 10,
						isLastPage: page => page.EnumerateArray().Any(x => ParseDateTime(x.GetProperty("created_at").GetString() ?? throw new InvalidOperationException("Missing created_at.")) < startDateTimeUtc));

					foreach (var eventElement in result.Elements)
					{
						var createdUtc = ParseDateTime(eventElement.GetProperty("created_at").GetString() ?? throw new InvalidOperationException("Missing created_at."));
						if (createdUtc >= startDateTimeUtc)
						{
							var eventId = eventElement.GetProperty("id").GetString() ?? throw new InvalidOperationException("Missing id.");
							var eventType = eventElement.GetProperty("type").GetString();
							var actorName = eventElement.GetProperty("actor", "login").GetString() ?? throw new InvalidOperationException("Missing actor.login.");
							var repoName = eventElement.GetProperty("repo", "name").GetString() ?? throw new InvalidOperationException("Missing repo.name.");
							var payload = eventElement.GetProperty("payload");

							if (!handledEventIds.Add(eventId))
								continue;

							if (usersToExclude.Any(x => x.IsMatch(actorName)))
								continue;

							if (!isNetwork && reposToExclude.Any(x => x.IsMatch(repoName)))
								continue;

							if (!isNetwork)
							{
								var pullRequestElement = payload.TryGetProperty("pull_request");
								if (pullRequestElement is not null)
								{
									var number = pullRequestElement.Value.TryGetProperty("number")?.GetInt32();
									var targetRepo = pullRequestElement.Value.TryGetProperty("base", "repo", "full_name")?.GetString();
									var sourceRepo = pullRequestElement.Value.TryGetProperty("head", "repo", "full_name")?.GetString();
									var sourceBranch = pullRequestElement.Value.TryGetProperty("head", "ref")?.GetString();
									var isOpen = pullRequestElement.Value.TryGetProperty("state")?.GetString() == "open";
									var action = eventType == "PullRequestEvent" ? payload.TryGetProperty("action")?.GetString() : null;
									if (number is not null && targetRepo == repoName && sourceRepo is not null && sourceBranch is not null && (isOpen || action == "closed"))
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

				async Task<(IReadOnlyList<RawEventData> Events, DownloadStatus Status)> LoadIssueEventsAsync(string repoName)
				{
					var events = new List<RawEventData>();
					var result = await LoadPagesAsync($"repos/{repoName}/issues/events?per_page=100",
						accepts: new[] { "application/vnd.github.v3+json" }, maxPageCount: 10,
						isLastPage: page => page.EnumerateArray().Any(x => ParseDateTime(x.GetProperty("created_at").GetString() ?? throw new InvalidOperationException("Missing created_at.")) < startDateTimeUtc));

					foreach (var eventElement in result.Elements)
					{
						var createdUtc = ParseDateTime(eventElement.GetProperty("created_at").GetString() ?? throw new InvalidOperationException("Missing created_at."));
						if (createdUtc >= startDateTimeUtc && createdUtc < endDateTimeUtc)
						{
							var eventId = "i" + eventElement.GetProperty("id").GetInt64();
							var actorName = eventElement.GetProperty("actor", "login").GetString() ?? throw new InvalidOperationException("Missing actor.login.");

							if (!handledEventIds.Add(eventId))
								continue;

							if (usersToExclude.Any(x => x.IsMatch(actorName)))
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
					if (reposToExclude.Any(x => x.IsMatch(sourceRepoName)))
						continue;

					var (rawRepoEvents, repoStatus) = await LoadEventsAsync("repos", sourceRepoName);
					rawEvents.AddRange(rawRepoEvents);
					if (repoStatus == DownloadStatus.TooMuchActivity)
						AddWarning($"{sourceRepoName} repository had too much activity.");
					else if (repoStatus == DownloadStatus.NotFound)
						AddWarning($"{sourceRepoName} repository not found.");

					// check repository network for push events if we know of any pull request branches
					if (repoStatus != DownloadStatus.NotFound && pullRequestBranches.Any(prb => prb.Value.Any(x => x.TargetRepo == sourceRepoName)))
					{
						var (rawNetworkEvents, networkStatus) = await LoadEventsAsync("networks", sourceRepoName);
						rawEvents.AddRange(rawNetworkEvents);
						if (networkStatus == DownloadStatus.NotFound)
							AddWarning($"{sourceRepoName} repository network not found.");
					}

					var (rawRepoIssueEvents, repoIssueStatus) = await LoadIssueEventsAsync(sourceRepoName);
					rawEvents.AddRange(rawRepoIssueEvents);
					if (repoIssueStatus == DownloadStatus.TooMuchActivity)
						AddWarning($"{sourceRepoName} repository had too much issue activity.");
					else if (repoIssueStatus == DownloadStatus.NotFound)
						AddWarning($"{sourceRepoName} repository not found.");
				}

				foreach (var sourceUserName in sourceUserNames)
				{
					if (usersToExclude.Any(x => x.IsMatch(sourceUserName)))
						continue;

					var (rawUserEvents, userStatus) = await LoadEventsAsync("users", sourceUserName);
					rawEvents.AddRange(rawUserEvents);
					if (userStatus == DownloadStatus.TooMuchActivity)
						AddWarning($"{sourceUserName} user had too much activity.");
					else if (userStatus == DownloadStatus.NotFound)
						AddWarning($"{sourceUserName} user not found.");
				}

				rawEvents = rawEvents.OrderBy(x => x.CreatedUtc).ToList();

				RepoData CreateRepo(string name) =>
					new()
					{
						WebBase = webBase,
						Name = name,
					};

				UserData CreateUser(string name) =>
					new()
					{
						WebBase = webBase,
						Name = name,
					};

				TeamData CreateTeam(string url)
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
					var actor = CreateUser(rawEvent.ActorName!);

					RepoData GetOrAddRepo(string? actualRepoName = null)
					{
						actualRepoName ??= repoName;

						var repo = repos.SingleOrDefault(x => x.Name == actualRepoName);
						if (repo is null)
						{
							repo = CreateRepo(actualRepoName);
							repos.Add(repo);
						}

						return repo;
					}

					BranchData GetOrAddBranch(string name)
					{
						if (name is null)
							throw new InvalidOperationException();

						var repo = GetOrAddRepo();
						var branch = repo.Branches.SingleOrDefault(x => x.Name == name);
						if (branch is null)
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

					PullRequestData GetOrAddPullRequest(int number, string? actualRepoName = null)
					{
						var repo = GetOrAddRepo(actualRepoName);
						var pullRequest = repo.PullRequests.SingleOrDefault(x => x.Number == number);
						if (pullRequest is null)
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

					IssueData GetOrAddIssue(int number)
					{
						var repo = GetOrAddRepo();
						var issue = repo.Issues.SingleOrDefault(x => x.Number == number);
						if (issue is null)
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

					void SetIssueBaseProperties(IssueBaseData data, JsonElement element)
					{
						var title = element.GetProperty("title").GetString();
						if (title is not null)
							data.Title = title;

						var body = element.GetProperty("body").GetString();
						if (body is not null)
							data.Body = body;
					}

					void SetIssueBaseEventProperties(IssueBaseEventData eventData, JsonElement payloadElement)
					{
						if ((payloadElement.TryGetProperty("review_requester", "login")?.GetString() ??
							    payloadElement.TryGetProperty("assigner", "login")?.GetString()) is { } sourceUserName)
						{
							eventData.SourceUser = CreateUser(sourceUserName);
						}

						if ((payloadElement.TryGetProperty("requested_reviewer", "login")?.GetString() ??
							    payloadElement.TryGetProperty("assignee", "login")?.GetString()) is { } targetUserName)
						{
							eventData.TargetUser = CreateUser(targetUserName);
						}

						if (payloadElement.TryGetProperty("requested_team", "html_url")?.GetString() is { } targetTeamUrl)
							eventData.TargetTeam = CreateTeam(targetTeamUrl);

						if (payloadElement.TryGetProperty("label", "name")?.GetString() is { } labelName)
							eventData.LabelName = labelName;

						if (payloadElement.TryGetProperty("milestone", "title")?.GetString() is { } milestoneTitle)
							eventData.MilestoneTitle = milestoneTitle;

						if (payloadElement.TryGetProperty("rename", "from")?.GetString() is { } renameFrom)
							eventData.RenameFrom = renameFrom;

						if (payloadElement.TryGetProperty("rename", "to")?.GetString() is { } renameTo)
							eventData.RenameTo = renameTo;
					}

					PullRequestEventData AddPullRequestEvent(PullRequestData pullRequest, string kind)
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

					IssueEventData AddIssueEvent(IssueData issue, string kind)
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

					List<(string RepoName, int Number)> GetAssociatedPullRequests(string branchName)
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
						if (refName is null || !refName.StartsWith(branchRefPrefix, StringComparison.Ordinal))
						{
							AddWarning($"Ignoring PushEvent to {refName} of {repoName}.");
						}
						else if (commitCount > 0)
						{
							var branchName = refName.Substring(branchRefPrefix.Length);

							var associatedPullRequests = GetAssociatedPullRequests(branchName);
							if (associatedPullRequests.Count != 0)
							{
								foreach (var associatedPullRequest in associatedPullRequests)
									AddPushEvent(null, GetOrAddPullRequest(associatedPullRequest.Number, associatedPullRequest.RepoName));
							}
							else if (!rawEvent.IsNetwork)
							{
								AddPushEvent(GetOrAddBranch(branchName), null);
							}

							void AddPushEvent(BranchData? branch, PullRequestData? pullRequest)
							{
								var events = branch?.Events ?? pullRequest?.Events ?? throw new InvalidOperationException();

								var beforeSha = payload.TryGetProperty("before")?.GetString();
								var afterSha = payload.TryGetProperty("head")?.GetString();
								var distinctCommitCount = payload.TryGetProperty("distinct_size")?.GetInt32() ?? 0;
								var commits = payload.TryGetProperty("commits")?.EnumerateArray().ToList() ?? new List<JsonElement>();
								var canMerge = commitCount == commits.Count;

								var pushRepo = branch?.Repo ?? CreateRepo(repoName);

								var push = events.LastOrDefault() as PushEventData;
								if (push is null ||
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
								var repo = GetOrAddRepo();
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
							var associatedPullRequests = GetAssociatedPullRequests(refName ?? throw new InvalidOperationException("Missing ref."));
							if (associatedPullRequests.Count != 0)
							{
								foreach (var associatedPullRequest in associatedPullRequests)
								{
									var pullRequest = GetOrAddPullRequest(associatedPullRequest.Number, associatedPullRequest.RepoName);
									pullRequest.Events.Add(new BranchEventData
									{
										Kind = "create-branch",
										Repo = pullRequest.Repo,
										Actor = actor,
										Branch = new BranchData
										{
											Name = refName,
											Repo = CreateRepo(repoName),
										},
									});
								}
							}
							else if (!rawEvent.IsNetwork)
							{
								var branch = GetOrAddBranch(refName);

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
								var repo = GetOrAddRepo();
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
							AddWarning($"CreateEvent ref type {refType} not supported to {repoName}.");
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
							var repo = GetOrAddRepo();
							var wikiEvent = repo.WikiEvents.LastOrDefault();
							if (wikiEvent is null || wikiEvent.Actor?.Name != actor.Name || wikiEvent.PageName != pageName)
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

						var repo = GetOrAddRepo();
						var commit = repo.CommentedCommits.SingleOrDefault(x => x.Sha == sha);
						if (commit is null)
						{
							repo.CommentedCommits.Add(commit = new CommentedCommitData
							{
								Repo = repo,
								Sha = sha,
							});
						}

						var conversation = commit.Conversations.SingleOrDefault(x => x.FilePath == filePath && x.Position == position?.ToString());
						if (conversation is null)
						{
							commit.Conversations.Add(conversation = new ConversationData
							{
								Commit = commit,
								FilePath = filePath,
								Position = position.ToString(),
							});
						}

						conversation.Comments.Add(new CommentData
						{
							Conversation = conversation,
							Actor = actor,
							CommentId = commentId,
							Body = data.GetProperty("body").GetString(),
						});
					}
					else if (eventType == "PullRequestEvent" || eventType == "PullRequestReviewEvent" || eventType == "PullRequestReviewCommentEvent")
					{
						var action = payload.GetProperty("action").GetString() ?? throw new InvalidOperationException("Missing action.");
						var pullRequestElement = payload.GetProperty("pull_request");
						var number = pullRequestElement.GetProperty("number").GetInt32();
						var pullRequest = GetOrAddPullRequest(number);

						pullRequest.FromBranch = new BranchData
						{
							Name = pullRequestElement.TryGetProperty("head", "ref")?.GetString() ?? "unknown",
							Repo = CreateRepo(pullRequestElement.TryGetProperty("head", "repo", "full_name")?.GetString() ?? "unknown"),
						};

						pullRequest.ToBranch = new BranchData
						{
							Name = pullRequestElement.TryGetProperty("base", "ref")?.GetString() ?? "unknown",
							Repo = CreateRepo(pullRequestElement.TryGetProperty("base", "repo", "full_name")?.GetString() ?? "unknown"),
						};

						SetIssueBaseProperties(pullRequest, pullRequestElement);

						if (eventType == "PullRequestEvent" && action == "closed")
						{
							var eventData = AddPullRequestEvent(pullRequest, pullRequestElement.GetProperty("merged").GetBoolean() ? "merged" : "closed");

							if (pullRequestElement.TryGetProperty("merge_commit_sha")?.GetString() is { } commitId)
							{
								eventData.Commit = new CommitData
								{
									Repo = CreateRepo(repoName),
									Sha = commitId,
								};
							}
						}
						else if (eventType == "PullRequestReviewCommentEvent")
						{
							if (action == "created")
							{
								var commentElement = payload.GetProperty("comment");
								var commentId = commentElement.GetProperty("id").GetInt32();
								var commentBody = commentElement.GetProperty("body").GetString();

								var filePath = commentElement.GetProperty("path").GetString();
								var positionBefore = commentElement.GetProperty("original_position").GetNullOrInt32();
								var positionAfter = commentElement.GetProperty("position").GetNullOrInt32();
								var position = positionBefore is not null ? $"{positionBefore}" : $":{positionAfter}";

								var conversation = pullRequest.Events.OfType<PullRequestEventData>().Select(x => x.Conversation).Where(x => x is not null)
									.SingleOrDefault(x => x!.FilePath == filePath && x.Position == position);
								if (conversation is null)
								{
									conversation = new ConversationData
									{
										PullRequest = pullRequest,
										FilePath = filePath,
										Position = position,
									};
									AddPullRequestEvent(pullRequest, "review-comment-created").Conversation = conversation;
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
								AddPullRequestEvent(pullRequest, $"review-comment-{action}");
							}
						}
						else if (eventType == "PullRequestReviewEvent")
						{
							if (action != "created")
								AddPullRequestEvent(pullRequest, $"review-{action}");
						}
						else if (action != "connected")
						{
							AddPullRequestEvent(pullRequest, action);
						}
					}
					else if (eventType == "IssuesEvent")
					{
						var action = payload.GetProperty("action").GetString();
						var issueElement = payload.GetProperty("issue");
						var number = issueElement.GetProperty("number").GetInt32();

						var issue = GetOrAddIssue(number);
						SetIssueBaseProperties(issue, issueElement);

						AddIssueEvent(issue, action ?? throw new InvalidOperationException("Missing action."));
					}
					else if (eventType == "IssueCommentEvent")
					{
						var action = payload.GetProperty("action").GetString();
						var issueElement = payload.GetProperty("issue");
						var number = issueElement.GetProperty("number").GetInt32();

						if (issueElement.TryGetProperty("pull_request") is not null)
						{
							var pullRequest = GetOrAddPullRequest(number);

							if (action == "created")
							{
								var commentElement = payload.GetProperty("comment");
								var commentId = commentElement.GetProperty("id").GetInt32();
								var commentBody = commentElement.GetProperty("body").GetString();

								var conversation = new ConversationData
								{
									PullRequest = pullRequest,
								};
								AddPullRequestEvent(pullRequest, "comment-created").Conversation = conversation;

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
								AddWarning($"{eventType} action {action} not supported to {repoName}.");
							}
						}
						else
						{
							var issue = GetOrAddIssue(number);

							if (action == "created")
							{
								var commentElement = payload.GetProperty("comment");
								var commentId = commentElement.GetProperty("id").GetInt32();
								var commentBody = commentElement.GetProperty("body").GetString();

								var conversation = new ConversationData
								{
									Issue = issue,
								};
								AddIssueEvent(issue, "comment-created").Conversation = conversation;

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
								AddWarning($"{eventType} action {action} not supported to {repoName}.");
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
					else if (eventType == "ReleaseEvent")
					{
						// ignore release events
					}
					else if (eventType == "IssuesApiEvent")
					{
						var action = payload.GetProperty("event").GetString() ?? throw new InvalidOperationException("Missing action.");
						if (action != "auto_merge_disabled" &&
						    action != "auto_merge_enabled" &&
						    action != "auto_rebase_enabled" &&
						    action != "auto_squash_enabled" &&
						    action != "comment_deleted" &&
						    action != "connected" &&
						    action != "deployed" &&
						    action != "mentioned" &&
						    action != "head_ref_deleted" &&
						    action != "head_ref_restored" &&
						    action != "referenced" &&
						    action != "subscribed")
						{
							var issueElement = payload.GetProperty("issue");
							var number = issueElement.GetProperty("number").GetInt32();

							if (issueElement.TryGetProperty("pull_request") is not null)
							{
								var pullRequest = GetOrAddPullRequest(number);
								SetIssueBaseProperties(pullRequest, issueElement);

								var eventData = AddPullRequestEvent(pullRequest, action);
								SetIssueBaseEventProperties(eventData, payload);

								if (payload.TryGetProperty("commit_id")?.GetString() is { } commitId)
								{
									eventData.Commit = new CommitData
									{
										Repo = CreateRepo(repoName),
										Sha = commitId,
									};
								}
							}
							else
							{
								var issue = GetOrAddIssue(number);
								SetIssueBaseProperties(issue, issueElement);

								var eventData = AddIssueEvent(issue, action);
								SetIssueBaseEventProperties(eventData, payload);
							}
						}
					}
					else
					{
						AddWarning($"Unexpected {eventType} for {repoName}.");
					}
				}

				void ReplaceList<T>(List<T> list, IEnumerable<T> items)
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
						foreach (var redundantEvent in pullRequest.Events
							         .OfType<PullRequestEventData>()
							         .Where(x => x.Kind == "closed" || x.Kind == "merged")
							         .OrderBy(x => x.Kind == "merged")
							         .ThenBy(x => x.Commit is not null)
							         .SkipLast(1)
							         .ToList())
						{
							pullRequest.Events.Remove(redundantEvent);
						}
					}

					foreach (var pullRequest in repo.PullRequests)
					{
						// remove redundant issue reopened events
						foreach (var redundantEvent in pullRequest.Events
							         .OfType<PullRequestEventData>()
							         .Where(x => x.Kind == "reopened")
							         .SkipLast(1)
							         .ToList())
						{
							pullRequest.Events.Remove(redundantEvent);
						}
					}

					foreach (var issue in repo.Issues)
					{
						// remove redundant issue close events
						foreach (var redundantEvent in issue.Events
							         .OfType<IssueEventData>()
							         .Where(x => x.Kind == "closed")
							         .SkipLast(1)
							         .ToList())
						{
							issue.Events.Remove(redundantEvent);
						}
					}

					foreach (var issue in repo.Issues)
					{
						// remove redundant issue reopened events
						foreach (var redundantEvent in issue.Events
							         .OfType<IssueEventData>()
							         .Where(x => x.Kind == "reopened")
							         .SkipLast(1)
							         .ToList())
						{
							issue.Events.Remove(redundantEvent);
						}
					}

					ReplaceList(repo.PullRequests, repo.PullRequests.OrderBy(x => x.Number));
					ReplaceList(repo.Issues, repo.Issues.OrderBy(x => x.Number));

					report.Repos.Add(repo);
				}
			}

			var codeGenTemplateText = GetEmbeddedResourceText("GitHubDigestBuilder.template.scriban-html");
			var codeGenTemplate = CodeGenTemplate.Parse(codeGenTemplateText);
			var codeGenGlobals = CodeGenGlobals.Create(report);
			var reportHtml = codeGenTemplate.Generate(codeGenGlobals, codeGenSettings)[0].Text;

			if (outputFile is not null)
			{
				if (!isQuiet)
					Console.WriteLine(outputFile);

				Directory.CreateDirectory(outputDirectory!);
				await File.WriteAllTextAsync(outputFile, reportHtml);
			}
			else if (emailTo is not null)
			{
				if (!isQuiet)
					Console.WriteLine($"Sending email to: {emailTo}");

				var message = new MimeMessage();
				message.From.Add(MailboxAddress.Parse(emailFrom));
				foreach (var to in emailTo.Split(';').Select(x => x.Trim()).Where(x => x.Length != 0).Select(MailboxAddress.Parse))
					message.To.Add(to);
				message.Subject = emailSubject ?? ((FormattableString) $"GitHub Digest for {date:D}").ToString(culture);
				message.Body = new TextPart(TextFormat.Html) { Text = reportHtml };

				using var client = new SmtpClient();
				await client.ConnectAsync(host: emailSmtp, port: 587, options: SecureSocketOptions.StartTls);
				if (emailUsername is not null || emailPassword is not null)
				{
					await client.AuthenticateAsync(
						userName: emailUsername ?? throw new ApplicationException("Missing --email-user."),
						password: emailPassword ?? throw new ApplicationException("Missing --email-pwd."));
				}
				await client.SendAsync(message: message);
				await client.DisconnectAsync(quit: true);
			}
			else
			{
				throw new InvalidOperationException();
			}
		}
		catch (Exception exception) when (outputFile is not null)
		{
			var codeGenTemplateText = GetEmbeddedResourceText("GitHubDigestBuilder.exception.scriban-html");
			var codeGenTemplate = CodeGenTemplate.Parse(codeGenTemplateText);
			var codeGenGlobals = CodeGenGlobals.Create(new ExceptionData { Message = exception.ToString() });
			var reportHtml = codeGenTemplate.Generate(codeGenGlobals, codeGenSettings)[0].Text;

			await File.WriteAllTextAsync(outputFile, reportHtml);

			throw;
		}
	}

	private static string GetEmbeddedResourceText(string name)
	{
		using var reader = new StreamReader(typeof(Program).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException());
		return reader.ReadToEnd();
	}

	private static DateTime ParseDateArgument(string? value, DateTime today)
	{
		if (value is null || value.Equals("yesterday", StringComparison.InvariantCultureIgnoreCase))
			return today.AddDays(-1.0);
		else if (value.Equals("today", StringComparison.InvariantCultureIgnoreCase))
			return today;
		else
			return DateTime.ParseExact(value, "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
	}

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

	private static string GetUsage() =>
		string.Join(Environment.NewLine,
			"Usage: GitHubDigestBuilder config-file [options]",
			"Options:",
			"  --auth <github-personal-access-token>  (one for each GitHub)",
			"  --date <yyyy-MM-dd|today|yesterday>  (default: yesterday)",
			"  --quiet  (no console output)",
			"  --verbose  (show GitHub API usage)",
			"  --output <output-directory>  (default: .)",
			"  --newline <lf|crlf> (default platform-specific)",
			"  --cache <cache-directory>  (default: %TEMP%\\GitHubDigestBuilderCache)",
			"  --email-from <address>  (send email from)",
			"  --email-to <address>  (send email to)",
			"  --email-subject <text>  (email subject)",
			"  --email-smtp <host>  (SMTP server)",
			"  --email-user <text>  (SMTP username)",
			"  --email-pwd <text>  (SMTP password)",
			"Documentation: https://ejball.com/GitHubDigestBuilder/");

	private enum DownloadStatus
	{
		Success,
		NotFound,
		TooMuchActivity,
		RateLimited,
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
