using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;

namespace DataverseToSql.Sync;

/// <summary>Columns to select from the TDS endpoint and how to turn them back into attribute values.</summary>
public sealed record TdsQueryPlan(
    IReadOnlyList<string> Attributes,
    IReadOnlySet<string> LookupAttributes,
    IReadOnlyDictionary<string, string> LookupTypeColumns);

/// <summary>
/// Reads Dataverse rows through the TDS (SQL) endpoint on port 5558, authenticated with the ServiceClient's own access token
/// (STD-INT-001/002: same app user or signed-in user as the SDK path). Pages use keyset paging on the primary key, so the
/// "paging cookie" stored in the checkpoint is simply the last primary key read.
/// </summary>
public sealed class TdsReader : ISliceReader
{
    /// <summary>TDS endpoint port.</summary>
    public const int Port = 5558;

    private const int MaxQueryAttempts = 4;
    private const int CommandTimeoutSeconds = 300; // the TDS endpoint itself stops queries after 5 minutes

    private readonly Func<CancellationToken, Task<SqlConnection>> _open;
    private readonly string _entityName;
    private readonly string _primaryId;
    private readonly TdsQueryPlan _plan;
    private readonly string _selectList;
    private readonly string _from;
    private readonly bool _filterOnModifiedOn;
    private SqlConnection? _conn;

    private string Label => _from == SqlTarget.Q(_entityName) ? "TDS" : "SQL source";

    /// <summary>
    /// Creates a reader that opens its own connection on first use. <paramref name="fromSql"/> is the quoted table to read
    /// (default: the entity name on the TDS endpoint; a SQL table source passes "[schema].[table]").
    /// <paramref name="filterOnModifiedOn"/> false reads every row (table without modifiedon); the slice window is ignored.
    /// </summary>
    public TdsReader(Func<CancellationToken, Task<SqlConnection>> open, string entityName, string primaryId, TdsQueryPlan plan,
        string? fromSql = null, bool filterOnModifiedOn = true)
    {
        _filterOnModifiedOn = filterOnModifiedOn;
        _open = open;
        _entityName = entityName;
        _from = fromSql ?? SqlTarget.Q(entityName);
        _primaryId = primaryId;
        _plan = plan;
        var columns = plan.Attributes.Concat(plan.LookupTypeColumns.Values).Distinct(StringComparer.OrdinalIgnoreCase);
        _selectList = string.Join(", ", columns.Select(SqlTarget.Q));
    }

    /// <summary>Builds a connection factory for the environment's TDS endpoint, reusing the ServiceClient token.</summary>
    public static Func<CancellationToken, Task<SqlConnection>> ConnectionFactory(AppSettings s, ServiceClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var host = new Uri(s.DataverseUrl.Trim()).Host;
        var cs = new SqlConnectionStringBuilder
        {
            DataSource = $"{host},{Port}",
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            ConnectTimeout = 60,
            ApplicationName = "DataverseToSql",
        }.ConnectionString;

        return async ct =>
        {
            var conn = new SqlConnection(cs)
            {
                // Called whenever SqlClient needs a token; ServiceClient refreshes its token when it expires.
                AccessTokenCallback = (_, _) =>
                {
                    var token = client.CurrentAccessToken
                                ?? throw new SyncConfigurationException("The Dataverse connection has no access token; TDS needs OAuth or ClientSecret auth.");
                    return Task.FromResult(new SqlAuthenticationToken(token, DateTimeOffset.UtcNow.AddMinutes(5)));
                },
            };
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
        };
    }

