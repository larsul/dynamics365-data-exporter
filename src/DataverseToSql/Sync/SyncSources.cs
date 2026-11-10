using System.Data;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataverseToSql.Sync;

/// <summary>Reads one modifiedon time slice page by page. One per worker; not thread-safe.</summary>
public interface ISliceReader : IAsyncDisposable
{
    /// <summary>Reads rows with modifiedon in [from, to), continuing from the slice's checkpoint.</summary>
    IAsyncEnumerable<DataverseSlicePage> ReadSliceAsync(SliceWork slice, int pageSize, CancellationToken ct);
}

/// <summary>Where a sync reads its rows: Dataverse (SDK or TDS) or a SQL Server table.</summary>
public interface ISyncSource : IDisposable
{
    /// <summary>Stored in the sync state and checkpoints to detect a target being reused for another source.</summary>
    string SourceKey { get; }

    /// <summary>Human-readable description for the log.</summary>
    string Description { get; }

    /// <summary>Upper bound for the number of parallel readers.</summary>
    int RecommendedParallelism { get; }

    /// <summary>Table columns as Dataverse-style metadata, so selection and mapping work the same for every source.</summary>
    Task<EntityMetadata> GetMetadataAsync(CancellationToken ct);

    /// <summary>Smallest modifiedon (UTC), or null when the table is empty.</summary>
    Task<DateTime?> GetMinModifiedOnAsync(CancellationToken ct);

    /// <summary>Largest modifiedon (UTC), or null when the table is empty.</summary>
    Task<DateTime?> GetMaxModifiedOnAsync(CancellationToken ct);

    /// <summary>Approximate number of rows in the source table (for progress display), or null when unknown.</summary>
    Task<long?> EstimateRowCountAsync(CancellationToken ct);

    /// <summary>Every primary id, in batches (delete detection).</summary>
    IAsyncEnumerable<IReadOnlyList<Guid>> ReadAllIdsAsync(string primaryId, CancellationToken ct);

    /// <summary>
    /// Creates <paramref name="count"/> readers (one per worker). Fails fast on configuration problems.
    /// <paramref name="filterOnModifiedOn"/> false: the table has no modifiedon column and readers ignore the slice window.
    /// </summary>
    Task<IReadOnlyList<ISliceReader>> CreateReadersAsync(int count, IReadOnlyList<ColumnDef> cols, string primaryId, bool filterOnModifiedOn, CancellationToken ct);
}

/// <summary>Dataverse source, read through the SDK or the TDS endpoint.</summary>
public sealed class DataverseSyncSource : ISyncSource
{
    private readonly DataverseSource _source;
    private readonly AppSettings _s;

    /// <summary>Wraps a connected <see cref="DataverseSource"/>.</summary>
    public DataverseSyncSource(DataverseSource source, AppSettings s)
    {
        _source = source;
        _s = s;
    }

    /// <inheritdoc />
    public string SourceKey => _s.DataverseUrl.Trim().TrimEnd('/');

    /// <inheritdoc />
    public string Description => _s.Source == SourceKind.Tds ? "Dataverse TDS endpoint" : "Dataverse SDK";

    /// <inheritdoc />
    public int RecommendedParallelism => _source.RecommendedParallelism;

    /// <inheritdoc />
    public Task<EntityMetadata> GetMetadataAsync(CancellationToken ct) => _source.GetMetadataAsync(_s.EntityName, ct);

    /// <inheritdoc />
    public Task<DateTime?> GetMinModifiedOnAsync(CancellationToken ct) => _source.GetMinModifiedOnAsync(_s.EntityName, ct);

    /// <inheritdoc />
    public Task<DateTime?> GetMaxModifiedOnAsync(CancellationToken ct) => _source.GetMaxModifiedOnAsync(_s.EntityName, ct);

