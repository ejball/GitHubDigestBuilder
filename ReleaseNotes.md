# Release Notes

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
