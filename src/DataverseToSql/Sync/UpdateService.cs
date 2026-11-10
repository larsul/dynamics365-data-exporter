using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace DataverseToSql.Sync;

/// <summary>How this copy of the app was deployed, which decides how an update is applied.</summary>
internal enum InstallKind
{
    /// <summary>Installed with the setup program: update by running the new installer silently.</summary>
    Installed,

    /// <summary>Portable single file: update by replacing the executable in place.</summary>
    Portable,
}

/// <summary>A release published on GitHub, reduced to what the updater needs.</summary>
internal sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string Name,
    string Notes,
    DateTimeOffset? Published,
    string? InstallerUrl,
    long InstallerSize,
    string? PortableUrl,
    long PortableSize,
    string HtmlUrl)
{
    /// <summary>The asset that fits <paramref name="kind"/>, falling back to the other one when only that was published.</summary>
    public (string? Url, long Size, bool IsInstaller) AssetFor(InstallKind kind) =>
        kind == InstallKind.Installed
            ? (InstallerUrl is not null ? (InstallerUrl, InstallerSize, true) : (PortableUrl, PortableSize, false))
            : (PortableUrl is not null ? (PortableUrl, PortableSize, false) : (InstallerUrl, InstallerSize, true));
}

/// <summary>
/// Checks GitHub Releases for a newer version, downloads the matching asset and applies it.
/// Installed copies run the new setup program silently; portable copies are replaced by a helper
/// script that waits for this process to exit, swaps the file and starts the app again.
/// </summary>
internal static class UpdateService
{
    /// <summary>Marker file the setup program writes next to the executable.</summary>
    public const string InstalledMarker = ".installed";

    private const string UserAgent = "DataverseToSql-Updater";
    private const string InstallerSuffix = "-Setup.exe";
    private const string PortableSuffix = "-portable-win-x64.zip";

    // Lazy, not a field initializer: the client's User-Agent needs CurrentVersion, and static fields are
    // initialized in declaration order, so building it eagerly here would read CurrentVersion as null.
    private static readonly Lazy<HttpClient> LazyHttp = new(CreateClient, LazyThreadSafetyMode.ExecutionAndPublication);

