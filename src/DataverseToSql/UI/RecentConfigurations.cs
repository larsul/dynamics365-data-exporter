using System.Text.Json;
using DataverseToSql.Sync;

namespace DataverseToSql.UI;

/// <summary>Most-recently-used configuration files, stored in %APPDATA%\DataverseToSql\recent.json.</summary>
internal static class RecentConfigurations
{
    private const int MaxEntries = 10;

    private static string AppFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataverseToSql");

    /// <summary>Default folder for configuration files: %APPDATA%\DataverseToSql\configs.</summary>
    public static string ConfigFolder { get; } = Path.Combine(AppFolder, "configs");

    private static string FilePath => Path.Combine(AppFolder, "recent.json");

    /// <summary>Returns existing recent files, newest first.</summary>
    public static List<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new List<string>();
            }
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? new List<string>();
            return list.Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxEntries).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not read the recent configurations list.", ex);
            return new List<string>();
        }
    }

    /// <summary>Moves <paramref name="path"/> to the top of the list.</summary>
    public static void Add(string path)
    {
        var full = Path.GetFullPath(path);
        var list = Load();
        list.RemoveAll(p => p.Equals(full, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, full);
        Save(list.Take(MaxEntries).ToList());
    }

    /// <summary>Removes <paramref name="path"/> from the list (the file itself is kept).</summary>
    public static void Remove(string path)
    {
        var list = Load();
        list.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        Save(list);
    }

    private static void Save(List<string> list)
    {
        try
        {
            Directory.CreateDirectory(AppFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not save the recent configurations list.", ex);
        }
    }
}
