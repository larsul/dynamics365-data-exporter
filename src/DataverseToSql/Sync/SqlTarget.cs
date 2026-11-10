using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;

namespace DataverseToSql.Sync;

/// <summary>SQL Server destination: creates database, schema, table and sync-state table; reads/writes watermarks.</summary>
public sealed class SqlTarget
{
    private const string StateTable = "__DataverseSyncState";
    private const string SyncedOnColumn = "__syncedon";
    private const double MaxDeleteFraction = 0.10; // delete detection aborts above 10% of the target...
    private const long MaxDeleteRowsFloor = 1000;  // ...unless the absolute number is small

    private readonly string _serverConnectionString;
    private readonly string _databaseName;
    private readonly string _schemaName;
    private readonly string _tableName;

    /// <summary>Creates a target for the given server, database, schema and table.</summary>
    public SqlTarget(string serverConnectionString, string databaseName, string schemaName, string tableName)
    {
        if (string.IsNullOrWhiteSpace(serverConnectionString)) throw new ArgumentException("Connection string is required.", nameof(serverConnectionString));
        if (string.IsNullOrWhiteSpace(databaseName)) throw new ArgumentException("Database name is required.", nameof(databaseName));
        if (string.IsNullOrWhiteSpace(tableName)) throw new ArgumentException("Table name is required.", nameof(tableName));

        _serverConnectionString = serverConnectionString;
        _databaseName = databaseName;
        _schemaName = string.IsNullOrWhiteSpace(schemaName) ? "dbo" : schemaName;
        _tableName = tableName;
        FullTableName = $"{Q(_schemaName)}.{Q(_tableName)}";
    }

    /// <summary>Bracket-quoted "[schema].[table]".</summary>
    public string FullTableName { get; }

    private string StateTableName => $"{Q(_schemaName)}.{Q(StateTable)}";

    /// <summary>Bracket-quotes an identifier, escaping ']' as ']]'.</summary>
    internal static string Q(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    private string MasterConnectionString =>
        new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = "master" }.ConnectionString;

    /// <summary>Returns null when the target database opens, else the error.</summary>
    private async Task<SqlException?> TryOpenDatabaseAsync(CancellationToken ct)
    {
        // Pooling off: a failed pooled open puts the pool into a "blocking period" that would replay this error
        // for the next opens, even right after the database has been created.
        var cs = new SqlConnectionStringBuilder(DatabaseConnectionString) { Pooling = false }.ConnectionString;
        try
        {
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return null;
        }
        catch (SqlException ex)
        {
            return ex;
        }
    }

    internal string DatabaseConnectionString =>
        new SqlConnectionStringBuilder(_serverConnectionString) { InitialCatalog = _databaseName }.ConnectionString;