    /// <summary>Checks that the TDS endpoint answers and the table is readable. Returns a message for the UI.</summary>
    public static async Task<string> TestConnectionAsync(AppSettings s, DataverseSource source, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        var open = ConnectionFactory(s, source.Client);
        try
        {
            await using var conn = await open(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 60;
            cmd.CommandText = $"SELECT TOP 1 1 FROM {SqlTarget.Q(s.EntityName)};";
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return $"TDS endpoint {new Uri(s.DataverseUrl).Host},{Port} is reachable and table '{s.EntityName}' is readable.";
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException(
                $"TDS endpoint test failed: {ex.Message} Check that the TDS endpoint is enabled (Power Platform admin center > " +
                "Environment > Settings > Product > Features > TDS endpoint) and that outbound TCP port 5558 is open.", ex);
        }
    }

    /// <summary>
    /// Resolves which selected attributes the endpoint exposes. Throws <see cref="SyncConfigurationException"/> when a selected
    /// attribute is missing (for example file or large text columns), so the user can deselect it or switch to the SDK source.
    /// </summary>
    public static async Task<TdsQueryPlan> PrepareAsync(Func<CancellationToken, Task<SqlConnection>> open, string entityName,
        IReadOnlyList<ColumnDef> cols, CancellationToken ct, string? fromSql = null)
    {
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var conn = await open(ct).ConfigureAwait(false))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandTimeout = 120;
            cmd.CommandText = $"SELECT TOP 0 * FROM {fromSql ?? SqlTarget.Q(entityName)};";
            await using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SchemaOnly, ct).ConfigureAwait(false);
            for (var i = 0; i < r.FieldCount; i++)
                available.Add(r.GetName(i));
        }

        var attributes = cols.Select(c => c.AttributeName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var missing = attributes.Where(a => !available.Contains(a)).ToList();
        if (missing.Count > 0)
            throw new SyncConfigurationException(
                (fromSql is null ? "The TDS endpoint does not" : "The SQL source table does not") + $" expose these selected attributes of '{entityName}': {string.Join(", ", missing)}. " +
                "Deselect them, or use the Dataverse SDK source.");

        var lookups = cols.Where(c => c.Kind == ColumnKind.LookupId).Select(c => c.AttributeName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var typeColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cols.Where(c => c.Kind == ColumnKind.LookupEntity))
        {
            var candidate = new[] { c.AttributeName + "type", c.AttributeName + "typecode", c.AttributeName + "logicalname" }
                .FirstOrDefault(available.Contains);
            if (candidate is null)
                AppLog.Info($"TDS: no table-type column found for lookup '{c.AttributeName}'; [{c.ColumnName}] stays empty.");
            else
                typeColumns[c.AttributeName] = available.First(a => a.Equals(candidate, StringComparison.OrdinalIgnoreCase));
        }

        AppLog.Info(fromSql is null ? $"TDS: {attributes.Count} attributes resolved on the endpoint." : $"SQL source: {attributes.Count} columns resolved.");
        return new TdsQueryPlan(attributes, lookups, typeColumns);
    }

    /// <summary>
    /// Reads rows with modifiedon in [from, to) and the primary key in [KeyFrom, KeyTo) when set, continuing after the
    /// checkpointed key when the slice was started before.
    /// </summary>
    public async IAsyncEnumerable<DataverseSlicePage> ReadSliceAsync(SliceWork slice, int pageSize,
        [EnumeratorCancellation] CancellationToken ct)
    {
        pageSize = Math.Clamp(pageSize, 1, 5000);
        var pageNumber = Math.Max(1, slice.NextPage);
        Guid? lastKey = !slice.IsFresh && Guid.TryParse(slice.PagingCookie, out var k) ? k : null;
        if (!slice.IsFresh && lastKey is null)
            AppLog.Info($"{Label}: slice {slice.FromUtc:O} has no usable checkpoint key; re-reading it from the start (MERGE makes this safe).");

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var rows = await FetchPageAsync(slice, lastKey, pageSize + 1, ct).ConfigureAwait(false);
            var more = rows.Count > pageSize;
            if (more)
                rows.RemoveAt(rows.Count - 1);
            if (rows.Count > 0)
                lastKey = rows[^1].Id;

            yield return new DataverseSlicePage(rows, more, pageNumber, more ? lastKey?.ToString() : null);
            if (!more)
                yield break;
            pageNumber++;
        }
    }

    private async Task<List<Entity>> FetchPageAsync(SliceWork slice, Guid? afterKey, int take, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _conn ??= await _open(ct).ConfigureAwait(false);
                return await QueryAsync(_conn, slice, afterKey, take, ct).ConfigureAwait(false);
            }
            catch (SqlException ex) when (attempt < MaxQueryAttempts && !ct.IsCancellationRequested)
            {
                // Transient endpoint errors (throttling, dropped connection, 5-minute limit): reconnect and retry the same page.
                var delay = TimeSpan.FromSeconds(5 * Math.Pow(3, attempt - 1));
                AppLog.Info($"{Label} query failed (error {ex.Number}: {ex.Message}); retry {attempt}/{MaxQueryAttempts - 1} in {delay.TotalSeconds:N0} s.");
                if (_conn is not null)
                {
                    await _conn.DisposeAsync().ConfigureAwait(false);
                    _conn = null;
                }
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<List<Entity>> QueryAsync(SqlConnection conn, SliceWork slice, Guid? afterKey, int take, CancellationToken ct)
    {
        var pk = SqlTarget.Q(_primaryId);
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = CommandTimeoutSeconds;
        var conditions = new List<string>(4);
        if (_filterOnModifiedOn)
        {
            conditions.Add("[modifiedon] >= @f AND [modifiedon] < @t");
            AddDate(cmd, "@f", slice.FromUtc);
            AddDate(cmd, "@t", slice.ToUtc);
        }
        // Primary-key range (tables without modifiedon): compared by the server, so its uniqueidentifier order applies.
        if (slice.KeyFrom is { } kf)
        {
            conditions.Add($"{pk} >= @kf");
            cmd.Parameters.Add("@kf", SqlDbType.UniqueIdentifier).Value = kf;
        }
        if (slice.KeyTo is { } kt)
        {
            conditions.Add($"{pk} < @kt");
            cmd.Parameters.Add("@kt", SqlDbType.UniqueIdentifier).Value = kt;
        }
        if (afterKey is not null)
            conditions.Add($"{pk} > @k");
        cmd.CommandText =
            $"SELECT TOP ({take}) {_selectList} FROM {_from}" +
            (conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "") +
            $" ORDER BY {pk};";
        if (afterKey is { } key)
            cmd.Parameters.Add("@k", SqlDbType.UniqueIdentifier).Value = key;

        var rows = new List<Entity>(take);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var ordinals = _plan.Attributes.ToDictionary(a => a, r.GetOrdinal, StringComparer.OrdinalIgnoreCase);
        var typeOrdinals = _plan.LookupTypeColumns.ToDictionary(kv => kv.Key, kv => r.GetOrdinal(kv.Value), StringComparer.OrdinalIgnoreCase);
        var pkOrdinal = ordinals[_primaryId];

        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var e = new Entity(_entityName, r.GetGuid(pkOrdinal));
            foreach (var (attribute, ordinal) in ordinals)
            {
                if (r.IsDBNull(ordinal))
                    continue;
                var value = r.GetValue(ordinal);
                if (_plan.LookupAttributes.Contains(attribute) && value is Guid id)
                {
                    var logicalName = typeOrdinals.TryGetValue(attribute, out var to) && !r.IsDBNull(to)
                        ? Convert.ToString(r.GetValue(to), System.Globalization.CultureInfo.InvariantCulture) ?? ""
                        : "";
                    e[attribute] = new EntityReference(logicalName, id);
                }
                else
                {
                    e[attribute] = value switch
                    {
                        DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc), // TDS returns UTC; synced tables store UTC
                        DateTimeOffset dto => dto.UtcDateTime,
                        _ => value,
                    };
                }
            }
            rows.Add(e);
        }
        return rows;
    }

    private static void AddDate(SqlCommand cmd, string name, DateTime value)
    {
        var p = cmd.Parameters.Add(name, SqlDbType.DateTime2);
        p.Scale = 3;
        p.Value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    /// <summary>Closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_conn is not null)
        {
            await _conn.DisposeAsync().ConfigureAwait(false);
            _conn = null;
        }
    }
}
