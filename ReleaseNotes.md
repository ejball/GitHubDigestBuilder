# Release Notes

## 0.7.8

* Fix cache directory for multiple githubs.

## 0.7.7

* Support [secondary rate limits](https://docs.github.com/en/rest/overview/resources-in-the-rest-api#secondary-rate-limits).
* Show headers and body of unexpected status codes.

## 0.7.3

* Hide some uninteresting actions.

## 0.7.2

* Don't report linking issues or PRs.

## 0.7.1

* Don't report creating a PR review.

## 0.7.0

* Try to avoid error when `pull_request.body` is null.
* Basic support for `PullRequestReviewEvent`.
* Support `convert_to_draft`.
* Don't report PR/issue connections.

## 0.6.0

* Support sending digest via email.
* Drop directory settings from configuration (use command-line arguments instead).

## 0.5.2

* Avoid crash on unexpected data.

## 0.5.1

* Support `milestoned` issue event.

## 0.5.0

* Support wildcards for repo/user filters.

## 0.4.0

* Support `repo` under `excludes`.

## 0.3.0

* Support `authTokenEnv` to get GitHub PAT from an environment variable.

## 0.2.1

* Ignore head_ref_restored.
* Support marked_as_duplicate.
* Remove redundant reopened events.
* Don't report commit of closed pull requests.

## 0.2.0

* Use .NET Core 3.1 to fix emojis.
* Ignore event for deleted issue/PR comments.
* Support reopened and renamed events for issues.
* Use full width of viewport in digest.
* Support --date today and --date yesterday.

## 0.1.0

* Initial release.
