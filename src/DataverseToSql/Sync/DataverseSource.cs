using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace DataverseToSql.Sync;

/// <summary>One page of rows read from a Dataverse time slice.</summary>
/// <remarks><see cref="NextPagingCookie"/> lets a later process resume the slice at <c>PageNumber + 1</c>.</remarks>
public sealed record DataverseSlicePage(IReadOnlyList<Entity> Rows, bool MoreRecords, int PageNumber, string? NextPagingCookie = null);

/// <summary>Dataverse connection and paged read helpers.</summary>
public sealed class DataverseSource : IDisposable
{
    private const string DefaultPublicAppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
    private const int MaxPages = 1_000_000;
    private const int IdPageSize = 5000;

    private readonly Guid _correlationId;

    static DataverseSource()
    {
        // Avoid thread-pool starvation when many parallel Dataverse calls are in flight.
        ThreadPool.SetMinThreads(100, 100);
    }

    private DataverseSource(ServiceClient client, Guid correlationId)
    {
        Client = client;
        _correlationId = correlationId;
    }

    /// <summary>The connected service client.</summary>
    public ServiceClient Client { get; }

    /// <summary>Server-recommended degree of parallelism (x-ms-dop-hint), at least 1. STD-DVB-020.</summary>
    public int RecommendedParallelism => Math.Max(1, Client.RecommendedDegreesOfParallelism);

    /// <summary>Connects to Dataverse using the configured auth type.</summary>
    /// <param name="nonInteractive">True for headless runs: Interactive auth uses LoginPrompt=Never so a token-cache miss fails fast.</param>
    public static DataverseSource Connect(AppSettings s, string? clientSecret, Guid correlationId, bool nonInteractive = false)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (string.IsNullOrWhiteSpace(s.DataverseUrl))
            throw new InvalidOperationException("Dataverse URL is not configured.");

        var b = new DbConnectionStringBuilder();
        if (s.AuthType == AuthKind.ClientSecret)
        {
            // STD-INT-001: server-to-server via app registration / application user.
            if (string.IsNullOrEmpty(clientSecret))
                throw new InvalidOperationException("Client secret is required for ClientSecret authentication.");
            if (string.IsNullOrWhiteSpace(s.ClientId))
                throw new InvalidOperationException("Client ID is required for ClientSecret authentication.");
            b["AuthType"] = "ClientSecret";
            b["Url"] = s.DataverseUrl;
            b["ClientId"] = s.ClientId;
            b["ClientSecret"] = clientSecret; // STD-INT-006: held only in memory, never logged.
        }
        else
        {
            // STD-INT-002: persist and reuse the MSAL token cache between runs.
            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DataverseToSql");
            Directory.CreateDirectory(cacheDir);
            b["AuthType"] = "OAuth";
            b["Url"] = s.DataverseUrl;
            b["AppId"] = string.IsNullOrWhiteSpace(s.ClientId) ? DefaultPublicAppId : s.ClientId;
            b["RedirectUri"] = "http://localhost";
            b["LoginPrompt"] = nonInteractive ? "Never" : "Auto";
            b["TokenCacheStorePath"] = Path.Combine(cacheDir, "tokencache");
        }