    /// <summary>
    /// Opens a connection to the target database. <paramref name="pooled"/> false gives a dedicated session that really
    /// ends on dispose (needed for session-owned locks: a pooled connection keeps its session, and its locks, alive).
    /// </summary>
    internal async Task<SqlConnection> OpenAsync(CancellationToken ct, bool pooled = true)
    {
        var conn = new SqlConnection(pooled ? DatabaseConnectionString
            : new SqlConnectionStringBuilder(DatabaseConnectionString) { Pooling = false }.ConnectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Creates the database (via master) and schema when missing.</summary>
    public async Task EnsureDatabaseAsync(CancellationToken ct)
    {
        // Azure SQL users often exist only in the user database (contained users) and cannot open master.
        // So try the database first and touch master only when the database cannot be opened.
        var openError = await TryOpenDatabaseAsync(ct).ConfigureAwait(false);
        if (openError is not null)
        {
            try
            {
                await using var master = new SqlConnection(MasterConnectionString);
                await master.OpenAsync(ct).ConfigureAwait(false);
                await using var cmd = master.CreateCommand();
                cmd.CommandTimeout = 600; // Azure SQL database creation can take minutes
                cmd.CommandText =
                    "DECLARE @created bit = 0; " +
                    "IF DB_ID(@n) IS NULL BEGIN DECLARE @sql nvarchar(400) = N'CREATE DATABASE ' + QUOTENAME(@n); EXEC sp_executesql @sql; SET @created = 1; END; " +
                    "SELECT @created, CAST(SERVERPROPERTY('EngineEdition') AS int);";
                cmd.Parameters.Add("@n", SqlDbType.NVarChar, 128).Value = _databaseName;
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await r.ReadAsync(ct).ConfigureAwait(false) && r.GetBoolean(0))
                {
                    AppLog.Info($"Database [{_databaseName}] created.");
                    if (r.GetInt32(1) == 5)
                        AppLog.Info("Azure SQL: the new database uses the server's default service objective. Resize it in the Azure portal if needed.");
                }
            }
            catch (SqlException masterError)
            {
                throw new InvalidOperationException(
                    $"Cannot open database [{_databaseName}] ({openError.Message}) and cannot create it through master ({masterError.Message}). " +
                    "On Azure SQL, create the database in the portal and grant your account db_owner (or db_ddladmin + db_datareader + db_datawriter).",
                    openError);
            }
        }

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var schemaCmd = conn.CreateCommand();
        schemaCmd.CommandText = "IF SCHEMA_ID(@s) IS NULL BEGIN DECLARE @sql nvarchar(400) = N'CREATE SCHEMA ' + QUOTENAME(@s); EXEC sp_executesql @sql; END";
        schemaCmd.Parameters.Add("@s", SqlDbType.NVarChar, 128).Value = _schemaName;
        await schemaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the table, or adds missing columns and widens too-narrow nvarchar/varbinary columns, plus the sync-state table.
    /// Never drops columns. Returns true when a column was added to an existing table.
    /// </summary>
    public async Task<bool> EnsureTableAsync(IReadOnlyList<ColumnDef> cols, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cols);
        var columns = Distinct(cols);
        var pk = columns.FirstOrDefault(c => c.IsPrimaryKey)
                 ?? throw new InvalidOperationException("Column list has no primary key column.");

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);

        var existing = new Dictionary<string, (string Type, int MaxLength)>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT c.name, t.name, CAST(c.max_length AS int) FROM sys.columns c " +
                "JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID(@t, N'U');";
            cmd.Parameters.Add("@t", SqlDbType.NVarChar, 600).Value = FullTableName;
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                existing[reader.GetString(0)] = (reader.GetString(1), reader.GetInt32(2));
        }
        var tableExists = existing.Count > 0;
        var added = false;

        string ddl;
        if (!tableExists)
        {
            var sb = new StringBuilder();
            sb.Append("CREATE TABLE ").Append(FullTableName).AppendLine(" (");
            foreach (var c in columns)
            {
                sb.Append("    ").Append(Q(c.ColumnName)).Append(' ').Append(c.SqlType)
                  .AppendLine(c.IsPrimaryKey ? " NOT NULL," : " NULL,");
            }
            sb.Append("    ").Append(Q(SyncedOnColumn)).AppendLine(" datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME(),");
            sb.Append("    CONSTRAINT ").Append(Q("PK_" + _tableName)).Append(" PRIMARY KEY CLUSTERED (").Append(Q(pk.ColumnName)).AppendLine(")");
            sb.Append(");");
            ddl = sb.ToString();
        }
        else
        {
            var sb = new StringBuilder();
            foreach (var c in columns)
            {
                if (!existing.TryGetValue(c.ColumnName, out var cur))
                {
                    sb.Append("ALTER TABLE ").Append(FullTableName).Append(" ADD ").Append(Q(c.ColumnName)).Append(' ').Append(c.SqlType).AppendLine(" NULL;");
                    added = true;
                    continue;
                }
                if (c.IsPrimaryKey) continue;

                var (baseType, length) = ParseSqlType(c.SqlType);
                if (!baseType.Equals(cur.Type, StringComparison.OrdinalIgnoreCase))
                {
                    // Existing (possibly hand-made) table: SqlBulkCopy converts compatible values (e.g. nvarchar -> varchar,
                    // datetime2 -> datetime). A value that cannot convert fails the page with a clear SQL error.
                    if (!(cur.Type.Equals("nvarchar", StringComparison.OrdinalIgnoreCase) && cur.MaxLength == -1))
                        AppLog.Info($"Note: {FullTableName}.{Q(c.ColumnName)} is {cur.Type}; source {c.AttributeName} maps to {c.SqlType}. " +
                                    "Values are converted by SQL Server.");
                    continue;
                }

                // Widen variable-length columns (max_length is bytes, nvarchar = 2 per char, -1 = max).
                if (length is { } want && cur.MaxLength != -1)
                {
                    var curLen = baseType.Equals("nvarchar", StringComparison.OrdinalIgnoreCase) ? cur.MaxLength / 2 : cur.MaxLength;
                    if (want == -1 || want > curLen)
                    {
                        AppLog.Info($"Widening {FullTableName}.{Q(c.ColumnName)} from {cur.Type}({curLen}) to {c.SqlType}.");
                        sb.Append("ALTER TABLE ").Append(FullTableName).Append(" ALTER COLUMN ").Append(Q(c.ColumnName)).Append(' ').Append(c.SqlType).AppendLine(" NULL;");
                    }
                }
            }
            if (!existing.ContainsKey(SyncedOnColumn))
                sb.Append("ALTER TABLE ").Append(FullTableName).Append(" ADD ").Append(Q(SyncedOnColumn))
                  .AppendLine(" datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME();");
            ddl = sb.ToString();
        }

