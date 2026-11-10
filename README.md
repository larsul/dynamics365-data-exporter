# DataverseToSql

[![Build](https://github.com/larsul/dynamics365-data-exporter/actions/workflows/build.yml/badge.svg)](https://github.com/larsul/dynamics365-data-exporter/actions/workflows/build.yml) [![Release](https://img.shields.io/github/v/release/larsul/dynamics365-data-exporter?sort=semver)](https://github.com/larsul/dynamics365-data-exporter/releases/latest)

Windows desktop app (WinForms, .NET 8) that copies a Dataverse table (default `annotation`, scales to millions of rows) into SQL Server or Azure SQL Database. A wizard walks through source, destination, mapping and start. The app creates the database, schema and table when they are missing, or maps the attributes onto an existing table. Runs are Full or Incremental (modifiedon watermark), with optional delete detection and parallel reads. Every page is checkpointed, so a crashed or cancelled run continues where it stopped. A headless mode lets you schedule runs.

## Prerequisites

- Windows 10 or 11, 64-bit. The released installer and portable zip carry .NET 8 inside the executable, so nothing has to be installed. You only need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build from source.
- SQL Server (any edition, including LocalDB), Azure SQL Managed Instance or Azure SQL Database. The account needs DDL and DML rights on the target database (for example `db_owner`, or `db_ddladmin` + `db_datareader` + `db_datawriter`). It needs `CREATE DATABASE` rights only if the database does not exist yet. On Azure SQL Database, create the database in the portal first so you choose its size.
- Dataverse access in one of two ways:
  - **ClientSecret (server-to-server, STD-INT-001)**: an Entra ID app registration with a client secret, plus a Dataverse **application user** for that app. Give the application user a security role with at least organization-level **Read** on the synced table. Use this mode for scheduled runs.
  - **Interactive**: sign in as a user with Read on the table. If you leave ClientId empty, the app uses the Microsoft public sample app id. The token cache is kept in `%LOCALAPPDATA%\DataverseToSql\tokencache` (STD-INT-002).

## Install

Two ways to get the app, both on the [Releases page](https://github.com/larsul/dynamics365-data-exporter/releases/latest). Windows 10 or 11, 64-bit; nothing else to install, because .NET 8 is built into the executable.

| | File | Use it when |
|---|---|---|
| **Installer** | `DataverseToSql-<version>-Setup.exe` | You want Start menu and desktop shortcuts, a clean uninstall through Settings, and upgrades that land in the same folder. |
| **Portable** | `DataverseToSql-<version>-portable-win-x64.zip` | You do not want to install anything: unpack it and run the executable. Good for a USB stick, a locked-down PC or a build server. |

**Installer.** Run the setup program and follow the wizard. It installs per machine when you have admin rights and under your own profile otherwise. Uninstall through *Settings → Apps → Dataverse to SQL Sync*.

**Portable.** Unpack the zip into a folder **you can write to**, for example `C:\Tools\DataverseToSql`, and double-click `DataverseToSql.exe`. Keep it out of `C:\Program Files`: the Updates button replaces the executable in place, which needs write access to that folder.

Both copies read and write the same per-user data, so you can switch between them:

| What | Where |
|---|---|
| Saved configurations | `%LOCALAPPDATA%\DataverseToSql` |
| Logs | `%LOCALAPPDATA%\DataverseToSql\logs` |
| Token cache | `%LOCALAPPDATA%\DataverseToSql\tokencache` |

Windows SmartScreen may say the file is not commonly downloaded, because the builds are not code-signed. Choose **More info → Run anyway**, or compare the file's SHA-256 with `SHA256SUMS.txt` on the release first:

```powershell
Get-FileHash .\DataverseToSql-1.0.0-Setup.exe -Algorithm SHA256
```

## Updating

The app checks for a newer release when it starts and whenever you ask it to. The check only reads the public releases page; nothing about your environment is sent.

1. Click **Updates** at the bottom of the window. When a newer version was already found, the button reads **Update available** and is highlighted.
2. The dialog shows your version, the newest version and its release notes.
3. **Download and install** fetches the right file for how this copy was deployed, applies it and starts the app again:
   - an installed copy runs the new setup program silently and upgrades in place;
   - a portable copy replaces `DataverseToSql.exe` after the app closes (the previous file is kept as `.old` until the swap succeeds).

Every version's changes are listed in [CHANGELOG.md](CHANGELOG.md), which is also what the release notes on GitHub and the notes in the Updates dialog show.

Stop a running sync before updating; the dialog refuses while one is in flight. If an update fails, the dialog shows why and **Open the releases page** lets you download it by hand. The portable helper writes `%TEMP%\DataverseToSql-update.log`.

The repository the app checks is baked in at build time (`UpdateRepositoryOwner` / `UpdateRepositoryName` in `DataverseToSql.csproj`). Change those two properties if you fork the project, so your copies look for your releases.

## Publishing a release

Versions are stamped from the git tag, so the tag is the single source of truth.

Add the version's section to [CHANGELOG.md](CHANGELOG.md) first, then tag it:

```powershell
git tag v1.1.0
git push origin v1.1.0
```

That starts the `Release` workflow, which publishes the single file, packs the portable zip, compiles the installer with Inno Setup, writes `SHA256SUMS.txt`, takes the release notes from the matching `## [1.1.0]` section of `CHANGELOG.md` and creates the GitHub release with all three files attached. You can also start it by hand from the **Actions** tab (*Release → Run workflow*) and type a version there; a tag or version containing `-` (for example `v1.1.0-beta1`) is published as a pre-release, which the in-app updater ignores unless it is the newest release.

To build the same artifacts locally:

```powershell
.\scripts\build-release.ps1 -Version 1.1.0
```

It writes `dist\` and needs [Inno Setup 6](https://jrsoftware.org/isdl.php) (`winget install JRSoftware.InnoSetup`) for the installer; without it you get the portable zip only.

## Build

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (`dotnet --list-sdks` must show an 8.x or newer SDK). Open a terminal (PowerShell or Command Prompt) in the repository folder.

**Build for yourself (needs the .NET 8 Desktop Runtime to run):**

```powershell
cd src\DataverseToSql
dotnet build -c Release
```

The app is then at `src\DataverseToSql\bin\Release\net8.0-windows\DataverseToSql.exe`.

**Build one file to hand to someone else (no .NET install needed on their PC):**

```powershell
cd src\DataverseToSql
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ..\..\publish
```

This creates `publish\DataverseToSql.exe` (about 180 MB). Copy that single file to the user's PC, for example to `C:\Tools\DataverseToSql\`. Nothing else is needed. The `.pdb` file next to it is only for debugging and can be left out.

If the build says the file is in use (`MSB3027` / "being used by another process"), close the running app first.

## Run

Double-click `DataverseToSql.exe`, or run it from a terminal:

```powershell
.\DataverseToSql.exe
```

The wizard opens. Work through the steps with **Next**:

1. **Configuration**: create a new configuration or open an existing one. A configuration file stores one sync job (source, destination, mapping and options). New files go to `%APPDATA%\DataverseToSql\configs\`. Use **Save** / **Save as...** at the bottom at any time.
2. **Source (Dataverse)**: enter the environment URL (for example `https://yourorg.crm4.dynamics.com`), the sign-in method and the table (`annotation`). Choose the read method: Dataverse SDK (recommended) or TDS endpoint. **Test & Next** signs in and checks the table.
3. **Destination (SQL)**: choose the sign-in method (for Azure SQL with MFA: "Microsoft Entra interactive"), then enter the server, database, schema and table. **Test & Next** signs in and reads the table if it exists.
4. **Column mapping**: tick the attributes to copy and pick the destination column for each one. Matching names are mapped automatically.
5. **Options and start**: choose Full or Incremental and press **Start**. If an earlier run was interrupted, choose to continue it (recommended) or start again. Starting again never deletes data in the destination.

During the run you see rows copied, speed and progress. **Stop** keeps the progress, so you can continue later. **Show details** opens the technical log.

Files the app uses:

| What | Where |
|---|---|
| Configurations | `%APPDATA%\DataverseToSql\configs\*.json` (older default: `%APPDATA%\DataverseToSql\settings.json`) |
| Recent-files list | `%APPDATA%\DataverseToSql\recent.json` |
| Logs | `%LOCALAPPDATA%\DataverseToSql\logs\sync-yyyyMMdd.log` |
| Dataverse sign-in cache | `%LOCALAPPDATA%\DataverseToSql\tokencache` |

The client secret and SQL passwords are never written to any of these files (STD-INT-006).

## Headless / scheduled

```
set DATAVERSE_CLIENT_SECRET=<secret>
DataverseToSql.exe --run C:\Sync\annotation.settings.json
```

- If you omit the path, the app uses `%APPDATA%\DataverseToSql\settings.json`.
- Exit code `0` means success and `1` means failure (details are in the log). Ctrl+C cancels the run.
- The secret is read only from `DATAVERSE_CLIENT_SECRET`. Never put it in the settings file or on the command line.

Task Scheduler example (hourly incremental). Set the secret as an environment variable for the service account, or have a wrapper script fetch it from a vault:

```
schtasks /Create /TN "DataverseToSql annotation" /SC HOURLY /RU "DOMAIN\svc-dvsync" /RP * ^
  /TR "\"C:\Tools\DataverseToSql\DataverseToSql.exe\" --run \"C:\Sync\annotation.settings.json\""
```

For scheduled runs, use a settings file with `"AuthType": "ClientSecret"`, `"Mode": "Incremental"` and a SQL connection string that uses Integrated Security. Interactive auth cannot prompt when no one is signed in.

## Sources: Dataverse SDK or TDS endpoint

Pick the read method in the wizard (setting `Source`):

- **Dataverse SDK** (`Sdk`, default): `ServiceClient` RetrieveMultiple with paging cookies. Works in every environment and supports `documentbody`.
- **TDS endpoint** (`Tds`): read-only SQL endpoint on `<org>.crm*.dynamics.com,5558`, signed in with the same Dataverse token as the SDK path. Often faster for wide scans. It must be enabled in the Power Platform admin center (Environment > Settings > Product > Features > TDS endpoint), and outbound TCP 5558 must be open. Some columns are not exposed (for example `documentbody`/file columns); the run stops at the start with the list of missing attributes, so deselect them or switch to the SDK. Pages use keyset paging on the primary key (`TOP n ... WHERE id > @last ORDER BY id`); each query is retried 3 times on endpoint errors.

Both sources share the same slicing, checkpointing, mapping and SQL writing. Use "Connect & test" in the wizard to check the TDS endpoint before a long run.

## Destination sign-in (SQL Server / Azure SQL)

Setting `SqlAuth`:

| Value | Use for | Notes |
|---|---|---|
| `ConnectionString` | SQL Server, Windows auth, SQL logins | The connection string is used as typed. A password is never written to the settings file. |
| `EntraInteractive` | Azure SQL Database with Microsoft Entra ID + MFA | Browser sign-in once per app start. Optional `SqlUser` (UPN) pre-fills the account. Not usable in headless runs. |
| `EntraDefault` | Scheduled runs against Azure SQL | Managed identity, Azure CLI (`az login`) or Visual Studio sign-in. |

Example server for Azure SQL: `Server=tcp:myserver.database.windows.net,1433;`. Encryption is forced on for both Entra modes. The app opens the target database first and touches `master` only if the database cannot be opened, so contained database users work.

## Crash recovery (checkpoints)

A run of a very large table can take hours, so progress is stored in the destination database:

- `[schema].[__DataverseSyncRun]`: one row per run (window, mode, status `Running` / `Completed` / `Abandoned`, rows written, attempts, last error).
- `[schema].[__DataverseSyncSlice]`: one row per time slice (status `Pending` / `Split` / `Done`, next page number, paging cookie or last key).

A page is checkpointed only after its upsert commits. After a crash, power loss, network failure or Cancel, the next start finds the `Running` run and continues each unfinished slice at the saved page; at most one page per worker is read again, and the upsert makes that harmless.

- **Automatic retry**: inside one start, a failed attempt is retried up to `MaxAttempts` times (default 5, waits 30 s, 60 s, 120 s ... up to 10 min). Each retry continues from the checkpoint. Configuration and safety errors (wrong mapping, delete-safety abort, sign-in not possible) are not retried.
- **Continue or start again**: on the last wizard step you choose "Continue the interrupted run" (default, `ResumeInterruptedRun=true`) or "Start again from the beginning". Starting again only marks the old run `Abandoned`. **Nothing is deleted or truncated**: existing rows stay and are updated or added again.
- **One run at a time**: a SQL session lock (`sp_getapplock`) per destination table stops two app instances from syncing the same table. If the process dies, SQL Server releases the lock.
- **Scheduled runs**: `--run` resumes automatically as well. Configure the scheduled task to restart on failure (Task Scheduler > Settings > "If the task fails, restart every 5 minutes").
- A run whose entity, environment or column set no longer matches the configuration is abandoned and a new run starts.

## SQL table as source

Step 2 can read from a SQL Server / Azure SQL table instead of Dataverse (read method "SQL Server table", or `Source: "SqlTable"` in the settings file), for example to copy a table this app filled into another table with different column types.

- Settings: `SourceSqlConnectionString` (server level), `SourceSqlAuth` (`ConnectionString`, `EntraInteractive`, `EntraDefault`), `SourceSqlUser`, `SourceDatabaseName`, and `EntityName` as `schema.table` (e.g. `dbo.annotation`).
- The source table needs a `uniqueidentifier` primary key (or a unique index on one). With a `modifiedon` datetime column, slicing, checkpoints, resume, incremental runs and delete detection work as for Dataverse; without one, every run is a full copy (see below).
- Columns are offered in the mapping step like Dataverse attributes. `__syncedon`, computed and `rowversion` columns are skipped.
- `documentbody` is converted to match the destination column: `varbinary(max)` stores the file bytes, a text column (`nvarchar(max)`, `varchar(max)`) stores base64 text. Other binary columns are written as base64 text.
- A destination table remembers its source; it cannot be switched between a Dataverse source and a SQL source.

### Converting an existing documentbody column to nvarchar(max)

`scripts/convert-documentbody-to-nvarchar-max.sql` converts the `documentbody` column of an existing table in place: `varbinary` is encoded to base64 text (every value is verified to decode back to the same bytes before the old column is dropped), `nvarchar(n)` is widened to `nvarchar(max)`. Set `@schema` and `@table` at the top, and take a backup first.

## Mapping onto an existing table

"Load destination table" reads the columns of the destination table. Each source column is auto-mapped to a destination column with the same name (case and underscores ignored); change any mapping in the grid. Mappings are stored in `ColumnMappings` (default column name -> destination column).

- Columns that do not exist are added (`ALTER TABLE ADD`, nullable). Existing columns are never dropped.
- If an existing column has a different type, SQL Server converts the values; a value that cannot convert stops the run with the SQL error.
- Two source columns mapped to the same destination column are rejected.
- Destination columns that are `NOT NULL` without a default and not mapped make every insert fail; the wizard warns about them.
- If the key column has no index, the app creates a nonclustered index on it so the upsert does not scan the table.

## How it works

- **Correlation id (STD-INT-007)**: each run gets a new GUID. The app logs it, sends it as `ServiceClient.SessionTrackingId` and stores it in the sync state table.
- **Window**: `upper = now` (UTC, whole second). Full mode starts at the minimum `modifiedon` in the source. Incremental mode starts at `watermark - 5 minutes`, which covers clock skew and in-flight transactions. Rows are upserted, so the overlap does no harm.
- **Adaptive time slicing**: the window `[lower, upper)` is split into one slice per worker. Each worker has its own cloned `ServiceClient` and its own SQL connection. If a slice's first page shows more records, the slice is at least 2 s wide and the queue is shorter than the worker count, the worker splits the slice at its midpoint and queues both halves. Otherwise it pages the slice to exhaustion (STD-DVB-003). Each page is upserted into the target in sub-batches (see SQL write tuning). Dense periods, such as bulk imports, end up split finely, and sparse periods stay coarse.
- **Paging**: queries use NoLock, filter only on `modifiedon` and sort only by the primary key. This keeps paging cookies efficient.
- **Watermark**: written only after every slice succeeds (and the delete step, when enabled). Until then the run stays resumable from its checkpoint (see Crash recovery).
- **Delete detection** (optional): runs only after all upserts succeed. The app streams every primary id from Dataverse into a `#ids` temp table, then deletes target rows that are not in that table. This step reads the full id list, so its cost grows with table size, not with the size of the change set.

## Performance notes

- **documentbody**: this attribute holds base64 file content and is by far the most expensive column. It inflates payloads, request time and SQL size (stored as `varbinary(max)`). It is excluded by default. When you select it, the page size is capped at 500.
- **Page size**: 5000 is the Dataverse maximum and the best choice for narrow columns. Lower it for wide rows.
- **Parallelism**: the setting you choose is capped by the server's `x-ms-dop-hint` (`RecommendedDegreesOfParallelism`, STD-DVB-020). Asking for more than the hint does not make the run faster.
- **Service protection limits (STD-INT-003)**: 429/throttling responses are retried by the ServiceClient's built-in retry (`MaxRetryCount=10`, `RetryPauseTime=5s`), which honours Retry-After. If the log shows sustained throttling, lower the parallelism. The affinity cookie is disabled so requests spread across web servers. The minimum thread pool size is raised so parallel requests do not queue.
- **SQL write tuning** (settings file only, no UI): each page is split into sub-batches of `SqlWriteBatchSize` rows (default 1000, below SQL Server's ~5000-lock escalation threshold; 0 = whole page). Each sub-batch is bulk-copied into `#stage` (clustered unique on the key, duplicates ignored) and written with `UPDATE` then `INSERT ... WHERE NOT EXISTS` in one short READ COMMITTED transaction, in key order, without range locks.
  - `SqlWriteParallelism` (default 0 = same as the parallelism in use): how many workers write to SQL at the same time. Reading stays parallel. `1` rules out deadlocks between workers.
  - `DeadlockMaxRetries` (default 8) and `DeadlockRetryBaseDelayMs` (default 250): a sub-batch that hits a deadlock (1205) or a concurrent-insert key violation (2627/2601) is retried with exponential back-off (capped at 30 s). When the retries run out, it is run once more while no other worker writes, instead of failing the run.
  - `UpsertMode` (`UpdateInsert` default, or `Merge`): `Merge` restores the earlier single `MERGE WITH (HOLDLOCK)`, which is more prone to deadlocks with several workers.
- **SQL**: the clustered PK is on the Dataverse primary id. For the initial load of very large tables, use the SIMPLE or BULK_LOGGED recovery model.

## SQL schema created

- Database `<DatabaseName>`, created if it is missing.
- Schema `<SchemaName>`, created if it is missing.
- Table `[schema].[table]`:
  - The primary id column (`uniqueidentifier NOT NULL`, `PK_<table>` clustered).
  - One column per selected attribute, named with the attribute's logical name and nullable.
  - Lookups become a `uniqueidentifier` column. Polymorphic lookups (Owner, Customer, multi-target) also get a `<name>_entity nvarchar(128)` column.
  - `[__syncedon] datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME()`.
- Type mapping:

  | Dataverse type | SQL type |
  |---|---|
  | String | `nvarchar(n)`, n up to 4000 |
  | Memo | `nvarchar(max)` |
  | `documentbody` | `varbinary(max)` |
  | Integer | `int` |
  | BigInt | `bigint` |
  | Decimal | `decimal(38,10)` |
  | Money | `decimal(38,4)` |
  | Double | `float` |
  | Boolean | `bit` |
  | DateTime | `datetime2(3)` (UTC) |
  | Picklist / State / Status | `int` |
  | MultiSelectPicklist | `nvarchar(4000)` (comma-separated values) |
  | EntityName | `nvarchar(128)` |

- When you select new attributes later, the matching columns are added with `ALTER TABLE ADD`. Columns are never dropped.
- `[schema].[__DataverseSyncState]` holds one row per target table: `TableName`, `WatermarkUtc`, `LastRunUtc`, `LastRows`, `LastCorrelationId`.

## Limitations

- Only one table per settings file. To sync several tables, run once per settings file.
- Changing a column's type in Dataverse is not propagated to SQL. Removed attributes stay as stale columns.
- Option set labels are not synced, only their integer values. Lookup names (formatted values) are not synced either.
- File, Image, PartyList, ManagedProperty, CalendarRules and virtual attributes (except multi-select picklists) are skipped.
- Incremental mode depends on `modifiedon`. Changes that do not update `modifiedon` are not picked up.
- Tables without `modifiedon` (for example `activitymimeattachment`) are supported: every run copies the whole table in parallel. With Parallelism > 1 the run first reads all primary ids once, then splits the table into ranges of about equal row count (4 per worker, max 256). Each range is filtered `id >= lo AND id < hi` on the server, paged on the primary key, and checkpointed. Range boundaries are real ids, so the server's uniqueidentifier sort order always applies. Incremental mode runs as Full. Turn on delete detection to remove rows deleted in the source.
- Without delete detection, deleted source rows stay in SQL. Delete detection always scans every id.
- `documentbody` is decoded and held in memory one page at a time, so very large attachments need plenty of RAM.