        // STD-INT-006: the connection string is never logged.
        var client = new ServiceClient(b.ConnectionString);
        if (!client.IsReady)
        {
            var error = client.LastError;
            var ex = client.LastException;
            client.Dispose();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error) ? "Failed to connect to Dataverse." : error, ex);
        }

        client.EnableAffinityCookie = false;
        // STD-INT-003: built-in retry handles service-protection throttling (429).
        client.MaxRetryCount = 10;
        client.RetryPauseTime = TimeSpan.FromSeconds(5);
        // STD-INT-007: correlation id per run.
        client.SessionTrackingId = correlationId;

        return new DataverseSource(client, correlationId);
    }

    /// <summary>Creates an independent client sharing the authenticated session.</summary>
    public ServiceClient CloneClient()
    {
        var clone = Client.Clone();
        clone.EnableAffinityCookie = false;
        clone.SessionTrackingId = _correlationId; // STD-INT-007
        clone.MaxRetryCount = Client.MaxRetryCount;
        clone.RetryPauseTime = Client.RetryPauseTime;
        return clone;
    }

    /// <summary>Retrieves entity metadata including attributes.</summary>
    public async Task<EntityMetadata> GetMetadataAsync(string entityName, CancellationToken ct)
    {
        var req = new RetrieveEntityRequest
        {
            LogicalName = entityName,
            EntityFilters = EntityFilters.Attributes,
            RetrieveAsIfPublished = false,
        };
        var resp = (RetrieveEntityResponse)await Client.ExecuteAsync(req, ct).ConfigureAwait(false);
        return resp.EntityMetadata;
    }

    /// <summary>Returns the smallest modifiedon (UTC), or null when the table is empty.</summary>
    public async Task<DateTime?> GetMinModifiedOnAsync(string entityName, CancellationToken ct)
    {
        var q = new QueryExpression(entityName)
        {
            ColumnSet = new ColumnSet("modifiedon"),
            NoLock = true,
            TopCount = 1,
        };
        q.Criteria.AddCondition("modifiedon", ConditionOperator.NotNull);
        q.AddOrder("modifiedon", OrderType.Ascending);

        var result = await Client.RetrieveMultipleAsync(q, ct).ConfigureAwait(false);
        var first = result.Entities.FirstOrDefault();
        if (first is null || !first.TryGetAttributeValue<DateTime>("modifiedon", out var value))
            return null;
        return value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);
    }

    /// <summary>Returns the largest modifiedon (UTC, server clock), or null when the table is empty.</summary>
    public async Task<DateTime?> GetMaxModifiedOnAsync(string entityName, CancellationToken ct)
    {
        var q = new QueryExpression(entityName)
        {
            ColumnSet = new ColumnSet("modifiedon"),
            NoLock = true,
            TopCount = 1,
        };
        q.Criteria.AddCondition("modifiedon", ConditionOperator.NotNull);
        q.AddOrder("modifiedon", OrderType.Descending);

        var result = await Client.RetrieveMultipleAsync(q, ct).ConfigureAwait(false);
        var first = result.Entities.FirstOrDefault();
        if (first is null || !first.TryGetAttributeValue<DateTime>("modifiedon", out var value))
            return null;
        return value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc);
    }

    /// <summary>
    /// Reads all rows with modifiedon in [fromUtc, toUtc) and the primary key in [keyFrom, keyTo) (null bounds are open),
    /// page by page to exhaustion.
    /// </summary>
    public static async IAsyncEnumerable<DataverseSlicePage> ReadSliceAsync(
        ServiceClient client, string entityName, string primaryId, string[] attributes,
        DateTime? fromUtc, DateTime? toUtc, int pageSize, [EnumeratorCancellation] CancellationToken ct,
        int startPage = 1, string? startPagingCookie = null, Guid? keyFrom = null, Guid? keyTo = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var q = new QueryExpression(entityName)
        {
            ColumnSet = new ColumnSet(attributes),
            NoLock = true,
        };
        if (fromUtc is { } f)
            q.Criteria.AddCondition("modifiedon", ConditionOperator.GreaterEqual, DateTime.SpecifyKind(f, DateTimeKind.Utc));
        if (toUtc is { } t)
            q.Criteria.AddCondition("modifiedon", ConditionOperator.LessThan, DateTime.SpecifyKind(t, DateTimeKind.Utc));
        // Primary-key range: evaluated in SQL, so it uses the same uniqueidentifier order as the ORDER BY below.
        if (keyFrom is { } kf)
            q.Criteria.AddCondition(primaryId, ConditionOperator.GreaterEqual, kf);
        if (keyTo is { } kt)
            q.Criteria.AddCondition(primaryId, ConditionOperator.LessThan, kt);
        // Order only by primary key so the paging cookie stays efficient.
        q.AddOrder(primaryId, OrderType.Ascending);

        await foreach (var page in PageAsync(client, q, pageSize, ct, startPage, startPagingCookie).ConfigureAwait(false))
            yield return page;
    }

    /// <summary>Reads every primary id of the entity, in pages of 5000.</summary>
    public static async IAsyncEnumerable<IReadOnlyList<Guid>> ReadAllIdsAsync(
        ServiceClient client, string entityName, string primaryId, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        var q = new QueryExpression(entityName)
        {
            ColumnSet = new ColumnSet(primaryId),
            NoLock = true,
        };
        q.AddOrder(primaryId, OrderType.Ascending);

        await foreach (var page in PageAsync(client, q, IdPageSize, ct).ConfigureAwait(false))
            yield return page.Rows.Select(e => e.Id).ToList();
    }

    // STD-DVB-003: page to exhaustion using paging cookies, bounded to avoid runaway loops.
    private static async IAsyncEnumerable<DataverseSlicePage> PageAsync(
        ServiceClient client, QueryExpression q, int pageSize, [EnumeratorCancellation] CancellationToken ct,
        int startPage = 1, string? startPagingCookie = null)
    {
        // A paging cookie only carries the last primary key read, so it stays valid across processes (checkpoint resume).
        var pageNumber = Math.Max(1, startPage);
        string? cookie = pageNumber > 1 ? startPagingCookie : null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (pageNumber > MaxPages)
                throw new InvalidOperationException($"Paging exceeded {MaxPages} pages for '{q.EntityName}'.");

            q.PageInfo = new PagingInfo
            {
                Count = Math.Clamp(pageSize, 1, 5000),
                PageNumber = pageNumber,
                PagingCookie = cookie,
            };

            var result = await client.RetrieveMultipleAsync(q, ct).ConfigureAwait(false);
            yield return new DataverseSlicePage(result.Entities.ToList(), result.MoreRecords, pageNumber,
                result.MoreRecords ? result.PagingCookie : null);

            if (!result.MoreRecords)
                yield break;

            cookie = result.PagingCookie;
            pageNumber++;
        }
    }

    /// <summary>Disposes the underlying client.</summary>
    public void Dispose() => Client.Dispose();
}
