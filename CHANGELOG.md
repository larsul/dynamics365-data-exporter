# Changelog

All notable changes to this project are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the versions follow
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). The release workflow reads the section for
the tag it builds and uses it as the GitHub release notes, so each version's section is what users see
on the [Releases page](https://github.com/larsul/dynamics365-data-exporter/releases) and in the app's
**Updates** dialog.

## [Unreleased]

Nothing yet.

## [1.0.0] - 2026-11-10

First published release. Everything below is new to anyone who has not used an internal build.

### Added

**Distribution**

- **Installer** (`DataverseToSql-1.0.0-Setup.exe`), built with Inno Setup: Start menu and optional
  desktop shortcuts, per-machine install with admin rights or per-user without, and a clean uninstall
  through *Settings → Apps*. A stable application id means later versions upgrade in place instead of
  installing a second copy.
- **Portable zip** (`DataverseToSql-1.0.0-portable-win-x64.zip`) for people who do not want to install
  anything: unpack it into a writable folder and run the executable. Includes the SQL helper scripts,
  `settings.sample.json` and a short readme.
- Both builds are self-contained single files, so **.NET 8 does not have to be installed**.
- `SHA256SUMS.txt` on every release, for checking a download before running it.

**In-app updates**

- **Updates** button in the bottom bar. The app also checks once, quietly, at startup; when a newer
  release exists the button becomes a highlighted **Update available**. A failed check never
  interrupts anything and only goes to the technical log.
- The dialog shows the running version, the newest version, its release notes and the download size,
  then applies the update and restarts the app. It picks the file that matches how the copy was
  deployed: an installed copy runs the new setup program silently and upgrades in place; a portable
  copy is replaced after the app closes by a helper that keeps a backup until the swap succeeds and
  restores it if anything fails.
- Updating is refused while a sync is running, and the dialog offers the releases page whenever a
  download or an install cannot be completed.
- The version is shown next to the product name in the header and written to the log at startup.

**Sync**

- Copies a Dataverse table into SQL Server, Azure SQL Managed Instance or Azure SQL Database, sized
  for tables in the millions of rows.
- Five-step wizard: configuration file, Dataverse source, SQL destination, column mapping, options
  and start. Each step tests itself before letting you continue.
- **Full** and **Incremental** runs (`modifiedon` watermark), with optional delete detection and
  parallel page reads.
- Creates the database, schema and table when they are missing, or maps the selected attributes onto
  an existing table. Matching column names are mapped automatically.
- Two read methods: the Dataverse SDK (recommended) or the TDS endpoint. A SQL table can also be used
  as the source.
- **Checkpoints after every page**, so a cancelled or crashed run continues where it stopped instead
  of starting over. Starting again never deletes data in the destination.
- Dataverse sign-in as an interactive user or as an application with a client secret; SQL sign-in with
  Windows auth, a SQL login, or Microsoft Entra (including interactive MFA).
- **Headless mode** (`--run [settings.json]`) for Task Scheduler, with `0`/`1` exit codes and Ctrl+C
  cancellation.
- Live run view with rows copied, throughput and progress, plus a technical log that can be opened
  from the bottom bar.
- Configuration files per sync job, a recent-files list, and a daily log file under
  `%LOCALAPPDATA%\DataverseToSql\logs`.

### Security

- Client secrets and SQL passwords are never written to a configuration file. The Dataverse client
  secret is read only from the `DATAVERSE_CLIENT_SECRET` environment variable, never from settings or
  the command line.
- The update check reads only the public releases page; nothing about your environment is sent.
- `settings.json` is no longer tracked by git, because a real configuration holds an environment URL,
  a server name and a sign-in name. `settings.sample.json` is the committed template.

### Known issues

- The builds are not code-signed, so Windows SmartScreen may warn that the file is not commonly
  downloaded. Choose **More info → Run anyway**, or check the SHA-256 against `SHA256SUMS.txt` first.
- A portable copy under `C:\Program Files` cannot update itself, because it has no write access to its
  own folder. Keep it somewhere writable, such as `C:\Tools\DataverseToSql`, or use the installer.
- Windows x64 only.

[Unreleased]: https://github.com/larsul/dynamics365-data-exporter/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/larsul/dynamics365-data-exporter/releases/tag/v1.0.0
