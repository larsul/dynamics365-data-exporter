using System.Data;
using Microsoft.Data.SqlClient;

namespace DataverseToSql.Sync;

/// <summary>A configuration or safety error. Retrying the run does not help, so the engine never retries it.</summary>
public sealed class SyncConfigurationException(string message) : Exception(message);

/// <summary>
/// One slice of a run, with the page to continue from. Normally a modifiedon time window [FromUtc, ToUtc).
/// For tables without modifiedon it is a primary-key range [KeyFrom, KeyTo) (null = unbounded, compared in the source's
/// uniqueidentifier order); FromUtc/ToUtc are then synthetic values that only identify the slice in the checkpoint.
/// </summary>
public sealed record SliceWork(DateTime FromUtc, DateTime ToUtc, int NextPage = 1, string? PagingCookie = null,
    Guid? KeyFrom = null, Guid? KeyTo = null)
{
    /// <summary>True when no page of this slice has been written yet.</summary>
    public bool IsFresh => NextPage <= 1;
}

/// <summary>Summary of an interrupted run, for the UI.</summary>
public sealed record InterruptedRunInfo(Guid RunId, string Mode, DateTime StartedUtc, long RowsWritten, int SlicesLeft, string? LastError);

/// <summary>One past or current run of a destination table, for the run history.</summary>
public sealed record RunHistoryItem(Guid RunId, string Mode, string Status, DateTime StartedUtc, DateTime UpdatedUtc, long RowsWritten, string? LastError)
{
    /// <summary>Time between start and the last recorded progress.</summary>
    public TimeSpan Duration => UpdatedUtc > StartedUtc ? UpdatedUtc - StartedUtc : TimeSpan.Zero;
}

/// <summary>An interrupted run that can be continued.</summary>
public sealed record ResumableRun(
    Guid RunId, string Mode, DateTime LowerUtc, DateTime UpperUtc, DateTime WatermarkToStore,
    bool DetectDeletes, long RowsWritten, int Attempts, IReadOnlyList<SliceWork> PendingSlices);

/// <summary>
/// Persists run progress in the target database so a crashed or cancelled run continues where it stopped.
/// Tables: [schema].[__DataverseSyncRun] (one row per run) and [schema].[__DataverseSyncSlice] (one row per time slice,
/// with the next page number and paging cookie). A page is checkpointed only after its MERGE succeeded, so a crash
/// repeats at most one page per worker, and MERGE makes the repeat harmless.
/// </summary>
public sealed class CheckpointStore : IAsyncDisposable
{
    private const string RunTable = "__DataverseSyncRun";
    private const string SliceTable = "__DataverseSyncSlice";

    private readonly SqlTarget _target;
    private readonly string _runTable;
    private readonly string _sliceTable;
    private SqlConnection? _lockConnection;

    /// <summary>Creates a store in the same database and schema as <paramref name="target"/>.</summary>
    public CheckpointStore(SqlTarget target, string schemaName)
    {
        _target = target;
        var schema = string.IsNullOrWhiteSpace(schemaName) ? "dbo" : schemaName;
        _runTable = $"{SqlTarget.Q(schema)}.{SqlTarget.Q(RunTable)}";
        _sliceTable = $"{SqlTarget.Q(schema)}.{SqlTarget.Q(SliceTable)}";
    }

