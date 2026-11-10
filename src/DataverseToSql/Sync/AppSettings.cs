using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace DataverseToSql.Sync;

/// <summary>Dataverse authentication mode.</summary>
public enum AuthKind
{
    /// <summary>Interactive OAuth sign-in (user).</summary>
    Interactive,
    /// <summary>App registration / application user (STD-INT-001).</summary>
    ClientSecret
}

/// <summary>How rows are read from Dataverse.</summary>
public enum SourceKind
{
    /// <summary>Dataverse SDK (ServiceClient, Web API/OData-style paging). Works everywhere; supports documentbody.</summary>
    Sdk,
    /// <summary>Dataverse TDS (SQL) endpoint, port 5558. Read-only, must be enabled in the environment; often faster for big tables.</summary>
    Tds,
    /// <summary>A table in a SQL Server / Azure SQL database (e.g. one this app filled before). <see cref="AppSettings.EntityName"/> is "schema.table".</summary>
    SqlTable
}

/// <summary>How the app signs in to the destination SQL Server / Azure SQL Database.</summary>
public enum SqlAuthKind
{
    /// <summary>Use the connection string exactly as typed (Windows auth, SQL login, or any Authentication= keyword).</summary>
    ConnectionString,
    /// <summary>Microsoft Entra ID interactive sign-in with MFA (browser). Not usable for scheduled/headless runs.</summary>
    EntraInteractive,
    /// <summary>Microsoft Entra ID default credential chain (managed identity, Azure CLI, Visual Studio). For scheduled runs.</summary>
    EntraDefault
}

/// <summary>Sync run mode.</summary>
public enum SyncMode
{
    /// <summary>Copy all rows.</summary>
    Full,
    /// <summary>Copy rows modified since the stored watermark.</summary>
    Incremental
}

/// <summary>How a page is upserted into the target table.</summary>
public enum UpsertMode
{
    /// <summary>UPDATE then INSERT ... WHERE NOT EXISTS at READ COMMITTED, in key order (no range locks; default).</summary>
    UpdateInsert,
    /// <summary>Legacy single MERGE WITH (HOLDLOCK) (SERIALIZABLE key-range locks; deadlock-prone in parallel).</summary>
    Merge
}