    private static HttpClient Http => LazyHttp.Value;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, CurrentVersionText));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    /// <summary>Version of the running app, from the assembly (the release workflow stamps it from the git tag).</summary>
    public static Version CurrentVersion { get; } = ReadCurrentVersion();

    /// <summary>Version as shown in the UI, for example <c>1.2.3</c>.</summary>
    public static string CurrentVersionText => CurrentVersion.ToString(3);

    /// <summary><c>owner/name</c> of the GitHub repository that publishes the releases.</summary>
    public static string Repository { get; } =
        typeof(UpdateService).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value is { Length: > 0 } repo
            ? repo
            : "larsul/dynamics365-data-exporter";

    /// <summary>Page a user can open to download a release by hand.</summary>
    public static string ReleasesPageUrl => $"https://github.com/{Repository}/releases";

    /// <summary>The project's changelog, which lists what changed in every version.</summary>
    public static string ChangelogUrl => $"https://github.com/{Repository}/blob/main/CHANGELOG.md";

    /// <summary>Full path of the running executable (the single file itself, not its extraction folder).</summary>
    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DataverseToSql.exe");

    /// <summary>Whether this copy was installed with the setup program or unpacked from the portable zip.</summary>
    public static InstallKind Kind =>
        File.Exists(Path.Combine(Path.GetDirectoryName(ExecutablePath) ?? ".", InstalledMarker))
            ? InstallKind.Installed
            : InstallKind.Portable;

    private static Version ReadCurrentVersion()
    {
        var assembly = typeof(UpdateService).Assembly;
        var text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? assembly.GetName().Version?.ToString()
                   ?? "0.0.0";
        return ParseVersion(text) ?? new Version(0, 0, 0);
    }

    /// <summary>
    /// Reads a version out of a tag or informational version: strips a leading <c>v</c> and anything
    /// from the first <c>-</c> or <c>+</c> (pre-release and build metadata), then pads it to three parts.
    /// </summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var span = text.Trim();
        if (span.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            span = span[1..];
        }
        var cut = span.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut >= 0)
        {
            span = span[..cut];
        }
        var parts = span.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var numbers = new int[4];
        var count = 0;
        foreach (var part in parts)
        {
            if (count == 4 || !int.TryParse(part, out var value) || value < 0)
            {
                break;
            }
            numbers[count++] = value;
        }
        return count == 0 ? null : new Version(numbers[0], count > 1 ? numbers[1] : 0, count > 2 ? numbers[2] : 0);
    }

    /// <summary>
    /// Reads the latest published release. Returns null when the repository has no usable release yet.
    /// Throws on network and HTTP errors so the caller can show why the check failed.
    /// </summary>
    public static async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken token)
    {
        var url = $"https://api.github.com/repos/{Repository}/releases/latest";
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null; // no release published yet
        }
        if (!response.IsSuccessStatusCode)
        {
            var hint = response.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? " (GitHub rate limit reached for this network; try again later)"
                : "";
            throw new InvalidOperationException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}{hint}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        var root = json.RootElement;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var version = ParseVersion(tag);
        if (version is null)
        {
            return null;
        }

        string? installerUrl = null, portableUrl = null;
        long installerSize = 0, portableSize = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var download = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                if (download is null)
                {
                    continue;
                }
                if (name.EndsWith(InstallerSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    (installerUrl, installerSize) = (download, size);
                }
                else if (name.EndsWith(PortableSuffix, StringComparison.OrdinalIgnoreCase)
                         || (portableUrl is null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
                {
                    (portableUrl, portableSize) = (download, size);
                }
            }
        }

        var published = root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(p.GetString(), out var when)
            ? when
            : (DateTimeOffset?)null;

        return new ReleaseInfo(
            version,
            tag,
            root.TryGetProperty("name", out var rn) && rn.ValueKind == JsonValueKind.String ? rn.GetString() ?? tag : tag,
            root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "",
            published,
            installerUrl,
            installerSize,
            portableUrl,
            portableSize,
            root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPageUrl : ReleasesPageUrl);
    }

    /// <summary>True when <paramref name="release"/> is newer than the running app.</summary>
    public static bool IsNewer(ReleaseInfo? release) => release is not null && release.Version > CurrentVersion;

    /// <summary>
    /// Downloads <paramref name="url"/> into a new folder under the temp directory, reporting progress as a
    /// fraction (null while the total length is unknown). Returns the downloaded file's path.
    /// </summary>
    public static async Task<string> DownloadAsync(
        string url,
        long expectedSize,
        IProgress<(double? Fraction, long Bytes, long Total)>? progress,
        CancellationToken token)
    {
        var folder = Path.Combine(Path.GetTempPath(), "DataverseToSql-update", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var name = Path.GetFileName(new Uri(url).LocalPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "update.bin";
        }
        var target = Path.Combine(folder, name);

        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? expectedSize;

        await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        await using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            var buffer = new byte[1 << 20];
            long done = 0, lastReport = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                done += read;
                if (done - lastReport >= 1 << 20 || done == total)
                {
                    lastReport = done;
                    progress?.Report((total > 0 ? Math.Min(1.0, (double)done / total) : null, done, total));
                }
            }
            progress?.Report((total > 0 ? 1.0 : null, done, total > 0 ? total : done));
        }
        return target;
    }

    /// <summary>
    /// Applies a downloaded update and returns true when the app must now exit. The installer case starts
    /// the setup program silently; the portable case starts a helper script that swaps the executable.
    /// </summary>
    public static bool Apply(string downloadedPath, bool isInstaller) =>
        isInstaller ? ApplyInstaller(downloadedPath) : ApplyPortable(downloadedPath);

    private static bool ApplyInstaller(string setupPath)
    {
        // Inno Setup: /SILENT shows only a progress window; the app flags close and restart this app for us.
        var info = new ProcessStartInfo(setupPath)
        {
            UseShellExecute = true,
            Arguments = "/SILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /NORESTART",
        };
        if (Process.Start(info) is null)
        {
            throw new InvalidOperationException("The setup program did not start.");
        }
        AppLog.Info($"Update: started {Path.GetFileName(setupPath)} silently; the app closes now.");
        return true;
    }

    private static bool ApplyPortable(string zipPath)
    {
        var staging = Path.Combine(Path.GetDirectoryName(zipPath)!, "unpacked");
        Directory.CreateDirectory(staging);
        ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

        var newExe = Directory
                         .EnumerateFiles(staging, "DataverseToSql.exe", SearchOption.AllDirectories)
                         .FirstOrDefault()
                     ?? throw new InvalidOperationException("The downloaded archive does not contain DataverseToSql.exe.");

        var target = ExecutablePath;
        var targetFolder = Path.GetDirectoryName(target)!;
        if (!CanWriteTo(targetFolder))
        {
            throw new UnauthorizedAccessException(
                $"No write access to \"{targetFolder}\". Move the app to a folder you can write to, or download the update by hand.");
        }

        var script = Path.Combine(Path.GetDirectoryName(zipPath)!, "apply-update.ps1");
        File.WriteAllText(script, PortableUpdateScript(Environment.ProcessId, newExe, target), new UTF8Encoding(false));
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"",
        };
        if (Process.Start(info) is null)
        {
            throw new InvalidOperationException("The update helper did not start.");
        }
        AppLog.Info($"Update: helper started; \"{Path.GetFileName(target)}\" is replaced as soon as the app closes.");
        return true;
    }

    private static bool CanWriteTo(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".write-probe-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Helper that runs after the app exits: waits for the process, keeps the old file as a backup,
    /// copies the new one in, starts it again and cleans up. On failure it restores the backup.
    /// </summary>
    private static string PortableUpdateScript(int processId, string newExe, string target)
    {
        var newEscaped = newExe.Replace("'", "''");
        var targetEscaped = target.Replace("'", "''");
        return $@"
$ErrorActionPreference = 'Stop'
$waitPid = {processId}
$new     = '{newEscaped}'
$target  = '{targetEscaped}'
$backup  = ""$target.old""
$log     = Join-Path $env:TEMP 'DataverseToSql-update.log'

function Write-Log($message) {{
    try {{ Add-Content -LiteralPath $log -Value (""{{0:s}}  {{1}}"" -f (Get-Date), $message) }} catch {{ }}
}}

try {{
    Write-Log ""Waiting for process $waitPid to exit.""
    $p = Get-Process -Id $waitPid -ErrorAction SilentlyContinue
    if ($p) {{
        if (-not $p.WaitForExit(120000)) {{
            Write-Log 'The app is still running after 120s; update abandoned.'
            exit 1
        }}
    }} else {{
        Write-Log 'The app had already exited.'
    }}

    # The file handle can linger for a moment after exit, so retry the swap for a few seconds.
    $swapped = $false
    for ($i = 1; $i -le 20; $i++) {{
        try {{
            if (Test-Path -LiteralPath $backup) {{ Remove-Item -LiteralPath $backup -Force }}
            if (Test-Path -LiteralPath $target) {{ Move-Item -LiteralPath $target -Destination $backup -Force }}
            Copy-Item -LiteralPath $new -Destination $target -Force
            $swapped = $true
            break
        }} catch {{
            Start-Sleep -Milliseconds 500
        }}
    }}
    if (-not $swapped) {{ throw ""Could not replace $target."" }}

    Write-Log ""Replaced $target.""
    if (Test-Path -LiteralPath $backup) {{ Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue }}
    Start-Process -FilePath $target
    Write-Log 'Restarted the app.'
}} catch {{
    Write-Log ""Update failed: $($_.Exception.Message)""
    if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $target)) {{
        Move-Item -LiteralPath $backup -Destination $target -Force
        Write-Log 'Restored the previous version.'
        Start-Process -FilePath $target
    }}
}} finally {{
    # Delete the whole download folder, this script included, once we are no longer running from it.
    $folder = Split-Path -Parent $PSCommandPath
    Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-Command',
        ""Start-Sleep -Seconds 5; Remove-Item -LiteralPath '$folder' -Recurse -Force -ErrorAction SilentlyContinue""
    )
}}
";
    }
}