    /// <summary>Creates the checkpoint tables when missing.</summary>
    public async Task EnsureAsync(CancellationToken ct)
    {
        await using var conn = await _target.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"IF OBJECT_ID(@rt, N'U') IS NULL CREATE TABLE {_runTable} (" +
            "RunId uniqueidentifier NOT NULL PRIMARY KEY, TableName nvarchar(256) NOT NULL, EntityName nvarchar(256) NOT NULL, " +
            "SourceUrl nvarchar(512) NOT NULL, ColumnSet nvarchar(max) NOT NULL, Mode nvarchar(20) NOT NULL, " +
            "LowerUtc datetime2(3) NOT NULL, UpperUtc datetime2(3) NOT NULL, WatermarkToStore datetime2(3) NOT NULL, " +
            "DetectDeletes bit NOT NULL, Status nvarchar(20) NOT NULL, RowsWritten bigint NOT NULL DEFAULT 0, " +
            "Attempts int NOT NULL DEFAULT 1, StartedUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(), " +
            "UpdatedUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(), LastError nvarchar(4000) NULL);" +
            $"IF OBJECT_ID(@st, N'U') IS NULL CREATE TABLE {_sliceTable} (" +
            "RunId uniqueidentifier NOT NULL, FromUtc datetime2(3) NOT NULL, ToUtc datetime2(3) NOT NULL, " +
            "Status nvarchar(10) NOT NULL, NextPage int NOT NULL DEFAULT 1, PagingCookie nvarchar(max) NULL, " +
            "RowsWritten bigint NOT NULL DEFAULT 0, UpdatedUtc datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(), " +
            "KeyFrom uniqueidentifier NULL, KeyTo uniqueidentifier NULL, " +
            "CONSTRAINT PK_DataverseSyncSlice PRIMARY KEY (RunId, FromUtc, ToUtc));" +
            // Tables created by earlier versions: add the key-range columns.
            $"IF COL_LENGTH(@st, 'KeyFrom') IS NULL ALTER TABLE {_sliceTable} ADD KeyFrom uniqueidentifier NULL, KeyTo uniqueidentifier NULL;";
        cmd.Parameters.Add("@rt", SqlDbType.NVarChar, 600).Value = _runTable;
        cmd.Parameters.Add("@st", SqlDbType.NVarChar, 600).Value = _sliceTable;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes an exclusive session lock on the target table so two processes never run the same sync at once.
    /// The lock is released on dispose, or by SQL Server when the process dies.
    /// </summary>
    public async Task AcquireRunLockAsync(CancellationToken ct)
    {
        if (_lockConnection is not null) return;
        // Unpooled: closing a pooled connection keeps its session, and this lock, alive, which made the app's own retry
        // fail with "Another sync is already running".
        var conn = await _target.OpenAsync(ct, pooled: false).ConfigureAwait(false);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', " +
                              "@LockOwner = 'Session', @LockTimeout = 0; SELECT @r;";
            cmd.Parameters.Add("@res", SqlDbType.NVarChar, 255).Value = "DataverseToSql:" + _target.FullTableName;
            var result = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            if (result < 0)
                throw new SyncConfigurationException(
                    $"Another sync is already running for {_target.FullTableName}. Wait for it to finish, or stop it first.");
            _lockConnection = conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Returns the latest unfinished run for this table, or null. A run for another source or column set is abandoned.</summary>
    public async Task<ResumableRun?> FindResumableAsync(string entityName, string sourceUrl, string columnSet, CancellationToken ct)
    {
        await using var conn = await _target.OpenAsync(ct).ConfigureAwait(false);
        Guid runId;
        string mode;
        DateTime lower, upper, watermark;
        bool deletes;
        long rows;
        int attempts;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                $"SELECT TOP 1 RunId, EntityName, SourceUrl, ColumnSet, Mode, LowerUtc, UpperUtc, WatermarkToStore, DetectDeletes, " +
                $"RowsWritten, Attempts FROM {_runTable} WHERE TableName = @t AND Status = N'Running' ORDER BY StartedUtc DESC;";
            cmd.Parameters.Add("@t", SqlDbType.NVarChar, 256).Value = _target.FullTableName;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
                return null;

            runId = r.GetGuid(0);
            var sameSource = r.GetString(1).Equals(entityName, StringComparison.OrdinalIgnoreCase)
                             && r.GetString(2).Equals(sourceUrl, StringComparison.OrdinalIgnoreCase)
                             && r.GetString(3).Equals(columnSet, StringComparison.OrdinalIgnoreCase);
            if (!sameSource)
            {
                await r.CloseAsync().ConfigureAwait(false);
                AppLog.Info($"Interrupted run {runId} used another entity, environment or column set; it is abandoned and a new run starts.");
                await SetRunStatusAsync(runId, "Abandoned", null, ct).ConfigureAwait(false);
                return null;
            }

            mode = r.GetString(4);
            lower = Utc(r.GetDateTime(5));
            upper = Utc(r.GetDateTime(6));
            watermark = Utc(r.GetDateTime(7));
            deletes = r.GetBoolean(8);
            rows = r.GetInt64(9);
            attempts = r.GetInt32(10);
        }

        var pending = new List<SliceWork>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                $"SELECT FromUtc, ToUtc, NextPage, PagingCookie, KeyFrom, KeyTo FROM {_sliceTable} WHERE RunId = @id AND Status = N'Pending' ORDER BY FromUtc;";
            cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = runId;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                pending.Add(new SliceWork(Utc(r.GetDateTime(0)), Utc(r.GetDateTime(1)), r.GetInt32(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetGuid(4), r.IsDBNull(5) ? null : r.GetGuid(5)));
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"UPDATE {_runTable} SET Attempts = Attempts + 1, UpdatedUtc = SYSUTCDATETIME() WHERE RunId = @id;";
            cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = runId;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return new ResumableRun(runId, mode, lower, upper, watermark, deletes, rows, attempts + 1, pending);
    }

    /// <summary>Describes the latest unfinished run of this table without changing it, or null.</summary>
    public async Task<InterruptedRunInfo?> PeekInterruptedAsync(CancellationToken ct)
    {
        await using var conn = await _target.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"IF OBJECT_ID(@rt, N'U') IS NOT NULL AND OBJECT_ID(@st, N'U') IS NOT NULL " +
            $"SELECT TOP 1 r.RunId, r.Mode, r.StartedUtc, r.RowsWritten, " +
            $"(SELECT COUNT(*) FROM {_sliceTable} s WHERE s.RunId = r.RunId AND s.Status = N'Pending'), r.LastError " +
            $"FROM {_runTable} r WHERE r.TableName = @t AND r.Status = N'Running' ORDER BY r.StartedUtc DESC;";
        cmd.Parameters.Add("@rt", SqlDbType.NVarChar, 600).Value = _runTable;
        cmd.Parameters.Add("@st", SqlDbType.NVarChar, 600).Value = _sliceTable;
        cmd.Parameters.Add("@t", SqlDbType.NVarChar, 256).Value = _target.FullTableName;
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
            return null;
        return new InterruptedRunInfo(r.GetGuid(0), r.GetString(1), Utc(r.GetDateTime(2)), r.GetInt64(3), r.GetInt32(4),
            r.IsDBNull(5) ? null : r.GetString(5));
    }

    /// <summary>Returns the latest <paramref name="top"/> runs of this table, newest first, without changing anything.</summary>
    public async Task<IReadOnlyList<RunHistoryItem>> GetRecentRunsAsync(int top, CancellationToken ct)
    {
        var list = new List<RunHistoryItem>();
        await using var conn = await _target.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"IF OBJECT_ID(@rt, N'U') IS NOT NULL " +
            $"SELECT TOP (@n) RunId, Mode, Status, StartedUtc, UpdatedUtc, RowsWritten, LastError FROM {_runTable} " +
            "WHERE TableName = @t ORDER BY StartedUtc DESC;";
        cmd.Parameters.Add("@rt", SqlDbType.NVarChar, 600).Value = _runTable;
        cmd.Parameters.Add("@t", SqlDbType.NVarChar, 256).Value = _target.FullTableName;
        cmd.Parameters.Add("@n", SqlDbType.Int).Value = Math.Max(1, top);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(new RunHistoryItem(r.GetGuid(0), r.GetString(1), r.GetString(2), Utc(r.GetDateTime(3)), Utc(r.GetDateTime(4)),
                r.GetInt64(5), r.IsDBNull(6) ? null : r.GetString(6)));
        return list;
    }

    /// <summary>Marks every unfinished run of this table as abandoned (user chose a fresh start).</summary>
    public async Task AbandonUnfinishedAsync(CancellationToken ct)
    {
        await using var conn = await _target.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE {_runTable} SET Status = N'Abandoned', UpdatedUtc = SYSUTCDATETIME() WHERE TableName = @t AND Status = N'Running';";
        cmd.Parameters.Add("@t", SqlDbType.NVarChar, 256).Value = _target.FullTableName;
        var n = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (n > 0) AppLog.Info($"Fresh start: {n} interrupted run(s) abandoned.");
    }

    /// <summary>Records a new run and its seed slices in one transaction.</summary>
    public async Task CreateRunAsync(Guid runId, string entityName, string sourceUrl, string columnSet, string mode,
        DateTime lowerUtc, DateTime upperUtc, DateTime watermarkToStore, bool detectDeletes,
        IReadOnlyList<SliceWork> seeds, CancellationToken ct)
    {
        await using var conn = await _target.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                $"INSERT INTO {_runTable} (RunId, TableName, EntityName, SourceUrl, ColumnSet, Mode, LowerUtc, UpperUtc, WatermarkToStore, DetectDeletes, Status) " +
                "VALUES (@id, @t, @e, @u, @cs, @m, @lo, @hi, @w, @d, N'Running');";
            cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = runId;
            cmd.Parameters.Add("@t", SqlDbType.NVarChar, 256).Value = _target.FullTableName;
            cmd.Parameters.Add("@e", SqlDbType.NVarChar, 256).Value = entityName;
            cmd.Parameters.Add("@u", SqlDbType.NVarChar, 512).Value = sourceUrl;
            cmd.Parameters.Add("@cs", SqlDbType.NVarChar, -1).Value = columnSet;
            cmd.Parameters.Add("@m", SqlDbType.NVarChar, 20).Value = mode;
            AddDate(cmd, "@lo", lowerUtc);
            AddDate(cmd, "@hi", upperUtc);
            AddDate(cmd, "@w", watermarkToStore);
            cmd.Parameters.Add("@d", SqlDbType.Bit).Value = detectDeletes;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        foreach (var slice in seeds)
            await InsertSliceAsync(conn, tx, runId, slice, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Replaces <paramref name="parent"/> with its two halves in one transaction.</summary>
    public async Task SplitSliceAsync(Guid runId, SliceWork parent, SliceWork left, SliceWork right, CancellationToken ct)
    {
        await using var conn = await _target.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"UPDATE {_sliceTable} SET Status = N'Split', UpdatedUtc = SYSUTCDATETIME() WHERE RunId = @id AND FromUtc = @f AND ToUtc = @to;";
            AddSliceKey(cmd, runId, parent);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await InsertSliceAsync(conn, tx, runId, left, ct).ConfigureAwait(false);
        await InsertSliceAsync(conn, tx, runId, right, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Records that a page was written. <paramref name="nextPage"/>/<paramref name="cookie"/> are where to continue; done=true closes the slice.</summary>
    public async Task SavePageAsync(Guid runId, SliceWork slice, int nextPage, string? cookie, int rowsAdded, bool done, CancellationToken ct)
    {
        await using var conn = await _target.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"UPDATE {_sliceTable} SET Status = CASE WHEN @done = 1 THEN N'Done' ELSE Status END, NextPage = @np, " +
            "PagingCookie = CASE WHEN @done = 1 THEN NULL ELSE @c END, RowsWritten = RowsWritten + @n, UpdatedUtc = SYSUTCDATETIME() " +
            "WHERE RunId = @id AND FromUtc = @f AND ToUtc = @to;" +
            $"UPDATE {_runTable} SET RowsWritten = RowsWritten + @n, UpdatedUtc = SYSUTCDATETIME() WHERE RunId = @id;";
        AddSliceKey(cmd, runId, slice);
        cmd.Parameters.Add("@done", SqlDbType.Bit).Value = done;
        cmd.Parameters.Add("@np", SqlDbType.Int).Value = nextPage;
        cmd.Parameters.Add("@c", SqlDbType.NVarChar, -1).Value = (object?)cookie ?? DBNull.Value;
        cmd.Parameters.Add("@n", SqlDbType.BigInt).Value = rowsAdded;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Sets the run status: Running, Completed, Failed or Abandoned. Failed runs stay resumable only while Running.</summary>
    public async Task SetRunStatusAsync(Guid runId, string status, string? error, CancellationToken ct)
    {
        await using var conn = await _target.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"UPDATE {_runTable} SET Status = @s, LastError = COALESCE(@err, LastError), UpdatedUtc = SYSUTCDATETIME() WHERE RunId = @id;";
        cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = runId;
        cmd.Parameters.Add("@s", SqlDbType.NVarChar, 20).Value = status;
        cmd.Parameters.Add("@err", SqlDbType.NVarChar, 4000).Value = error is null ? DBNull.Value : Truncate(error, 4000);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Stores the last error on a run that stays resumable.</summary>
    public Task RecordErrorAsync(Guid runId, string error, CancellationToken ct) => SetRunStatusAsync(runId, "Running", error, ct);

    /// <summary>Releases the run lock.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_lockConnection is not null)
        {
            try
            {
                // Release explicitly; closing the (unpooled) session would release it too.
                await using var cmd = _lockConnection.CreateCommand();
                cmd.CommandText = "EXEC sp_releaseapplock @Resource = @res, @LockOwner = 'Session';";
                cmd.Parameters.Add("@res", SqlDbType.NVarChar, 255).Value = "DataverseToSql:" + _target.FullTableName;
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                // Connection already broken: SQL Server drops the session and its lock.
            }
            await _lockConnection.DisposeAsync().ConfigureAwait(false);
            _lockConnection = null;
        }
    }

    private async Task InsertSliceAsync(SqlConnection conn, SqlTransaction tx, Guid runId, SliceWork slice, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {_sliceTable} (RunId, FromUtc, ToUtc, Status, NextPage, KeyFrom, KeyTo) VALUES (@id, @f, @to, N'Pending', 1, @kf, @kt);";
        AddSliceKey(cmd, runId, slice);
        cmd.Parameters.Add("@kf", SqlDbType.UniqueIdentifier).Value = (object?)slice.KeyFrom ?? DBNull.Value;
        cmd.Parameters.Add("@kt", SqlDbType.UniqueIdentifier).Value = (object?)slice.KeyTo ?? DBNull.Value;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static void AddSliceKey(SqlCommand cmd, Guid runId, SliceWork slice)
    {
        cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = runId;
        AddDate(cmd, "@f", slice.FromUtc);
        AddDate(cmd, "@to", slice.ToUtc);
    }

    private static void AddDate(SqlCommand cmd, string name, DateTime value)
    {
        var p = cmd.Parameters.Add(name, SqlDbType.DateTime2);
        p.Scale = 3;
        p.Value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
