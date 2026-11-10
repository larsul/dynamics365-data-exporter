using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace DataverseToSql.Sync;

/// <summary>Progress snapshot of a running sync.</summary>
/// <param name="EstimatedTotalRows">Rows the run is expected to write in total (full copies only; approximate), or null when unknown.</param>
/// <param name="Workers">Number of parallel workers of this run (0 before the copy starts).</param>
public sealed record SyncProgress(long RowsWritten, int SlicesDone, int SlicesQueued, int ActiveWorkers, double RowsPerSecond, string? Stage,
    long? EstimatedTotalRows = null, int Workers = 0);

/// <summary>Outcome of a completed sync run.</summary>
public sealed record SyncResult(long RowsWritten, long RowsDeleted, TimeSpan Elapsed, Guid CorrelationId);

/// <summary>Orchestrates a Dataverse-to-SQL copy using adaptive, parallel modifiedon time slicing.</summary>
public sealed class SyncEngine
{
    private const string ModifiedOn = "modifiedon";
    private const string DocumentBody = "documentbody";
    private const int DocumentBodyMaxPageSize = 500;
    private const string MimeAttachment = "activitymimeattachment";
    private const string MimeAttachmentBody = "body";
    private static readonly TimeSpan IncrementalOverlap = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinSplitWidth = TimeSpan.FromSeconds(2);
    // Start of the single slice used for tables without modifiedon (e.g. activitymimeattachment); only a checkpoint key.
    private static readonly DateTime NoTimestampLowerUtc = new(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    // Key ranges for tables without modifiedon: a few per worker so uneven rows (e.g. large attachments) still balance.
    private const int KeyRangesPerWorker = 4;
    private const int MaxKeyRanges = 256;
    private const int MaxKeySample = 8192;
    private const long ProgressIntervalMs = 500;

    /// <summary>True for headless/scheduled runs: Interactive auth never shows a login prompt and fails fast instead.</summary>
    public bool NonInteractive { get; init; }

    /// <summary>
    /// Runs one sync according to <paramref name="s"/>, retrying failed attempts up to <see cref="AppSettings.MaxAttempts"/> times.
    /// Every attempt, and every later start after a crash, continues from the checkpoint in SQL. The secret is used only in memory (STD-INT-006).
    /// </summary>
    public async Task<SyncResult> RunAsync(AppSettings s, string? clientSecret, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var maxAttempts = Math.Max(1, s.MaxAttempts);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // A fresh start (resume off) applies to the first attempt only; retries always continue the run they started.
                return await RunOnceAsync(s, clientSecret, progress, allowResume: s.ResumeInterruptedRun || attempt > 1, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts && IsRetryable(ex, ct))
            {
                var delay = RetryDelay(attempt);
                AppLog.Info($"Attempt {attempt}/{maxAttempts} failed: {ex.Message} Retrying in {delay.TotalSeconds:N0} s; the run continues from its checkpoint.");
                progress?.Report(new SyncProgress(0, 0, 0, 0, 0, $"retry {attempt + 1}/{maxAttempts} in {delay.TotalSeconds:N0} s"));
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Returns the interrupted run of the configured destination table, or null (also when database or table do not exist).</summary>
    public static async Task<InterruptedRunInfo?> FindInterruptedRunAsync(AppSettings s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var target = new SqlTarget(s.BuildSqlConnectionString(), s.DatabaseName, s.SchemaName, s.TableName);
        if (await target.GetTableColumnsAsync(ct).ConfigureAwait(false) is null)
            return null;
        await using var store = new CheckpointStore(target, s.SchemaName);
        return await store.PeekInterruptedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns the latest runs of the configured destination table, newest first (empty when nothing ran yet).</summary>
    public static async Task<IReadOnlyList<RunHistoryItem>> GetRunHistoryAsync(AppSettings s, int top, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var target = new SqlTarget(s.BuildSqlConnectionString(), s.DatabaseName, s.SchemaName, s.TableName);
        if (await target.GetTableColumnsAsync(ct).ConfigureAwait(false) is null)
            return Array.Empty<RunHistoryItem>();
        await using var store = new CheckpointStore(target, s.SchemaName);
        return await store.GetRecentRunsAsync(top, ct).ConfigureAwait(false);
    }

    // Approximate row count for the progress bar; never fails the run.
    private static async Task<long?> TryEstimateRowsAsync(ISyncSource source, CancellationToken ct)
    {
        try
        {
            var n = await source.EstimateRowCountAsync(ct).ConfigureAwait(false);
            if (n is > 0)
                AppLog.Info($"Source holds about {n:N0} rows (estimate for progress).");
            return n is > 0 ? n : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Info($"Row count estimate not available: {ex.Message}");
            return null;
        }
    }

    // SQL errors caused by the data or the table design: a retry hits the same error again, so they are final.
    private static readonly HashSet<int> PermanentSqlErrors = new()
    {
        13537, 13536,      // GENERATED ALWAYS / temporal period column
        271, 272, 273,     // computed / timestamp (rowversion) column
        207, 208, 4104,    // invalid column / object
        515, 547,          // NULL into NOT NULL / constraint violation
        2627, 2601,        // duplicate key
        245, 8114, 8115, 241, 242, 295, // conversion / overflow
        8152, 2628,        // string or binary data would be truncated
        229, 230, 262, 300, 916, // permission denied
    };

    // User cancellation, configuration, safety and data/design errors are final; everything else (network, throttling,
    // timeouts, deadlocks) is retried.
    private static bool IsRetryable(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && ex is not SyncConfigurationException and not ArgumentException && !IsPermanentSqlError(ex);

    private static bool IsPermanentSqlError(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is Microsoft.Data.SqlClient.SqlException sql && sql.Errors.Cast<Microsoft.Data.SqlClient.SqlError>().Any(x => PermanentSqlErrors.Contains(x.Number)))
                return true;
        }
        return false;
    }

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromSeconds(Math.Min(600, 30 * Math.Pow(2, attempt - 1)));

    private async Task<SyncResult> RunOnceAsync(AppSettings s, string? clientSecret, IProgress<SyncProgress>? progress,
        bool allowResume, CancellationToken ct)
    {

        if (NonInteractive && s.SqlAuth == SqlAuthKind.EntraInteractive)
            throw new SyncConfigurationException(
                "Scheduled (headless) runs cannot show the Microsoft Entra sign-in window. Use 'Microsoft Entra default' " +
                "(managed identity / az login) or a SQL login for the destination.");

        // STD-INT-007: one correlation id per run, propagated as ServiceClient.SessionTrackingId and stored in sync state.
        var correlationId = Guid.NewGuid();
        var sw = Stopwatch.StartNew();
        AppLog.Info($"Sync run started. CorrelationId={correlationId} Entity={s.EntityName} Mode={s.Mode} Target=[{s.DatabaseName}].[{s.SchemaName}].[{s.TableName}]");

        try
        {
            progress?.Report(new SyncProgress(0, 0, 0, 0, 0, "connecting"));

            // STD-INT-001/002/006: auth handled by DataverseSource; secret never logged.
            using ISyncSource source = s.Source == SourceKind.SqlTable
                ? await SqlTableSource.ConnectAsync(s, NonInteractive, ct).ConfigureAwait(false)
                : new DataverseSyncSource(
                    await Task.Run(() => DataverseSource.Connect(s, clientSecret, correlationId, NonInteractive), ct).ConfigureAwait(false), s);
            AppLog.Info($"Connected to source: {source.Description}.");

            progress?.Report(new SyncProgress(0, 0, 0, 0, 0, "metadata"));
            var meta = await source.GetMetadataAsync(ct).ConfigureAwait(false);
            var cols = ColumnMap.ApplyMappings(ColumnMap.Build(meta, s.SelectedAttributes), s.ColumnMappings);
            foreach (var m in cols.Where(c => !string.Equals(c.ColumnName, c.AttributeName, StringComparison.OrdinalIgnoreCase)
                                              && !c.ColumnName.EndsWith("_entity", StringComparison.OrdinalIgnoreCase)))
                AppLog.Info($"Mapping: {m.AttributeName} -> [{m.ColumnName}]");
            var attributes = ColumnMap.AttributeNames(cols);
            var primaryId = meta.PrimaryIdAttribute;
            var pkColumn = cols.First(c => c.IsPrimaryKey).ColumnName;
            AppLog.Info($"Metadata loaded: {attributes.Length} attributes, {cols.Count} columns.");

            // Tables such as activitymimeattachment have no modifiedon: copy the whole table in primary-key ranges
            // (parallel, resumable, keyset-paged). Incremental runs fall back to a full copy.
            var hasModifiedOn = ColumnMap.HasModifiedOn(meta);
            if (!hasModifiedOn)
                AppLog.Info($"'{s.EntityName}' has no {ModifiedOn} column: every run copies the full table in {primaryId} ranges " +
                            "(no time slicing, no incremental). Enable delete detection to remove deleted rows.");

            progress?.Report(new SyncProgress(0, 0, 0, 0, 0, "schema"));
            var target = new SqlTarget(s.BuildSqlConnectionString(), s.DatabaseName, s.SchemaName, s.TableName);
            await target.EnsureDatabaseAsync(ct).ConfigureAwait(false);
            var columnsAdded = await target.EnsureTableAsync(cols, ct).ConfigureAwait(false);
            AppLog.Info($"Target table {target.FullTableName} ready.");

            // documentbody is decoded to bytes for varbinary columns. If the existing destination column is a text column
            // (e.g. nvarchar(max) chosen on purpose), keep the base64 text as Dataverse returns it.
            if (cols.Any(c => c.Kind == ColumnKind.Base64Binary)
                && await target.GetTableColumnsAsync(ct).ConfigureAwait(false) is { } destCols)
            {
                cols = cols.Select(c =>
                {
                    if (c.Kind != ColumnKind.Base64Binary) return c;
                    var dest = destCols.FirstOrDefault(d => d.Name.Equals(c.ColumnName, StringComparison.OrdinalIgnoreCase));
                    if (dest is null || !IsTextType(dest.TypeDisplay)) return c;
                    AppLog.Info($"{target.FullTableName}.[{c.ColumnName}] is {dest.TypeDisplay}: {c.AttributeName} is stored as base64 text.");
                    return c with { SqlType = "nvarchar(max)", ClrType = typeof(string), Kind = ColumnKind.Value };
                }).ToList();
            }

            // Columns SQL Server fills itself (temporal period columns, computed, rowversion) cannot be written.
            if (await target.GetTableColumnsAsync(ct).ConfigureAwait(false) is { } existingCols)
            {
                var readOnly = cols.Where(c => existingCols.Any(d => d.IsReadOnly && d.Name.Equals(c.ColumnName, StringComparison.OrdinalIgnoreCase)))
                    .Select(c => c.ColumnName).ToList();
                if (readOnly.Count > 0)
                    throw new SyncConfigurationException(
                        $"These destination columns of {target.FullTableName} are filled by SQL Server itself (GENERATED ALWAYS / temporal period, "
                        + $"computed or rowversion) and cannot be written: {string.Join(", ", readOnly)}. Deselect them or map them to another column in the column mapping step.");
            }

            // Guard against reusing state written for another entity or org.
            var state = await target.GetStateAsync(ct).ConfigureAwait(false);
            var sourceUrl = source.SourceKey;
            var columnSet = string.Join(",", cols.Select(c => c.ColumnName).Order(StringComparer.OrdinalIgnoreCase));
            if (state?.EntityName is { } stateEntity && !stateEntity.Equals(s.EntityName, StringComparison.OrdinalIgnoreCase))
                throw new SyncConfigurationException(
                    $"Target {target.FullTableName} was synced from entity '{stateEntity}', not '{s.EntityName}'. Choose another target table.");
            if (state?.SourceUrl is { } stateUrl && !stateUrl.Equals(sourceUrl, StringComparison.OrdinalIgnoreCase))
                throw new SyncConfigurationException(
                    $"Target {target.FullTableName} was synced from another source ({stateUrl}). Choose another target table.");
            var originUnknown = state is not null && (state.EntityName is null || state.SourceUrl is null);
            var detectDeletes = s.DetectDeletes;
            if (detectDeletes && originUnknown)
            {
                AppLog.Info("Delete detection skipped this run: the sync state does not record the source entity/environment yet.");
                detectDeletes = false;
            }

            var pageSize = Math.Max(1, s.PageSize);
            // documentbody (annotation) and body (activitymimeattachment) hold base64 file content.
            var fileContent = attributes.Contains(DocumentBody, StringComparer.OrdinalIgnoreCase) ? DocumentBody
                : s.EntityName.Trim().Equals(MimeAttachment, StringComparison.OrdinalIgnoreCase)
                  && attributes.Contains(MimeAttachmentBody, StringComparer.OrdinalIgnoreCase) ? MimeAttachmentBody : null;
            if (fileContent is not null && pageSize > DocumentBodyMaxPageSize)
            {
                AppLog.Info($"{fileContent} selected: page size capped from {pageSize} to {DocumentBodyMaxPageSize} to bound payload size and request duration.");
                pageSize = DocumentBodyMaxPageSize;
            }

            // STD-DVB-020: honour x-ms-dop-hint as cap on parallelism.
            var recommended = Math.Max(1, source.RecommendedParallelism);
            var dop = Math.Clamp(s.Parallelism, 1, recommended);
            AppLog.Info($"Parallelism: requested={s.Parallelism}, server recommended={recommended}, using={dop}. PageSize={pageSize}.");
            var writeOptions = SqlWriteOptions.From(s);
            var writeDop = s.SqlWriteParallelism <= 0 ? dop : Math.Clamp(s.SqlWriteParallelism, 1, dop);
            AppLog.Info($"SQL writes: mode={writeOptions.Mode}, concurrent writers={writeDop}, batch={(writeOptions.BatchSize <= 0 ? "page" : writeOptions.BatchSize.ToString())}, " +
                        $"deadlock retries={writeOptions.DeadlockMaxRetries} (base {writeOptions.DeadlockRetryBaseDelayMs} ms).");

            // Checkpoints live in the target database; the session lock stops two processes syncing the same table.
            await using var store = new CheckpointStore(target, s.SchemaName);
            await store.EnsureAsync(ct).ConfigureAwait(false);
            await store.AcquireRunLockAsync(ct).ConfigureAwait(false);

            ResumableRun? resume = null;
            if (allowResume)
                resume = await store.FindResumableAsync(s.EntityName, sourceUrl, columnSet, ct).ConfigureAwait(false);
            else
                await store.AbandonUnfinishedAsync(ct).ConfigureAwait(false);

            Guid runId;
            DateTime lowerUtc, upperUtc, watermarkToStore;
            IReadOnlyList<SliceWork> slices;
            long rowsBefore = 0;
            var fullCopy = false;      // the run reads the whole table, so a row count gives a meaningful percentage
            long? knownTotal = null;   // exact count from the key-range scan

            if (resume is not null)
            {
                runId = resume.RunId;
                lowerUtc = resume.LowerUtc;
                upperUtc = resume.UpperUtc;
                watermarkToStore = resume.WatermarkToStore;
                detectDeletes = resume.DetectDeletes;
                slices = resume.PendingSlices;
                rowsBefore = resume.RowsWritten;
                fullCopy = resume.Mode.Equals(nameof(SyncMode.Full), StringComparison.OrdinalIgnoreCase);
                AppLog.Info($"Resuming interrupted {resume.Mode} run {runId} (attempt {resume.Attempts}): {rowsBefore:N0} rows already copied, " +
                            $"{slices.Count} slice(s) left, {slices.Count(x => !x.IsFresh)} continue mid-slice. Window {lowerUtc:O} .. {upperUtc:O}.");
            }
            else
            {
                // Window [lower, upper) on whole seconds. Upper is capped by the server clock (max modifiedon) so client clock skew
                // cannot push the watermark past rows the server has not stamped yet.
                var clientNowUtc = FloorToSecond(DateTime.UtcNow);
                if (!hasModifiedOn)
                {
                    if (s.Mode == SyncMode.Incremental)
                        AppLog.Info($"Incremental mode needs {ModifiedOn}; running a full copy (rows are upserted).");
                    lowerUtc = NoTimestampLowerUtc;
                    upperUtc = clientNowUtc;
                    watermarkToStore = state?.WatermarkUtc is { } prevNt && !originUnknown && prevNt > upperUtc ? prevNt : upperUtc;
                    runId = correlationId;
                    IReadOnlyList<Guid> boundaries = Array.Empty<Guid>();
                    if (dop > 1)
                        (boundaries, knownTotal) = await ComputeKeyBoundariesAsync(source, primaryId,
                            Math.Min(dop * KeyRangesPerWorker, MaxKeyRanges), progress, ct).ConfigureAwait(false);
                    slices = BuildKeySlices(boundaries);
                    fullCopy = true;
                    await store.CreateRunAsync(runId, s.EntityName, sourceUrl, columnSet, SyncMode.Full.ToString(), lowerUtc, upperUtc,
                        watermarkToStore, detectDeletes, slices, ct).ConfigureAwait(false);
                    AppLog.Info($"Run {runId} checkpointed with {slices.Count} {primaryId} range(s) (full table).");
                }
                else
                {
                    var serverMax = await source.GetMaxModifiedOnAsync(ct).ConfigureAwait(false);
                    upperUtc = clientNowUtc;
                    if (serverMax is { } sm)
                    {
                        var serverUpper = FloorToSecond(sm).AddSeconds(1);
                        if (serverUpper > clientNowUtc)
                            AppLog.Info($"Server max modifiedon {sm:O} is ahead of local clock {clientNowUtc:O}; using local clock as upper bound.");
                        upperUtc = serverUpper < clientNowUtc ? serverUpper : clientNowUtc;
                    }

                    DateTime? lower = null;
                    if (s.Mode == SyncMode.Incremental)
                    {
                        var missingColumns = state?.ColumnSet is { } stored
                            && cols.Any(c => !stored.Split(',').Contains(c.ColumnName, StringComparer.OrdinalIgnoreCase));
                        if (state?.WatermarkUtc is not { } wm)
                            AppLog.Info("No watermark found; running incremental as full window.");
                        else if (columnsAdded || missingColumns || state!.ColumnSet is null || originUnknown)
                            AppLog.Info("Column set changed (or is unrecorded) since the last run; running incremental as full window to backfill.");
                        else
                        {
                            lower = FloorToSecond(wm - IncrementalOverlap);
                            AppLog.Info($"Incremental from watermark {wm:O} (minus {IncrementalOverlap.TotalMinutes} min overlap).");
                        }
                    }

                    // Never move the watermark backwards.
                    watermarkToStore = state?.WatermarkUtc is { } prev && !originUnknown && prev > upperUtc ? prev : upperUtc;

                    fullCopy = lower is null;
                    if (lower is null)
                    {
                        var min = await source.GetMinModifiedOnAsync(ct).ConfigureAwait(false);
                        if (min is null)
                        {
                            AppLog.Info("Source table is empty; nothing to copy.");
                            long deletedEmpty = 0;
                            if (detectDeletes)
                                deletedEmpty = await RunDeletesAsync(source, target, primaryId, pkColumn, progress, ct).ConfigureAwait(false);
                            await target.SetStateAsync(watermarkToStore, 0, correlationId, s.EntityName, sourceUrl, columnSet, ct).ConfigureAwait(false);
                            return new SyncResult(0, deletedEmpty, sw.Elapsed, correlationId);
                        }
                        lower = FloorToSecond(min.Value);
                    }

                    lowerUtc = lower.Value;
                    if (upperUtc <= lowerUtc)
                        upperUtc = lowerUtc.AddSeconds(1);
                    AppLog.Info($"Window: {lowerUtc:O} .. {upperUtc:O} (exclusive).");

                    runId = correlationId;
                    slices = BuildSeeds(lowerUtc, upperUtc, dop);
                    await store.CreateRunAsync(runId, s.EntityName, sourceUrl, columnSet, s.Mode.ToString(), lowerUtc, upperUtc,
                        watermarkToStore, detectDeletes, slices, ct).ConfigureAwait(false);
                    AppLog.Info($"Run {runId} checkpointed with {slices.Count} seed slices.");
                }
            }

            AppLog.Info($"Source: {source.Description}.");
            var estimatedTotal = knownTotal ?? (fullCopy && progress is not null ? await TryEstimateRowsAsync(source, ct).ConfigureAwait(false) : null);

            long rowsWritten;
            try
            {
                rowsWritten = rowsBefore + await CopyAsync(source, target, store, runId, cols, primaryId, hasModifiedOn,
                    slices, rowsBefore, estimatedTotal, pageSize, dop, writeDop, writeOptions, sw, progress, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await TryRecordErrorAsync(store, runId, ex).ConfigureAwait(false);
                throw;
            }
            AppLog.Info($"Upserts complete: {rowsWritten} rows.");

            long deleted = 0;
            try
            {
                if (detectDeletes)
                    deleted = await RunDeletesAsync(source, target, primaryId, pkColumn, progress, ct).ConfigureAwait(false);

                // Only on full success; a crash before this point resumes the run (all slices done => only deletes and state remain).
                await target.SetStateAsync(watermarkToStore, rowsWritten, correlationId, s.EntityName, sourceUrl, columnSet, ct).ConfigureAwait(false);
                await store.SetRunStatusAsync(runId, "Completed", null, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await TryRecordErrorAsync(store, runId, ex).ConfigureAwait(false);
                throw;
            }

            var elapsed = sw.Elapsed;
            progress?.Report(new SyncProgress(rowsWritten, 0, 0, 0, Rate(rowsWritten, elapsed), "done", rowsWritten, dop));
            AppLog.Info($"Sync run finished. CorrelationId={correlationId} Rows={rowsWritten} Deleted={deleted} Elapsed={elapsed:hh\\:mm\\:ss} Watermark={watermarkToStore:O}");
            return new SyncResult(rowsWritten, deleted, elapsed, correlationId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AppLog.Info($"Sync run cancelled. CorrelationId={correlationId}");
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sync run failed. CorrelationId={correlationId}", ex);
            throw;
        }
    }

    // "nvarchar(max)", "varchar(8000)", "ntext" ... (DestinationColumn.TypeDisplay).
    private static bool IsTextType(string typeDisplay)
    {
        var t = typeDisplay.Split('(')[0].Trim();
        return t.Equals("nvarchar", StringComparison.OrdinalIgnoreCase) || t.Equals("varchar", StringComparison.OrdinalIgnoreCase)
            || t.Equals("nchar", StringComparison.OrdinalIgnoreCase) || t.Equals("char", StringComparison.OrdinalIgnoreCase)
            || t.Equals("ntext", StringComparison.OrdinalIgnoreCase) || t.Equals("text", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<long> RunDeletesAsync(ISyncSource source, SqlTarget target,
        string primaryId, string pkColumn, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new SyncProgress(0, 0, 0, 0, 0, "deletes"));
        AppLog.Info("Delete detection: reading all source ids.");
        var ids = source.ReadAllIdsAsync(primaryId, ct);
        var deleted = await target.DeleteMissingAsync(ids, pkColumn, ct).ConfigureAwait(false);
        AppLog.Info($"Delete detection: {deleted} rows removed from target.");
        return deleted;
    }

    private static async Task TryRecordErrorAsync(CheckpointStore store, Guid runId, Exception ex)
    {
        try { await store.RecordErrorAsync(runId, ex.Message, CancellationToken.None).ConfigureAwait(false); }
        catch { /* best effort: the checkpoint itself is already safe */ }
    }

    /// <summary>
    /// Reads every primary id once (in the source's own sort order) and returns up to <paramref name="parts"/> - 1 ids that
    /// split the table into ranges of roughly equal row counts. Boundaries are real ids compared by the server, so the
    /// uniqueidentifier byte order never has to be reproduced here. Memory stays bounded: the sample keeps every
    /// stride-th id and halves itself (doubling the stride) when it grows too large.
    /// </summary>
    private static async Task<(IReadOnlyList<Guid> Boundaries, long RowCount)> ComputeKeyBoundariesAsync(ISyncSource source, string primaryId, int parts,
        IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new SyncProgress(0, 0, 0, 0, 0, "planning ranges"));
        AppLog.Info($"Reading all {primaryId} values to split the table into {parts} ranges.");
        var sample = new List<Guid>(MaxKeySample + 1);
        long stride = 1, count = 0;
        await foreach (var batch in source.ReadAllIdsAsync(primaryId, ct).ConfigureAwait(false))
        {
            foreach (var id in batch)
            {
                if (count++ % stride == 0)
                    sample.Add(id);
                if (sample.Count > MaxKeySample)
                {
                    // Keep positions 0, 2, 4 ... = ids at multiples of the doubled stride.
                    var kept = 0;
                    for (var i = 0; i < sample.Count; i += 2)
                        sample[kept++] = sample[i];
                    sample.RemoveRange(kept, sample.Count - kept);
                    stride *= 2;
                }
            }
        }

        var boundaries = new List<Guid>(parts);
        for (var p = 1; p < parts && sample.Count > 1; p++)
        {
            var id = sample[(int)((long)sample.Count * p / parts)];
            if (id != Guid.Empty && (boundaries.Count == 0 || boundaries[^1] != id))
                boundaries.Add(id);
        }
        AppLog.Info($"{count:N0} rows found; {boundaries.Count + 1} {primaryId} range(s) of about {count / (boundaries.Count + 1):N0} rows.");
        return (boundaries, count);
    }

    // Ranges [null, b1), [b1, b2) ... [bn, null). FromUtc/ToUtc are synthetic, unique per range, and only key the checkpoint.
    private static List<SliceWork> BuildKeySlices(IReadOnlyList<Guid> boundaries)
    {
        var slices = new List<SliceWork>(boundaries.Count + 1);
        for (var i = 0; i <= boundaries.Count; i++)
            slices.Add(new SliceWork(NoTimestampLowerUtc.AddSeconds(i), NoTimestampLowerUtc.AddSeconds(i + 1),
                KeyFrom: i == 0 ? null : boundaries[i - 1],
                KeyTo: i == boundaries.Count ? null : boundaries[i]));
        return slices;
    }

    // dop equal slices of [lower, upper) on whole-second boundaries.
    private static List<SliceWork> BuildSeeds(DateTime lowerUtc, DateTime upperUtc, int dop)
    {
        var totalSeconds = (long)(upperUtc - lowerUtc).TotalSeconds;
        var seedCount = (int)Math.Max(1, Math.Min(dop, totalSeconds));
        var seeds = new List<SliceWork>(seedCount);
        for (var i = 0; i < seedCount; i++)
        {
            var from = lowerUtc.AddSeconds(totalSeconds * i / seedCount);
            var to = i == seedCount - 1 ? upperUtc : lowerUtc.AddSeconds(totalSeconds * (i + 1) / seedCount);
            if (to > from) seeds.Add(new SliceWork(from, to));
        }
        return seeds;
    }

    /// <summary>Copies the given slices; returns rows written in this attempt. Every page is checkpointed after its MERGE.</summary>
    private static async Task<long> CopyAsync(ISyncSource source, SqlTarget target, CheckpointStore store, Guid runId,
        IReadOnlyList<ColumnDef> cols, string primaryId, bool hasModifiedOn,
        IReadOnlyList<SliceWork> startSlices, long rowsBefore, long? estimatedTotal, int pageSize, int dop, int writeDop, SqlWriteOptions writeOptions, Stopwatch sw,
        IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        if (startSlices.Count == 0)
        {
            AppLog.Info("No slices left to copy.");
            return 0;
        }

        var channel = Channel.CreateUnbounded<SliceWork>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

        long rows = 0;
        int slicesDone = 0;
        int active = 0;
        int outstanding = 0;
        long lastReportMs = -ProgressIntervalMs;

        void Report(bool force)
        {
            if (progress is null) return;
            var now = sw.ElapsedMilliseconds;
            var last = Interlocked.Read(ref lastReportMs);
            if (!force && (now - last < ProgressIntervalMs || Interlocked.CompareExchange(ref lastReportMs, now, last) != last))
                return;
            if (force) Interlocked.Exchange(ref lastReportMs, now);
            var r = Interlocked.Read(ref rows);
            progress.Report(new SyncProgress(rowsBefore + r, Volatile.Read(ref slicesDone), channel.Reader.Count,
                Volatile.Read(ref active), Rate(r, sw.Elapsed), "copying", estimatedTotal, dop));
        }

        outstanding = startSlices.Count;
        foreach (var slice in startSlices)
            channel.Writer.TryWrite(slice);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = linked.Token;

        // Shared by all workers: caps concurrent SQL writers and serializes a batch whose deadlock retries ran out.
        using var writeGate = new SqlWriteGate(writeDop);
        var options = writeOptions with { Gate = writeGate };

        // One reader per worker, created before the workers start (fails fast on configuration problems).
        var readers = await source.CreateReadersAsync(dop, cols, primaryId, hasModifiedOn, ct).ConfigureAwait(false);
        try
        {
            Exception? firstError = null;

            async Task Worker(int workerId, ISliceReader reader)
            {
                try
                {
                    await using var writer = await target.OpenWriterAsync(cols, options, token).ConfigureAwait(false);
                    await foreach (var slice in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
                    {
                        Interlocked.Increment(ref active);
                        try
                        {
                            var first = true;
                            var split = false;
                            var pages = reader.ReadSliceAsync(slice, pageSize, token);
                            await foreach (var page in pages.ConfigureAwait(false))
                            {
                                if (first)
                                {
                                    first = false;
                                    // Only a slice with nothing written yet may split; a resumed slice continues from its cookie.
                                    if (hasModifiedOn && slice.IsFresh && page.MoreRecords && slice.ToUtc - slice.FromUtc >= MinSplitWidth && channel.Reader.Count < dop)
                                    {
                                        var mid = slice.FromUtc.AddSeconds(Math.Floor((slice.ToUtc - slice.FromUtc).TotalSeconds / 2));
                                        var left = new SliceWork(slice.FromUtc, mid);
                                        var right = new SliceWork(mid, slice.ToUtc);
                                        await store.SplitSliceAsync(runId, slice, left, right, token).ConfigureAwait(false);
                                        Interlocked.Add(ref outstanding, 2);
                                        channel.Writer.TryWrite(left);
                                        channel.Writer.TryWrite(right);
                                        split = true;
                                        break;
                                    }
                                }

                                if (page.Rows.Count > 0)
                                {
                                    using var table = ColumnMap.ToDataTable(cols, page.Rows);
                                    await writer.UpsertAsync(table, token).ConfigureAwait(false);
                                    Interlocked.Add(ref rows, page.Rows.Count);
                                }

                                // Checkpoint after the MERGE: a crash repeats at most this page, and MERGE makes that harmless.
                                await store.SavePageAsync(runId, slice, page.PageNumber + 1, page.NextPagingCookie, page.Rows.Count,
                                    done: !page.MoreRecords, token).ConfigureAwait(false);
                                Report(false);
                            }

                            if (!split)
                                Interlocked.Increment(ref slicesDone);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref active);
                        }

                        if (Interlocked.Decrement(ref outstanding) == 0)
                            channel.Writer.TryComplete();
                        Report(false);
                    }
                }
                catch (Exception ex)
                {
                    if (Interlocked.CompareExchange(ref firstError, ex, null) is null && ex is not OperationCanceledException)
                        AppLog.Error($"Worker {workerId} failed.", ex);
                    linked.Cancel();
                    channel.Writer.TryComplete();
                    throw;
                }
            }

            var tasks = readers.Select((r, i) => Task.Run(() => Worker(i, r), CancellationToken.None)).ToArray();
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch
            {
                // Prefer the original failure over secondary cancellations.
                var original = tasks.Where(t => t.IsFaulted)
                    .SelectMany(t => t.Exception!.InnerExceptions)
                    .FirstOrDefault(e => e is not OperationCanceledException);
                ct.ThrowIfCancellationRequested();
                if (original is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original).Throw();
                if (firstError is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstError).Throw();
                throw;
            }
        }
        finally
        {
            foreach (var r in readers)
            {
                try { await r.DisposeAsync().ConfigureAwait(false); } catch { /* best effort */ }
            }
        }

        Report(true);
        return Interlocked.Read(ref rows);
    }

    private static DateTime FloorToSecond(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    private static double Rate(long rows, TimeSpan elapsed) =>
        elapsed.TotalSeconds > 0 ? rows / elapsed.TotalSeconds : 0;
}