/// <summary>Persisted user settings. Never contains secrets (STD-INT-006).</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Dataverse environment URL, e.g. https://org.crm.dynamics.com.</summary>
    public string DataverseUrl { get; set; } = "";

    /// <summary>Authentication type.</summary>
    public AuthKind AuthType { get; set; } = AuthKind.Interactive;

    /// <summary>App (client) id; empty uses the default public dev app id for Interactive.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>Read path: SDK (default) or TDS endpoint.</summary>
    public SourceKind Source { get; set; } = SourceKind.Sdk;

    /// <summary>Logical name of the table to sync; for <see cref="SourceKind.SqlTable"/> the source table as "schema.table".</summary>
    public string EntityName { get; set; } = "annotation";

    /// <summary>Selected attribute logical names; empty means all syncable except documentbody.</summary>
    public List<string> SelectedAttributes { get; set; } = new();

    /// <summary>SQL source (<see cref="SourceKind.SqlTable"/>): server-level connection string (password is stripped on save).</summary>
    public string SourceSqlConnectionString { get; set; } = "";

    /// <summary>SQL source: sign-in method.</summary>
    public SqlAuthKind SourceSqlAuth { get; set; } = SqlAuthKind.ConnectionString;

    /// <summary>SQL source: optional Entra user (UPN) hint for <see cref="SqlAuthKind.EntraInteractive"/>.</summary>
    public string SourceSqlUser { get; set; } = "";

    /// <summary>SQL source: database name.</summary>
    public string SourceDatabaseName { get; set; } = "";

    /// <summary>SQL Server connection string (server level; password is stripped on save).</summary>
    public string SqlConnectionString { get; set; } = "Server=localhost;Integrated Security=true;TrustServerCertificate=true";

    /// <summary>Destination sign-in method.</summary>
    public SqlAuthKind SqlAuth { get; set; } = SqlAuthKind.ConnectionString;

    /// <summary>Optional Entra user (UPN) hint for <see cref="SqlAuthKind.EntraInteractive"/>.</summary>
    public string SqlUser { get; set; } = "";

    /// <summary>Target database name.</summary>
    public string DatabaseName { get; set; } = "DataverseSync";

    /// <summary>Target schema name.</summary>
    public string SchemaName { get; set; } = "dbo";

    /// <summary>Target table name.</summary>
    public string TableName { get; set; } = "annotation";

    /// <summary>Full or incremental run.</summary>
    public SyncMode Mode { get; set; } = SyncMode.Full;

    /// <summary>Dataverse page size.</summary>
    public int PageSize { get; set; } = 5000;

    /// <summary>Requested parallel workers (capped by server hint).</summary>
    public int Parallelism { get; set; } = 8;

    /// <summary>Delete SQL rows no longer present in Dataverse.</summary>
    public bool DetectDeletes { get; set; } = false;

    /// <summary>
    /// Maps a default column name (the attribute logical name, or "&lt;lookup&gt;_entity") to a column of the destination table.
    /// Only entries that differ from the default are stored. Empty means every column keeps its default name.
    /// </summary>
    public Dictionary<string, string> ColumnMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Continue an interrupted run from its last checkpoint instead of starting over.</summary>
    public bool ResumeInterruptedRun { get; set; } = true;

    /// <summary>How many times one sync is attempted before it gives up (each retry resumes from the checkpoint).</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Max workers writing to SQL at the same time; 0 = same as <see cref="Parallelism"/>. 1 rules out deadlocks between workers.</summary>
    public int SqlWriteParallelism { get; set; } = 0;

    /// <summary>Rows per upsert transaction (sub-batch of a page). Kept below the ~5000-lock escalation threshold. 0 = whole page.</summary>
    public int SqlWriteBatchSize { get; set; } = 1000;

    /// <summary>Retries of one sub-batch after a deadlock (1205) or concurrent-insert key violation, before the serialized last-resort attempt.</summary>
    public int DeadlockMaxRetries { get; set; } = 8;

    /// <summary>Base delay of the exponential deadlock back-off, in milliseconds.</summary>
    public int DeadlockRetryBaseDelayMs { get; set; } = 250;

    /// <summary>How rows are upserted into the target table.</summary>
    public UpsertMode UpsertMode { get; set; } = UpsertMode.UpdateInsert;

    /// <summary>Default settings file path: %APPDATA%\DataverseToSql\settings.json.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DataverseToSql", "settings.json");

    /// <summary>Loads settings; returns defaults when the file is missing.</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
            return new AppSettings();

        var json = File.ReadAllText(path);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        settings.SelectedAttributes ??= new();
        return settings;
    }

    /// <summary>Returns the destination connection string with the chosen <see cref="SqlAuth"/> applied.</summary>
    public string BuildSqlConnectionString() => BuildConnectionString(SqlConnectionString, SqlAuth, SqlUser);

    /// <summary>Returns the SQL source connection string (with <see cref="SourceDatabaseName"/> as initial catalog).</summary>
    public string BuildSourceSqlConnectionString() =>
        new SqlConnectionStringBuilder(BuildConnectionString(SourceSqlConnectionString, SourceSqlAuth, SourceSqlUser))
        {
            InitialCatalog = SourceDatabaseName.Trim(),
            ApplicationName = "DataverseToSql",
        }.ConnectionString;

    private static string BuildConnectionString(string connectionString, SqlAuthKind auth, string user)
    {
        var b = new SqlConnectionStringBuilder(connectionString);
        if (auth == SqlAuthKind.ConnectionString)
            return b.ConnectionString;

        // Entra ID: no Windows auth and no password; Azure SQL requires encryption.
        b.IntegratedSecurity = false;
        b.Remove("Password");
        b.Encrypt = SqlConnectionEncryptOption.Mandatory;
        if (auth == SqlAuthKind.EntraInteractive)
        {
            b.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
            b.Remove("User ID");
            if (!string.IsNullOrWhiteSpace(user))
                b.UserID = user.Trim();
        }
        else
        {
            b.Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault;
            b.Remove("User ID");
        }
        return b.ConnectionString;
    }

    /// <summary>Writes settings as JSON. The SQL password is stripped (STD-INT-006).</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var copy = (AppSettings)MemberwiseClone();
        copy.SelectedAttributes = new List<string>(SelectedAttributes ?? new());
        copy.SqlConnectionString = StripPassword(SqlConnectionString);
        copy.SourceSqlConnectionString = StripPassword(SourceSqlConnectionString);

        var json = JsonSerializer.Serialize(copy, JsonOptions);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    // STD-INT-006: secrets are never persisted.
    private static string StripPassword(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return "";
        try
        {
            var b = new SqlConnectionStringBuilder(connectionString);
            b.Remove("Password"); // also covers the PWD synonym
            return b.ConnectionString;
        }
        catch (ArgumentException)
        {
            // Unparseable: do not risk persisting a secret.
            return "";
        }
    }
}