    /// <inheritdoc />
    public async Task<long?> EstimateRowCountAsync(CancellationToken ct)
    {
        // Snapshot count kept by Dataverse (may lag a little behind); fine for a progress bar.
        var resp = (Microsoft.Crm.Sdk.Messages.RetrieveTotalRecordCountResponse)await _source.Client.ExecuteAsync(
            new Microsoft.Crm.Sdk.Messages.RetrieveTotalRecordCountRequest { EntityNames = new[] { _s.EntityName } }, ct).ConfigureAwait(false);
        return resp.EntityRecordCountCollection.TryGetValue(_s.EntityName, out var n) ? n : null;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<IReadOnlyList<Guid>> ReadAllIdsAsync(string primaryId, CancellationToken ct) =>
        DataverseSource.ReadAllIdsAsync(_source.Client, _s.EntityName, primaryId, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ISliceReader>> CreateReadersAsync(int count, IReadOnlyList<ColumnDef> cols, string primaryId, bool filterOnModifiedOn, CancellationToken ct)
    {
        var readers = new List<ISliceReader>(count);
        if (_s.Source == SourceKind.Tds)
        {
            // Resolve columns on the endpoint before any work is queued (fails fast with a clear message).
            var open = TdsReader.ConnectionFactory(_s, _source.Client);
            var plan = await TdsReader.PrepareAsync(open, _s.EntityName, cols, ct).ConfigureAwait(false);
            for (var i = 0; i < count; i++)
                readers.Add(new TdsReader(open, _s.EntityName, primaryId, plan, filterOnModifiedOn: filterOnModifiedOn));
            return readers;
        }

        var attributes = ColumnMap.AttributeNames(cols);
        try
        {
            // Clone clients sequentially before starting workers.
            for (var i = 0; i < count; i++)
                readers.Add(new SdkSliceReader(_source.CloneClient(), _s.EntityName, primaryId, attributes, filterOnModifiedOn));
            return readers;
        }
        catch
        {
            foreach (var r in readers) await r.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _source.Dispose();

    private sealed class SdkSliceReader(ServiceClient client, string entityName, string primaryId, string[] attributes, bool filterOnModifiedOn) : ISliceReader
    {
        public IAsyncEnumerable<DataverseSlicePage> ReadSliceAsync(SliceWork slice, int pageSize, CancellationToken ct) =>
            DataverseSource.ReadSliceAsync(client, entityName, primaryId, attributes,
                filterOnModifiedOn ? slice.FromUtc : null, filterOnModifiedOn ? slice.ToUtc : null,
                pageSize, ct, slice.NextPage, slice.PagingCookie, slice.KeyFrom, slice.KeyTo);

        public ValueTask DisposeAsync()
        {
            try { client.Dispose(); } catch { /* best effort */ }
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// A SQL Server / Azure SQL table as source, e.g. a table this app filled from Dataverse. Needs a uniqueidentifier key
/// (primary key or unique index). A <c>modifiedon</c> datetime column enables time slicing and incremental runs; without it
/// every run copies the whole table, split into key ranges. Rows are read with keyset paging on the key.
/// </summary>
public sealed class SqlTableSource : ISyncSource
{
    private const string ModifiedOn = "modifiedon";
    private const string SyncedOnColumn = "__syncedon";
    private const int IdBatchSize = 50_000;
    private const int MaxParallelism = 64;

    private readonly AppSettings _s;
    private readonly string _connectionString;
    private readonly string _schema;
    private readonly string _table;
    private readonly string _from;

    private SqlTableSource(AppSettings s)
    {
        _s = s;
        _connectionString = s.BuildSourceSqlConnectionString();
        (_schema, _table) = SplitName(s.EntityName);
        _from = $"{SqlTarget.Q(_schema)}.{SqlTarget.Q(_table)}";
    }

    /// <summary>Validates the settings and checks that the database opens.</summary>
    public static async Task<SqlTableSource> ConnectAsync(AppSettings s, bool nonInteractive, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (string.IsNullOrWhiteSpace(s.SourceSqlConnectionString) || string.IsNullOrWhiteSpace(s.SourceDatabaseName))
            throw new SyncConfigurationException("SQL source: enter the server / connection string and the database.");
        if (string.IsNullOrWhiteSpace(s.EntityName))
            throw new SyncConfigurationException("SQL source: enter the source table as schema.table, e.g. dbo.annotation.");
        if (IsSameAsDestination(s))
            throw new SyncConfigurationException(
                $"The source table {s.EntityName} is also the destination table. Choose another destination table in step 3.");
        if (nonInteractive && s.SourceSqlAuth == SqlAuthKind.EntraInteractive)
            throw new SyncConfigurationException(
                "Scheduled (headless) runs cannot show the Microsoft Entra sign-in window for the SQL source. " +
                "Use 'Microsoft Entra default' (managed identity / az login) or a SQL login.");

        var source = new SqlTableSource(s);
        await using var conn = await source.OpenAsync(ct).ConfigureAwait(false);
        return source;
    }

    /// <inheritdoc />
    public string SourceKey
    {
        get
        {
            var b = new SqlConnectionStringBuilder(_connectionString);
            return $"mssql://{b.DataSource.Trim().ToLowerInvariant()}/{b.InitialCatalog.Trim().ToLowerInvariant()}";
        }
    }

    /// <inheritdoc />
    public string Description => $"SQL table [{new SqlConnectionStringBuilder(_connectionString).InitialCatalog}].{_from}";

    /// <inheritdoc />
    public int RecommendedParallelism => MaxParallelism;

    /// <summary>
    /// True when the SQL source table of <paramref name="s"/> is the destination table itself (same server, database, schema
    /// and table). Syncing a table onto itself would alter its schema and rewrite every row, so it is refused.
    /// </summary>
    public static bool IsSameAsDestination(AppSettings s)
    {
        if (s.Source != SourceKind.SqlTable) return false;
        static string Server(string cs)
        {
            try { return new SqlConnectionStringBuilder(cs).DataSource.Trim().Replace("tcp:", "", StringComparison.OrdinalIgnoreCase).Split(',')[0].ToLowerInvariant(); }
            catch (ArgumentException) { return cs.Trim().ToLowerInvariant(); }
        }
        var (schema, table) = SplitName(s.EntityName);
        var destSchema = string.IsNullOrWhiteSpace(s.SchemaName) ? "dbo" : s.SchemaName.Trim();
        return Server(s.SourceSqlConnectionString) == Server(s.SqlConnectionString)
               && s.SourceDatabaseName.Trim().Equals(s.DatabaseName.Trim(), StringComparison.OrdinalIgnoreCase)
               && schema.Equals(destSchema, StringComparison.OrdinalIgnoreCase)
               && table.Equals(s.TableName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Splits "schema.table" (or "table", schema dbo); brackets are optional.</summary>
    public static (string Schema, string Table) SplitName(string name)
    {
        var n = (name ?? "").Trim();
        var dot = n.IndexOf("].[", StringComparison.Ordinal) is var bi and >= 0 ? bi + 1 : n.IndexOf('.');
        var schema = dot > 0 ? n[..dot] : "dbo";
        var table = dot > 0 ? n[(dot + 1)..] : n;
        static string Unq(string x) => x.Trim().TrimStart('[').TrimEnd(']').Replace("]]", "]");
        return (Unq(schema), Unq(table));
    }

    /// <inheritdoc />
    public async Task<EntityMetadata> GetMetadataAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        var columns = new List<(string Name, string Type, int MaxLength, int Precision, int Scale, bool Computed)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT c.name, TYPE_NAME(c.system_type_id), CAST(c.max_length AS int), CAST(c.precision AS int), CAST(c.scale AS int), c.is_computed " +
                "FROM sys.columns c WHERE c.object_id = OBJECT_ID(@t, N'U') ORDER BY c.column_id;";
            cmd.Parameters.Add("@t", SqlDbType.NVarChar, 600).Value = _from;
            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
                columns.Add((r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetBoolean(5)));
        }
        if (columns.Count == 0)
            throw new SyncConfigurationException($"SQL source table {_from} was not found in database [{conn.Database}].");

        string? key;
        await using (var cmd = conn.CreateCommand())
        {
            // Single-column primary key (preferred) or unique index on a uniqueidentifier column.
            cmd.CommandText =
                "SELECT TOP 1 c.name FROM sys.indexes i " +
                "JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1 " +
                "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
                "WHERE i.object_id = OBJECT_ID(@t, N'U') AND (i.is_primary_key = 1 OR i.is_unique = 1) " +
                "AND c.system_type_id = TYPE_ID('uniqueidentifier') " +
                "AND (SELECT COUNT(*) FROM sys.index_columns x WHERE x.object_id = i.object_id AND x.index_id = i.index_id AND x.key_ordinal > 0) = 1 " +
                "ORDER BY i.is_primary_key DESC, i.index_id;";
            cmd.Parameters.Add("@t", SqlDbType.NVarChar, 600).Value = _from;
            key = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }
        if (key is null)
            throw new SyncConfigurationException(
                $"SQL source table {_from} needs a uniqueidentifier primary key (or a unique index on one uniqueidentifier column).");

        // modifiedon is optional (without it every run is a full copy), but when present it must be a date/time column.
        var modified = columns.FirstOrDefault(c => c.Name.Equals(ModifiedOn, StringComparison.OrdinalIgnoreCase));
        if (modified.Name is not null && !modified.Type.StartsWith("date", StringComparison.OrdinalIgnoreCase) && modified.Type != "smalldatetime")
            throw new SyncConfigurationException(
                $"Column '{ModifiedOn}' of SQL source table {_from} is {modified.Type}; it must be a date/time column (rows are sliced and synced incrementally on it).");

        var attributes = new List<AttributeMetadata>();
        foreach (var c in columns)
        {
            if (c.Computed || c.Name.Equals(SyncedOnColumn, StringComparison.OrdinalIgnoreCase))
                continue;
            var a = ToAttribute(c.Name, c.Type, c.MaxLength);
            if (a is not null)
                a.ExternalName = ColumnMap.SqlTypePrefix + SqlTypeDisplay(c.Type, c.MaxLength, c.Precision, c.Scale);
            if (a is null)
            {
                AppLog.Info($"SQL source: column [{c.Name}] ({c.Type}) is not supported and is skipped.");
                continue;
            }
            attributes.Add(a);
        }

        var meta = new EntityMetadata { LogicalName = _s.EntityName.Trim() };
        SetHidden(meta, nameof(EntityMetadata.PrimaryIdAttribute), columns.First(c => c.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).Name);
        SetHidden(meta, nameof(EntityMetadata.Attributes), attributes.ToArray());
        return meta;
    }

    /// <inheritdoc />
    public Task<DateTime?> GetMinModifiedOnAsync(CancellationToken ct) => ScalarDateAsync("MIN", ct);

    /// <inheritdoc />
    public Task<DateTime?> GetMaxModifiedOnAsync(CancellationToken ct) => ScalarDateAsync("MAX", ct);

    /// <inheritdoc />
    public async Task<long?> EstimateRowCountAsync(CancellationToken ct)
    {
        // Partition metadata: instant on any table size.
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = OBJECT_ID(@t, N'U') AND p.index_id IN (0, 1);";
        cmd.Parameters.Add("@t", SqlDbType.NVarChar, 600).Value = _from;
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is long n ? n : null;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<IReadOnlyList<Guid>> ReadAllIdsAsync(string primaryId, [EnumeratorCancellation] CancellationToken ct)
    {
        var pk = SqlTarget.Q(primaryId);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        Guid? last = null;
        while (true)
        {
            var batch = new List<Guid>(IdBatchSize);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandTimeout = 600;
                cmd.CommandText = $"SELECT TOP ({IdBatchSize}) {pk} FROM {_from} WHERE {pk} IS NOT NULL" +
                                  (last is null ? "" : $" AND {pk} > @k") + $" ORDER BY {pk};";
                if (last is { } k)
                    cmd.Parameters.Add("@k", SqlDbType.UniqueIdentifier).Value = k;
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                    batch.Add(r.GetGuid(0));
            }
            if (batch.Count == 0)
                yield break;
            last = batch[^1];
            yield return batch;
            if (batch.Count < IdBatchSize)
                yield break;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ISliceReader>> CreateReadersAsync(int count, IReadOnlyList<ColumnDef> cols, string primaryId, bool filterOnModifiedOn, CancellationToken ct)
    {
        var plan = await TdsReader.PrepareAsync(OpenAsync, _s.EntityName, cols, ct, _from).ConfigureAwait(false);
        var readers = new List<ISliceReader>(count);
        for (var i = 0; i < count; i++)
            readers.Add(new TdsReader(OpenAsync, _s.EntityName, primaryId, plan, _from, filterOnModifiedOn));
        return readers;
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(_connectionString);
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

    private async Task<DateTime?> ScalarDateAsync(string aggregate, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 600;
        cmd.CommandText = $"SELECT {aggregate}({SqlTarget.Q(ModifiedOn)}) FROM {_from};";
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) switch
        {
            DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
            DateTimeOffset dto => dto.UtcDateTime,
            _ => null,
        };
    }

    // "nvarchar(200)", "varbinary(max)", "decimal(38,4)" ... for the mapping grid.
    private static string SqlTypeDisplay(string type, int maxLength, int precision, int scale) => type.ToLowerInvariant() switch
    {
        "nvarchar" or "nchar" => $"{type}({(maxLength == -1 ? "max" : (maxLength / 2).ToString())})",
        "varchar" or "char" or "varbinary" or "binary" => $"{type}({(maxLength == -1 ? "max" : maxLength.ToString())})",
        "decimal" or "numeric" => $"{type}({precision},{scale})",
        _ => type,
    };

    // SQL type -> the Dataverse attribute type that ColumnMap maps back to an equivalent SQL column.
    private static AttributeMetadata? ToAttribute(string name, string type, int maxLength)
    {
        AttributeMetadata? a = type.ToLowerInvariant() switch
        {
            "uniqueidentifier" => new UniqueIdentifierAttributeMetadata(),
            "nvarchar" or "nchar" when maxLength > 0 => new StringAttributeMetadata { MaxLength = maxLength / 2 },
            "varchar" or "char" when maxLength is > 0 and <= 4000 => new StringAttributeMetadata { MaxLength = maxLength },
            "nvarchar" or "varchar" or "nchar" or "char" or "ntext" or "text" or "xml" or "time" => new MemoAttributeMetadata(),
            // Binary values are written as base64 text (documentbody is always kept as varbinary(max), see ColumnMap).
            "varbinary" or "binary" or "image" => new MemoAttributeMetadata(),
            "int" or "smallint" or "tinyint" => new IntegerAttributeMetadata(),
            "bigint" => new BigIntAttributeMetadata(),
            "bit" => new BooleanAttributeMetadata(),
            "decimal" or "numeric" => new DecimalAttributeMetadata(),
            "money" or "smallmoney" => new MoneyAttributeMetadata(),
            "float" or "real" => new DoubleAttributeMetadata(),
            "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => new DateTimeAttributeMetadata(),
            _ => null, // timestamp/rowversion, sql_variant, hierarchyid, geography, geometry
        };
        if (a is null)
            return null;
        a.LogicalName = name;
        SetHidden(a, nameof(AttributeMetadata.IsValidForRead), (bool?)true);
        return a;
    }

    // The SDK metadata classes keep these setters internal; they are only filled here, for a synthetic SQL table description.
    private static void SetHidden(object target, string property, object? value)
    {
        var p = target.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Property {property} not found on {target.GetType().Name}.");
        var setter = p.GetSetMethod(nonPublic: true);
        if (setter is not null)
        {
            setter.Invoke(target, new[] { value });
            return;
        }
        for (var t = target.GetType(); t is not null; t = t.BaseType)
        {
            var f = t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .FirstOrDefault(x => x.Name.Equals("_" + property, StringComparison.OrdinalIgnoreCase)
                                     || x.Name == $"<{property}>k__BackingField");
            if (f is not null)
            {
                f.SetValue(target, value);
                return;
            }
        }
        throw new InvalidOperationException($"Cannot set {property} on {target.GetType().Name}.");
    }
}
