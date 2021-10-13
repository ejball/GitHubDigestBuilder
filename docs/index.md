# GitHubDigestBuilder

[![NuGet](https://img.shields.io/nuget/v/GitHubDigestBuilder.svg)](https://www.nuget.org/packages/GitHubDigestBuilder)

This tool creates a single-page HTML digest of the previous day's GitHub activity for the configured repositories and users. It supports both public GitHub and GitHub Enterprise.

## Overview

The GitHub API provides multiple mechanisms for tracking activity, most notably notifications and events. This tool uses events rather than notifications.

**Notifications** are triggered by pull request and issue activity for each repository that is being *watched* by the user. Notifications can be emailed to the user, accessed at [github.com/notifications](https://github.com/notifications), or searched and filtered at [octobox.io](https://octobox.io/).

**Events** are similar to notifications, but they track additional types of activity, including pushed commits and comments not associated with a pull request. Events can be downloaded for any repository or user. There isn't a good user interface for events at [github.com](https://github.com/), so this tool makes events more accessible by creating a single-page HTML digest of the previous day's events.

The goal of the digest is to increase your awareness of GitHub activity. It does not give the full details of each event; just click the link into GitHub for that.

## Installation

GitHubDigestBuilder is a .NET Core Tool, so the simplest way to install it is globally via:

```shell
> dotnet tool install -g GitHubDigestBuilder
```

## Usage

Once installed, invoke GitHubDigestBuilder with the path to your configuration file as the first argument.

```shell
> GitHubDigestBuilder digest.yaml
```

The following command-line options are also supported:

### `--auth`

You can use this tool without a GitHub [personal access token](https://github.com/settings/tokens) (PAT), but the GitHub event API is extremely rate-limited without one. You can provide the PAT via the `--auth` command-line option or via the `authToken` or `authTokenEnv` configuration fields described later. The *public_repo* permission should be sufficient.

```shell
> GitHubDigestBuilder digest.yaml --auth 1234567890123456789012345678901234567890
```

If multiple `githubs` are provided in the configuration, add an `--auth` option for each one.

### `--output`

Use this option to specify an output directory relative to the current directory. The actual output file is named after the digest date, e.g. `2020-02-02.html`.

```shell
> GitHubDigestBuilder digest.yaml --verbose
```

### `--verbose`

If you want to see what GitHub APIs are being used, specify this option.

```shell
> GitHubDigestBuilder digest.yaml --verbose
```

### `--quiet`

The tool normally writes the path of the output file to the console; use this option to suppress that.

```shell
> GitHubDigestBuilder digest.yaml --quiet
```

### `--date`

Without this command-line option, the digest for yesterday is created. You can generate digests for older dates, but GitHub only provides access to the most recent events, so the tool is most reliable when generating a digest for yesterday, ideally early in the morning before much activity happens today.

```shell
> GitHubDigestBuilder digest.yaml --date 2020-02-02
```

### `--email-from`, `--email-to`, `--email-subject`, `--email-smtp`, `--email-user`, `--email-pwd`

When these command-line options are specified, the tool uses SMTP to send the digest as an email message. The email subject is optional.

### `--cache`

Specifies the cache directory. Without this command-line option, the cache is stored in a subdirectory of the standard temporary directory called `GitHubDigestBuilderCache`.

## Configuration

The configuration file is primarily used to indicate which GitHub repositories you'd like to monitor. It can also be used to monitor individual users, exclude events from individual users, etc. For example:

```yaml
github:
  repos:
    - user: ejball
    - org: FacilityApi
  users:
    - name: ejball
  excludes:
    - user: dependabot-preview[bot]
outputDirectory: ejball
```

The configuration file can use YAML or JSON.

### Root Fields

These fields are supported at the root of the configuration file.

#### github / githubs

Specifies the GitHub configuration. Use `githubs` to specify an array of configurations, e.g. to generate a single report for both GitHub and GitHub Enterprise.

#### outputDirectory

Specifies an output directory relative to the directory containing the configuration file. Only used if `--output` is not specified on the command-line.

#### timeZoneOffsetHours

The tool uses local time by default, but this field can be used to override that with the number of hours time is offset from UTC.

#### culture

The tool uses local culture by default, but this field can be used to override that with the specified culture, e.g. `en-US`, `es-MX`, etc.

#### cacheDirectory

By default, GitHub API cache files are stored in the user's temp directory under a `GitHubDigestBuilderCache` subdirectory. This field can be used to override that.

### GitHub Fields

These fields are supported by the `github` or `githubs` objects.

#### enterprise

Public GitHub is used by default, but this field can be set to the host of a GitHub Enterprise instance.

#### authToken

The GitHub [personal access token](https://github.com/settings/tokens) to use with this GitHub instance. If the configuration file is not in a secure location, the `authTokenEnv` field or the `--auth` command-line option may be preferrable.

#### authTokenEnv

The name of the environment variable with the GitHub [personal access token](https://github.com/settings/tokens) to use with this GitHub instance.

#### repos

Objects that specify the repositories to watch for events, if any. (See below for fields.)

#### users

Objects that specify the users to watch for events, if any. (See below for fields.)

#### excludes

Objects that specify users that should be excluded, e.g. "bot" users. (See below for fields.)

### Repo Fields

These fields are supported by the `repos` objects.

#### name

The full name of the repository to watch, including the organization/user and the repository name.

```yaml
  repos:
    - name: FacilityApi/FacilityCSharp
    - name: ejball/GitHubDigestBuilder
```

#### org

Watches all repositories of the specified organization.

```yaml
  repos:
    - org: FacilityApi
```

#### user

Watches all repositories of the specified user.

```yaml
  repos:
    - user: ejball
```

#### topic

Only includes repositories with the specified topic. Used with `org` or `user`.

```yaml
  repos:
    - org: FacilityApi
      topic: codegen
```

### User Fields

These fields are supported by the `users` objects.

#### name

The name of the user to watch. That user's events will still be organized by repository.

```yaml
  users:
    - name: ejball
```

### Exclude Fields

These fields are supported by the `excludes` objects.

Names in exclude fields are case-insensitive. Use an asterisk (`*`) to match zero or more characters; all matching names will be excluded.

#### user

The name of the user to exclude. Events triggered by that user will not be shown.

```yaml
  excludes:
    - user: dependabot-preview[bot]
```

#### repo

The name of the repository to exclude. Events triggered by that repository will not be shown.

```yaml
  excludes:
    - repo: FacilityApi/RepoTemplate
```
