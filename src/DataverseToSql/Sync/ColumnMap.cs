using System.Data;
using System.Globalization;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace DataverseToSql.Sync;

/// <summary>How a SQL column is derived from a Dataverse attribute value.</summary>
public enum ColumnKind { Value, LookupId, LookupEntity, Base64Binary }

/// <summary>One SQL column mapped from a Dataverse attribute.</summary>
public sealed record ColumnDef(string AttributeName, string ColumnName, string SqlType, Type ClrType, ColumnKind Kind, bool IsPrimaryKey);

/// <summary>Maps Dataverse attribute metadata to SQL columns and converts entities to DataTables.</summary>
public static class ColumnMap
{
    private const string DocumentBody = "documentbody";
    private const string ModifiedOn = "modifiedon";

    /// <summary>True when the attribute can be read and stored as a SQL column.</summary>
    public static bool IsSyncable(AttributeMetadata a)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (string.IsNullOrEmpty(a.LogicalName)) return false;
        if (a.IsValidForRead != true) return false;
        if (a.AttributeOf != null) return false; // shadow name/yomi columns
        if (a is ImageAttributeMetadata || IsFileOrImage(a)) return false;

        return a.AttributeType switch
        {
            null => false,
            AttributeTypeCode.ManagedProperty or AttributeTypeCode.PartyList or AttributeTypeCode.CalendarRules => false,
            AttributeTypeCode.Virtual => a is MultiSelectPicklistAttributeMetadata,
            _ => true,
        };
    }

    /// <summary>Marks <see cref="AttributeMetadata.ExternalName"/> of a SQL-table source column; the rest is its SQL type.</summary>
    public const string SqlTypePrefix = "sql:";

    /// <summary>Short type description for UI display.</summary>
    public static string Describe(AttributeMetadata a)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.ExternalName is { } ext && ext.StartsWith(SqlTypePrefix, StringComparison.Ordinal)) return ext[SqlTypePrefix.Length..];
        if (a is MultiSelectPicklistAttributeMetadata) return "MultiSelectPicklist";
        if (a is LookupAttributeMetadata l)
        {
            var kind = a.AttributeType?.ToString() ?? "Lookup";
            var targets = l.Targets is { Length: > 0 } t ? string.Join(",", t) : "?";
            return $"{kind} -> {targets}";
        }
        if (a is StringAttributeMetadata s && s.MaxLength is int len) return $"String({len})";
        return a.AttributeTypeName?.Value?.Replace("Type", "", StringComparison.Ordinal)
               ?? a.AttributeType?.ToString()
               ?? "Unknown";
    }

    /// <summary>True when the entity has a readable modifiedon attribute (needed for time slicing and incremental runs).</summary>
    public static bool HasModifiedOn(EntityMetadata meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        return (meta.Attributes ?? Array.Empty<AttributeMetadata>())
            .Any(a => ModifiedOn.Equals(a.LogicalName, StringComparison.OrdinalIgnoreCase) && IsSyncable(a));
    }

    /// <summary>Builds the column list: primary id first, modifiedon when the entity has it, then selected (or all syncable except documentbody).</summary>
    public static IReadOnlyList<ColumnDef> Build(EntityMetadata meta, IEnumerable<string> selectedAttributes)
    {
        ArgumentNullException.ThrowIfNull(meta);
        var attrs = (meta.Attributes ?? Array.Empty<AttributeMetadata>())
            .Where(a => !string.IsNullOrEmpty(a.LogicalName))
            .GroupBy(a => a.LogicalName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var primaryId = meta.PrimaryIdAttribute
            ?? throw new InvalidOperationException($"Entity '{meta.LogicalName}' has no primary id attribute.");

        var selected = (selectedAttributes ?? Enumerable.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .ToList();

        IEnumerable<string> names = selected.Count > 0
            ? selected
            : attrs.Values.Where(IsSyncable)
                .Select(a => a.LogicalName)
                .Where(n => !n.Equals(DocumentBody, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        var result = new List<ColumnDef>
        {
            new(primaryId, primaryId, "uniqueidentifier", typeof(Guid), ColumnKind.Value, true),
        };
        var usedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primaryId };
        var usedAttrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primaryId };

        foreach (var name in names.Prepend(ModifiedOn))
        {
            if (!usedAttrs.Add(name)) continue;
            if (!attrs.TryGetValue(name, out var a) || !IsSyncable(a)) continue;
            foreach (var col in Map(a))
            {
                if (usedColumns.Add(col.ColumnName)) result.Add(col);
            }
        }
        return result;
    }

    /// <summary>Renames columns per the destination mapping (default column name -> destination column). Rejects two sources mapped to one column.</summary>
    public static IReadOnlyList<ColumnDef> ApplyMappings(IReadOnlyList<ColumnDef> cols, IReadOnlyDictionary<string, string>? mappings)
    {
        ArgumentNullException.ThrowIfNull(cols);
        if (mappings is null || mappings.Count == 0)
            return cols;

        var lookup = new Dictionary<string, string>(mappings.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in mappings)
            if (!string.IsNullOrWhiteSpace(kv.Value))
                lookup[kv.Key] = kv.Value.Trim();

        var mapped = cols.Select(c => lookup.TryGetValue(c.ColumnName, out var target) ? c with { ColumnName = target } : c).ToList();

        var duplicate = mapped.GroupBy(c => c.ColumnName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new SyncConfigurationException(
                $"Destination column [{duplicate.Key}] is mapped from more than one source column " +
                $"({string.Join(", ", duplicate.Select(c => c.AttributeName))}). Map each one to its own column.");
        return mapped;
    }

    /// <summary>Distinct attribute names of <paramref name="cols"/> for the query ColumnSet.</summary>
    public static string[] AttributeNames(IReadOnlyList<ColumnDef> cols)
    {
        ArgumentNullException.ThrowIfNull(cols);
        return cols.Select(c => c.AttributeName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Converts Dataverse entities into a DataTable shaped by the column list.</summary>
    public static DataTable ToDataTable(IReadOnlyList<ColumnDef> cols, IEnumerable<Entity> rows)
    {
        ArgumentNullException.ThrowIfNull(cols);
        ArgumentNullException.ThrowIfNull(rows);

        var table = new DataTable { Locale = CultureInfo.InvariantCulture };
        foreach (var c in cols) table.Columns.Add(c.ColumnName, c.ClrType);

        var values = new object[cols.Count];
        table.BeginLoadData();
        foreach (var e in rows)
        {
            for (int i = 0; i < cols.Count; i++)
            {
                var c = cols[i];
                e.Attributes.TryGetValue(c.AttributeName, out var raw);
                if (raw == null && c.IsPrimaryKey && e.Id != Guid.Empty) raw = e.Id;
                values[i] = Convert(raw, c);
            }
            table.Rows.Add(values);
        }
        table.EndLoadData();
        return table;
    }

    private static IEnumerable<ColumnDef> Map(AttributeMetadata a)
    {
        var n = a.LogicalName;

        // documentbody is base64 file content. Some orgs report it as String (max 4000), others as Memo; the value can be
        // far longer either way, so always store it as bytes.
        if (n.Equals(DocumentBody, StringComparison.OrdinalIgnoreCase))
        {
            yield return new(n, n, "varbinary(max)", typeof(byte[]), ColumnKind.Base64Binary, false);
            yield break;
        }

        if (a is MultiSelectPicklistAttributeMetadata)
        {
            yield return new(n, n, "nvarchar(4000)", typeof(string), ColumnKind.Value, false);
            yield break;
        }

        switch (a.AttributeType)
        {
            case AttributeTypeCode.Lookup:
            case AttributeTypeCode.Owner:
            case AttributeTypeCode.Customer:
                yield return new(n, n, "uniqueidentifier", typeof(Guid), ColumnKind.LookupId, false);
                var polymorphic = a.AttributeType != AttributeTypeCode.Lookup
                    || (a is LookupAttributeMetadata { Targets.Length: > 1 });
                if (polymorphic)
                    yield return new(n, n + "_entity", "nvarchar(128)", typeof(string), ColumnKind.LookupEntity, false);
                yield break;

            case AttributeTypeCode.String:
                // MaxLength can be lowered without trimming stored data; DatabaseLength reflects the physical column.
                var sa = a as StringAttributeMetadata;
                var len = Math.Clamp(Math.Max(sa?.MaxLength ?? 4000, sa?.DatabaseLength ?? 0), 1, 4000);
                yield return new(n, n, $"nvarchar({len})", typeof(string), ColumnKind.Value, false);
                yield break;

            case AttributeTypeCode.Memo:
                yield return new(n, n, "nvarchar(max)", typeof(string), ColumnKind.Value, false);
                yield break;
        }

        var (sql, clr) = a.AttributeType switch
        {
            AttributeTypeCode.Integer => ("int", typeof(int)),
            AttributeTypeCode.BigInt => ("bigint", typeof(long)),
            AttributeTypeCode.Decimal => ("decimal(38,10)", typeof(decimal)),
            AttributeTypeCode.Money => ("decimal(38,4)", typeof(decimal)),
            AttributeTypeCode.Double => ("float", typeof(double)),
            AttributeTypeCode.Boolean => ("bit", typeof(bool)),
            AttributeTypeCode.DateTime => ("datetime2(3)", typeof(DateTime)),
            AttributeTypeCode.Uniqueidentifier => ("uniqueidentifier", typeof(Guid)),
            AttributeTypeCode.Picklist or AttributeTypeCode.State or AttributeTypeCode.Status => ("int", typeof(int)),
            AttributeTypeCode.EntityName => ("nvarchar(128)", typeof(string)),
            _ => ("nvarchar(max)", typeof(string)),
        };
        yield return new(n, n, sql, clr, ColumnKind.Value, false);
    }

    private static bool IsFileOrImage(AttributeMetadata a)
    {
        var tn = a.AttributeTypeName?.Value;
        return tn is "FileType" or "ImageType";
    }

    private static object Convert(object? raw, ColumnDef c)
    {
        while (raw is AliasedValue av) raw = av.Value;
        if (raw == null) return DBNull.Value;

        switch (c.Kind)
        {
            case ColumnKind.LookupId:
                return raw switch
                {
                    EntityReference er => er.Id,
                    Guid g => g,
                    _ => DBNull.Value,
                };
            case ColumnKind.LookupEntity:
                return raw is EntityReference er2 && !string.IsNullOrEmpty(er2.LogicalName)
                    ? er2.LogicalName
                    : DBNull.Value;
            case ColumnKind.Base64Binary:
                return raw switch
                {
                    string s when s.Length == 0 => Array.Empty<byte>(),
                    string s => System.Convert.FromBase64String(s),
                    byte[] b => b,
                    _ => DBNull.Value,
                };
        }

        object value = raw switch
        {
            OptionSetValue o => o.Value,
            OptionSetValueCollection coll => string.Join(",", coll.Select(o => o.Value.ToString(CultureInfo.InvariantCulture))),
            Money m => m.Value,
            EntityReference er3 => er3.Id,
            DateTime dt => dt.Kind == DateTimeKind.Utc ? dt
                : DateTime.SpecifyKind(dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt, DateTimeKind.Utc),
            _ => raw,
        };

        if (c.ClrType.IsInstanceOfType(value)) return value;
        if (c.ClrType == typeof(string))
            return value is byte[] bytes
                ? System.Convert.ToBase64String(bytes) // binary from a SQL source into a text column
                : System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? (object)DBNull.Value;
        if (c.ClrType == typeof(Guid)) return value is string gs && Guid.TryParse(gs, out var pg) ? pg : DBNull.Value;
        try
        {
            return System.Convert.ChangeType(value, c.ClrType, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return DBNull.Value;
        }
    }
}