        if (ddl.Length > 0)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0; // adding/widening columns on a large table may take time
            cmd.CommandText = ddl;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // An existing table may have no index on the key column; every MERGE would then scan the whole table.
        if (tableExists)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
                "WHERE ic.object_id = OBJECT_ID(@t, N'U') AND ic.key_ordinal = 1 AND c.name = @c;";
            cmd.Parameters.Add("@t", SqlDbType.NVarChar, 600).Value = FullTableName;
            cmd.Parameters.Add("@c", SqlDbType.NVarChar, 128).Value = pk.ColumnName;
            var indexed = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false)) > 0;
            if (!indexed)
            {
                var ix = Q("IX_" + _tableName + "_" + pk.ColumnName);
                AppLog.Info($"Key column {Q(pk.ColumnName)} of {FullTableName} has no index; creating {ix} so MERGE does not scan the table.");
                await using var create = conn.CreateCommand();
                create.CommandTimeout = 0;
                create.CommandText = $"CREATE NONCLUSTERED INDEX {ix} ON {FullTableName} ({Q(pk.ColumnName)});";
                await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                $"IF OBJECT_ID(@st, N'U') IS NULL CREATE TABLE {StateTableName} (" +
                "TableName nvarchar(256) NOT NULL PRIMARY KEY, WatermarkUtc datetime2(3) NULL, LastRunUtc datetime2(3) NULL, " +
                "LastRows bigint NULL, LastCorrelationId uniqueidentifier NULL);";
            cmd.Parameters.Add("@st", SqlDbType.NVarChar, 600).Value = StateTableName;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Separate batch so the new columns are visible to later statements.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                $"IF COL_LENGTH(@st, 'EntityName') IS NULL ALTER TABLE {StateTableName} ADD EntityName nvarchar(256) NULL;" +
                $"IF COL_LENGTH(@st, 'SourceUrl') IS NULL ALTER TABLE {StateTableName} ADD SourceUrl nvarchar(512) NULL;" +
                $"IF COL_LENGTH(@st, 'ColumnSet') IS NULL ALTER TABLE {StateTableName} ADD ColumnSet nvarchar(max) NULL;";
            cmd.Parameters.Add("@st", SqlDbType.NVarChar, 600).Value = StateTableName;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return added;
    }

    /// <summary>
    /// Reads the columns of the destination table. Returns null when the database or the table does not exist yet.
    /// </summary>
    public async Task<IReadOnlyList<DestinationColumn>?> GetTableColumnsAsync(CancellationToken ct)
    {
        var openError = await TryOpenDatabaseAsync(ct).ConfigureAwait(false);
        if (openError is not null)
        {
            // Distinguish "database missing" from "no access" through master; if master is closed too, report the real error.
            try
            {
                await using var master = new SqlConnection(MasterConnectionString);
                await master.OpenAsync(ct).ConfigureAwait(false);
                await using var check = master.CreateCommand();
                check.CommandText = "SELECT DB_ID(@n);";
                check.Parameters.Add("@n", SqlDbType.NVarChar, 128).Value = _databaseName;
                if (await check.ExecuteScalarAsync(ct).ConfigureAwait(false) is null or DBNull)
                    return null;
            }
            catch (SqlException)
            {
                // fall through to the original error
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(openError).Throw();
        }

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT c.name, t.name, CAST(c.max_length AS int), CAST(c.precision AS int), CAST(c.scale AS int), c.is_nullable, " +
            "CAST(CASE WHEN c.default_object_id <> 0 OR c.is_identity = 1 OR c.is_computed = 1 THEN 1 ELSE 0 END AS bit), " +
            // Columns SQL Server fills itself: computed, temporal period (GENERATED ALWAYS) and rowversion.
            "CAST(CASE WHEN c.is_computed = 1 OR c.generated_always_type <> 0 OR t.name = 'timestamp' THEN 1 ELSE 0 END AS bit) " +
            "FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id " +
            "WHERE c.object_id = OBJECT_ID(@t, N'U') ORDER BY c.column_id;";
        cmd.Parameters.Add("@t", SqlDbType.NVarChar, 600).Value = FullTableName;
        var list = new List<DestinationColumn>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var type = r.GetString(1);
            var len = r.GetInt32(2);
            var display = type.ToLowerInvariant() switch
            {
                "nvarchar" or "nchar" => $"{type}({(len == -1 ? "max" : (len / 2).ToString())})",
                "varchar" or "char" or "varbinary" or "binary" => $"{type}({(len == -1 ? "max" : len.ToString())})",
                "decimal" or "numeric" => $"{type}({r.GetInt32(3)},{r.GetInt32(4)})",
                _ => type,
            };
            list.Add(new DestinationColumn(r.GetString(0), display, r.GetBoolean(5), r.GetBoolean(6), r.GetBoolean(7)));
        }
        return list.Count == 0 ? null : list;
    }

    /// <summary>Returns the stored modifiedon watermark (UTC) or null.</summary>
    public async Task<DateTime?> GetWatermarkAsync(CancellationToken ct) =>
        (await GetStateAsync(ct).ConfigureAwait(false))?.WatermarkUtc;

    /// <summary>Returns the stored sync-state row for this table, or null. Call after <see cref="EnsureTableAsync"/>.</summary>
    public async Task<SyncState?> GetStateAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT WatermarkUtc, EntityName, SourceUrl, ColumnSet FROM {StateTableName} WHERE TableName = @t;";
        cmd.Parameters.Add("@t", SqlDbType.NVarChar, 256).Value = FullTableName;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;
        DateTime? wm = reader.IsDBNull(0) ? null : DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc);
        string? Str(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        return new SyncState(wm, Str(1), Str(2), Str(3));
    }

    /// <summary>Upserts the sync-state row for this table.</summary>
    public Task SetWatermarkAsync(DateTime watermarkUtc, long rows, Guid correlationId, CancellationToken ct) =>
        SetStateAsync(watermarkUtc, rows, correlationId, null, null, null, ct);

    /// <summary>Upserts the sync-state row including source entity, org URL and synced column set (null keeps the stored value).</summary>
    public async Task SetStateAsync(DateTime watermarkUtc, long rows, Guid correlationId,
        string? entityName, string? sourceUrl, string? columnSet, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"MERGE {StateTableName} WITH (HOLDLOCK) AS t USING (SELECT @t AS TableName) AS s ON t.TableName = s.TableName " +
            "WHEN MATCHED THEN UPDATE SET WatermarkUtc = @w, LastRunUtc = SYSUTCDATETIME(), LastRows = @r, LastCorrelationId = @c, " +
            "EntityName = COALESCE(@e, t.EntityName), SourceUrl = COALESCE(@u, t.SourceUrl), ColumnSet = COALESCE(@cs, t.ColumnSet) " +
            "WHEN NOT MATCHED THEN INSERT (TableName, WatermarkUtc, LastRunUtc, LastRows, LastCorrelationId, EntityName, SourceUrl, ColumnSet) " +
            "VALUES (@t, @w, SYSUTCDATETIME(), @r, @c, @e, @u, @cs);";
        cmd.Parameters.Add("@t", SqlDbType.NVarChar, 256).Value = FullTableName;
        cmd.Parameters.Add("@w", SqlDbType.DateTime2).Value = DateTime.SpecifyKind(watermarkUtc, DateTimeKind.Utc);
        cmd.Parameters["@w"].Scale = 3;
        cmd.Parameters.Add("@r", SqlDbType.BigInt).Value = rows;
        cmd.Parameters.Add("@c", SqlDbType.UniqueIdentifier).Value = correlationId;
        cmd.Parameters.Add("@e", SqlDbType.NVarChar, 256).Value = (object?)entityName ?? DBNull.Value;
        cmd.Parameters.Add("@u", SqlDbType.NVarChar, 512).Value = (object?)sourceUrl ?? DBNull.Value;
        cmd.Parameters.Add("@cs", SqlDbType.NVarChar, -1).Value = (object?)columnSet ?? DBNull.Value;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // "nvarchar(100)" -> ("nvarchar", 100); "nvarchar(max)" -> ("nvarchar", -1); "int" / "decimal(38,4)" -> (name, null).
    private static (string BaseType, int? Length) ParseSqlType(string sqlType)
    {
        var open = sqlType.IndexOf('(');
        if (open < 0) return (sqlType.Trim(), null);
        var name = sqlType[..open].Trim();
        if (!name.Equals("nvarchar", StringComparison.OrdinalIgnoreCase) && !name.Equals("varbinary", StringComparison.OrdinalIgnoreCase))
            return (name, null);
        var arg = sqlType[(open + 1)..].TrimEnd(')', ' ');
        return (name, arg.Equals("max", StringComparison.OrdinalIgnoreCase)
            ? -1
            : int.Parse(arg, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Opens a writer with its own connection and #stage temp table.</summary>
    public async Task<SqlUpsertWriter> OpenWriterAsync(IReadOnlyList<ColumnDef> cols, SqlWriteOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cols);
        var columns = Distinct(cols);
        var pk = columns.FirstOrDefault(c => c.IsPrimaryKey)
                 ?? throw new InvalidOperationException("Column list has no primary key column.");
        var conn = await OpenAsync(ct).ConfigureAwait(false);
        try
        {
            await using (var cmd = conn.CreateCommand())
            {
                // Clustered unique key on the PK: de-duplicates the page (IGNORE_DUP_KEY) and makes every upsert visit target
                // rows in key order, so all workers acquire locks in the same order. A unique index (not a PRIMARY KEY)
                // also works when a hand-made table's key column is nullable.
                cmd.CommandText =
                    $"SELECT TOP 0 {string.Join(", ", columns.Select(c => Q(c.ColumnName)))} INTO #stage FROM {FullTableName};" +
                    $"CREATE UNIQUE CLUSTERED INDEX [CX_stage] ON #stage ({Q(pk.ColumnName)}) WITH (IGNORE_DUP_KEY = ON);";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            return new SqlUpsertWriter(conn, FullTableName, columns, options ?? new SqlWriteOptions());
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Deletes target rows whose primary key is not in <paramref name="allIds"/>. Returns rows deleted.</summary>
    public async Task<long> DeleteMissingAsync(IAsyncEnumerable<IReadOnlyList<Guid>> allIds, string primaryKeyColumn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(allIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryKeyColumn);

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using (var cmd = conn.CreateCommand())
        {
            // IGNORE_DUP_KEY guards against an id appearing on two source pages.
            cmd.CommandText = "CREATE TABLE #ids (id uniqueidentifier NOT NULL PRIMARY KEY WITH (IGNORE_DUP_KEY = ON));";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        long sourceCount = 0;
        using var table = new DataTable();
        table.Columns.Add("id", typeof(Guid));
        using (var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.TableLock, null)
               { DestinationTableName = "#ids", BatchSize = 0, BulkCopyTimeout = 0 })
        {
            bulk.ColumnMappings.Add("id", "id");
            await foreach (var batch in allIds.WithCancellation(ct).ConfigureAwait(false))
            {
                if (batch.Count == 0) continue;
                table.Clear();
                foreach (var id in batch) table.Rows.Add(id);
                await bulk.WriteToServerAsync(table, ct).ConfigureAwait(false);
                sourceCount += batch.Count;
            }
        }

        if (sourceCount == 0)
        {
            // Safety: an empty id set would wipe the table; treat as suspicious and skip.
            AppLog.Info($"Delete detection skipped for {FullTableName}: source returned no ids.");
            return 0;
        }

        // Safety: a partial id read (e.g. a principal with BU/user-scoped read) must not delete rows that incremental runs never re-copy.
        long targetCount, toDelete;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandTimeout = 0;
            cmd.CommandText =
                "SELECT COUNT_BIG(*), ISNULL(SUM(CASE WHEN i.id IS NULL THEN CAST(1 AS bigint) ELSE 0 END), 0) " +
                $"FROM {FullTableName} AS t LEFT JOIN #ids AS i ON i.id = t.{Q(primaryKeyColumn)};";
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            await reader.ReadAsync(ct).ConfigureAwait(false);
            targetCount = reader.GetInt64(0);
            toDelete = reader.GetInt64(1);
        }
        if (toDelete > MaxDeleteRowsFloor && toDelete > targetCount * MaxDeleteFraction)
            throw new SyncConfigurationException(
                $"Delete detection aborted for {FullTableName}: it would remove {toDelete} of {targetCount} rows " +
                $"(source returned {sourceCount} ids). The connected principal may not be able to read every row; " +
                "run with organization-wide read access, or remove the rows manually.");

        long deleted = 0;
        var pk = Q(primaryKeyColumn);
        while (true)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText =
                $"DELETE TOP (50000) t FROM {FullTableName} AS t WHERE NOT EXISTS (SELECT 1 FROM #ids AS i WHERE i.id = t.{pk});";
            var n = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (n <= 0) break;
            deleted += n;
        }
        return deleted;
    }

    private static List<ColumnDef> Distinct(IReadOnlyList<ColumnDef> cols)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ColumnDef>(cols.Count);
        foreach (var c in cols)
            if (seen.Add(c.ColumnName)) list.Add(c);
        return list;
    }
}

/// <summary>A column of an existing destination table.</summary>
public sealed record DestinationColumn(string Name, string TypeDisplay, bool IsNullable, bool HasDefault, bool IsReadOnly = false)
{
    /// <summary>True when an INSERT that leaves this column out would fail.</summary>
    public bool RequiresValue => !IsNullable && !HasDefault && !IsReadOnly;
}

/// <summary>Stored sync state of a target table.</summary>
public sealed record SyncState(DateTime? WatermarkUtc, string? EntityName, string? SourceUrl, string? ColumnSet);

/// <summary>Write tuning for <see cref="SqlUpsertWriter"/>.</summary>
public sealed record SqlWriteOptions
{
    /// <summary>Upsert statement shape.</summary>
    public UpsertMode Mode { get; init; } = UpsertMode.UpdateInsert;
    /// <summary>Rows per upsert transaction; 0 or less = whole page.</summary>
    public int BatchSize { get; init; } = 1000;
    /// <summary>Retries per sub-batch before the serialized last-resort attempt.</summary>
    public int DeadlockMaxRetries { get; init; } = 8;
    /// <summary>Base delay of the exponential back-off (ms).</summary>
    public int DeadlockRetryBaseDelayMs { get; init; } = 250;
    /// <summary>Gate shared by all writers of one run; null = no concurrency cap and no serialized fallback.</summary>
    public SqlWriteGate? Gate { get; init; }

    /// <summary>Builds options from settings (the gate is attached by the caller).</summary>
    public static SqlWriteOptions From(AppSettings s) => new()
    {
        Mode = s.UpsertMode,
        BatchSize = s.SqlWriteBatchSize,
        DeadlockMaxRetries = Math.Max(0, s.DeadlockMaxRetries),
        DeadlockRetryBaseDelayMs = Math.Max(1, s.DeadlockRetryBaseDelayMs),
    };
}

/// <summary>
/// Caps concurrent SQL writers (shared slots) and offers an exclusive mode that waits until no other writer of the run
/// is writing. Exclusive = take every slot, so it cannot deadlock with the app's own workers.
/// </summary>
public sealed class SqlWriteGate : IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _exclusive = new(1, 1);
    private readonly int _count;

    /// <summary>Creates a gate allowing <paramref name="concurrency"/> concurrent writers.</summary>
    public SqlWriteGate(int concurrency)
    {
        _count = Math.Max(1, concurrency);
        _slots = new SemaphoreSlim(_count, _count);
    }

    /// <summary>Number of concurrent writers allowed.</summary>
    public int Concurrency => _count;

    /// <summary>Takes one shared slot.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(() => _slots.Release());
    }

    /// <summary>Takes every slot: no other writer of this run writes until released. The caller must not hold a slot.</summary>
    public async Task<IDisposable> EnterExclusiveAsync(CancellationToken ct)
    {
        await _exclusive.WaitAsync(ct).ConfigureAwait(false);
        var taken = 0;
        try
        {
            for (; taken < _count; taken++)
                await _slots.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            if (taken > 0) _slots.Release(taken);
            _exclusive.Release();
            throw;
        }
        return new Releaser(() =>
        {
            _slots.Release(_count);
            _exclusive.Release();
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _slots.Dispose();
        _exclusive.Dispose();
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

/// <summary>
/// Bulk-loads rows into a session temp table and upserts them into the target in sub-batches.
/// Not thread-safe; one per worker.
/// </summary>
public sealed class SqlUpsertWriter : IAsyncDisposable
{
    private const int CommandTimeoutSeconds = 600;
    private const int MaxBackoffMs = 30_000;
    private readonly SqlConnection _conn;
    private readonly IReadOnlyList<ColumnDef> _cols;
    private readonly SqlWriteOptions _options;
    private readonly string _upsertSql;

    internal SqlUpsertWriter(SqlConnection conn, string fullTableName, IReadOnlyList<ColumnDef> cols, SqlWriteOptions options)
    {
        _conn = conn;
        _cols = cols;
        _options = options;
        _upsertSql = options.Mode == UpsertMode.Merge ? BuildMerge(fullTableName, cols) : BuildUpdateInsert(fullTableName, cols);
    }

    /// <summary>
    /// Upserts the rows in sub-batches of <see cref="SqlWriteOptions.BatchSize"/>. Each sub-batch is retried on deadlock (1205)
    /// or a concurrent-insert key violation (2627/2601); when retries run out it is run once more under the exclusive gate.
    /// </summary>
    public async Task UpsertAsync(DataTable rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var total = rows.Rows.Count;
        if (total == 0) return;

        var batchSize = _options.BatchSize <= 0 ? total : _options.BatchSize;
        for (var offset = 0; offset < total; offset += batchSize)
        {
            var count = Math.Min(batchSize, total - offset);
            var batch = new DataRow[count];
            for (var i = 0; i < count; i++)
                batch[i] = rows.Rows[offset + i];

            await StageAsync(rows, batch, ct).ConfigureAwait(false);
            await UpsertStagedAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task StageAsync(DataTable table, DataRow[] batch, CancellationToken ct)
    {
        await using (var trunc = _conn.CreateCommand())
        {
            trunc.CommandText = "TRUNCATE TABLE #stage;";
            await trunc.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        using var bulk = new SqlBulkCopy(_conn, SqlBulkCopyOptions.TableLock, null)
            { DestinationTableName = "#stage", BatchSize = 0, BulkCopyTimeout = CommandTimeoutSeconds };
        foreach (var c in _cols)
            if (table.Columns.Contains(c.ColumnName))
                bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
        await bulk.WriteToServerAsync(batch, ct).ConfigureAwait(false);
    }

    private async Task UpsertStagedAsync(CancellationToken ct)
    {
        var gate = _options.Gate;
        var maxRetries = _options.DeadlockMaxRetries;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using (gate is null ? null : await gate.EnterAsync(ct).ConfigureAwait(false))
                    await ExecuteUpsertAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (SqlException ex) when (IsRetryable(ex) && attempt <= maxRetries)
            {
                await RollbackIfOpenAsync().ConfigureAwait(false);
                var backoff = Math.Min(MaxBackoffMs, _options.DeadlockRetryBaseDelayMs * Math.Pow(2, attempt - 1));
                var delay = TimeSpan.FromMilliseconds(backoff + Random.Shared.Next(0, _options.DeadlockRetryBaseDelayMs));
                AppLog.Info($"Upsert {Describe(ex)} (attempt {attempt}/{maxRetries}), retrying in {delay.TotalMilliseconds:F0} ms.");
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (SqlException ex) when (IsRetryable(ex) && gate is not null)
            {
                await RollbackIfOpenAsync().ConfigureAwait(false);
                AppLog.Info($"Upsert {Describe(ex)}: {maxRetries} retries used; running this batch serialized (no other writer active).");
                break;
            }
        }

        // Last resort: no other worker of this run writes meanwhile, so this cannot deadlock with them.
        using (await gate!.EnterExclusiveAsync(ct).ConfigureAwait(false))
            await ExecuteUpsertAsync(ct).ConfigureAwait(false);
    }

    private async Task ExecuteUpsertAsync(CancellationToken ct)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandTimeout = CommandTimeoutSeconds;
        cmd.CommandText = _upsertSql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    // A deadlock victim's transaction is already rolled back; this covers any other abort that left one open.
    private async Task RollbackIfOpenAsync()
    {
        try
        {
            await using var cmd = _conn.CreateCommand();
            cmd.CommandText = "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;";
            await cmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SqlException)
        {
            // best effort; the next attempt surfaces a real connection problem
        }
    }

    /// <summary>Closes the writer's connection (drops #stage).</summary>
    public ValueTask DisposeAsync() => _conn.DisposeAsync();

    private static bool IsRetryable(SqlException ex) => HasError(ex, 1205) || HasError(ex, 2627) || HasError(ex, 2601);

    private static string Describe(SqlException ex) => HasError(ex, 1205) ? "deadlock" : "concurrent insert (key violation)";

    private static bool HasError(SqlException ex, int number)
    {
        if (ex.Number == number) return true;
        foreach (SqlError e in ex.Errors)
            if (e.Number == number) return true;
        return false;
    }

    // READ COMMITTED, no range locks. #stage is clustered on the PK, so both statements touch target rows in key order.
    // A row inserted by another worker between UPDATE and INSERT raises 2627/2601; the retry then takes the UPDATE path.
    private static string BuildUpdateInsert(string fullTableName, IReadOnlyList<ColumnDef> cols)
    {
        var pk = cols.FirstOrDefault(c => c.IsPrimaryKey)
                 ?? throw new InvalidOperationException("Column list has no primary key column.");
        var pkq = SqlTarget.Q(pk.ColumnName);
        var colList = string.Join(", ", cols.Select(c => SqlTarget.Q(c.ColumnName)));
        var sourceCols = string.Join(", ", cols.Select(c => "s." + SqlTarget.Q(c.ColumnName)));
        var updates = cols.Where(c => !c.IsPrimaryKey)
                          .Select(c => $"t.{SqlTarget.Q(c.ColumnName)} = s.{SqlTarget.Q(c.ColumnName)}")
                          .Append("t.[__syncedon] = SYSUTCDATETIME()");

        return
            "SET XACT_ABORT ON; SET TRANSACTION ISOLATION LEVEL READ COMMITTED; " +
            "BEGIN TRANSACTION; " +
            $"UPDATE t SET {string.Join(", ", updates)} " +
            $"FROM {fullTableName} AS t WITH (ROWLOCK) INNER JOIN #stage AS s ON t.{pkq} = s.{pkq}; " +
            $"INSERT INTO {fullTableName} WITH (ROWLOCK) ({colList}) SELECT {sourceCols} FROM #stage AS s " +
            $"WHERE NOT EXISTS (SELECT 1 FROM {fullTableName} AS t WITH (UPDLOCK, ROWLOCK) WHERE t.{pkq} = s.{pkq}); " +
            "COMMIT TRANSACTION;";
    }

    private static string BuildMerge(string fullTableName, IReadOnlyList<ColumnDef> cols)
    {
        var pk = cols.FirstOrDefault(c => c.IsPrimaryKey)
                 ?? throw new InvalidOperationException("Column list has no primary key column.");
        var pkq = SqlTarget.Q(pk.ColumnName);
        var all = cols.Select(c => SqlTarget.Q(c.ColumnName)).ToList();
        var updates = cols.Where(c => !c.IsPrimaryKey)
                          .Select(c => $"t.{SqlTarget.Q(c.ColumnName)} = s.{SqlTarget.Q(c.ColumnName)}")
                          .Append("t.[__syncedon] = SYSUTCDATETIME()");
        var colList = string.Join(", ", all);

        // #stage is unique on the PK (IGNORE_DUP_KEY), so MERGE never hits the same target row twice.
        return
            $"MERGE {fullTableName} WITH (HOLDLOCK) AS t " +
            $"USING (SELECT {colList} FROM #stage) AS s " +
            $"ON t.{pkq} = s.{pkq} " +
            $"WHEN MATCHED THEN UPDATE SET {string.Join(", ", updates)} " +
            $"WHEN NOT MATCHED THEN INSERT ({colList}) VALUES ({string.Join(", ", all.Select(c => "s." + c))});";
    }
}
