/*
    Converts the documentbody column of an existing table to nvarchar(max) (base64 text), in place.

    - varbinary / binary / image : file bytes are encoded to base64 text (lossless; decode gives the same bytes).
    - nvarchar(n) / varchar(n)   : widened to nvarchar(max); the stored base64 text is kept as it is.
    - nvarchar(max)              : nothing to do.

    Run it once per table. Set @schema and @table first. Take a backup (or test on a copy) before running it on production data.
    Works on SQL Server 2016+ and Azure SQL. Large tables are converted in batches of 1000 rows to keep the log small.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @schema sysname = N'dbo';
DECLARE @table  sysname = N'AnnotationTemp';
DECLARE @column sysname = N'documentbody';

DECLARE @full nvarchar(300) = QUOTENAME(@schema) + N'.' + QUOTENAME(@table);
DECLARE @type sysname, @maxLength int, @sql nvarchar(max);

SELECT @type = TYPE_NAME(c.system_type_id), @maxLength = c.max_length
FROM sys.columns AS c
WHERE c.object_id = OBJECT_ID(@full, N'U') AND c.name = @column;

IF @type IS NULL
BEGIN
    RAISERROR(N'Column %s not found on %s.', 16, 1, @column, @full);
    RETURN;
END;

IF @type = N'nvarchar' AND @maxLength = -1
BEGIN
    PRINT @full + N'.' + QUOTENAME(@column) + N' is already nvarchar(max). Nothing to do.';
    RETURN;
END;

IF @type IN (N'nvarchar', N'varchar', N'nchar', N'char')
BEGIN
    -- Text already holds base64: widening keeps every value unchanged.
    SET @sql = N'ALTER TABLE ' + @full + N' ALTER COLUMN ' + QUOTENAME(@column) + N' nvarchar(max) NULL;';
    EXEC sys.sp_executesql @sql;
    PRINT @full + N'.' + QUOTENAME(@column) + N': ' + @type + N' widened to nvarchar(max).';
    RETURN;
END;

IF @type NOT IN (N'varbinary', N'binary', N'image')
BEGIN
    RAISERROR(N'%s.%s has type %s; only varbinary/binary/image or text columns are converted.', 16, 1, @full, @column, @type);
    RETURN;
END;

-- Binary: add a text column, fill it with base64 in batches, then swap the columns.
DECLARE @tmp sysname = @column + N'__b64';
IF COL_LENGTH(@full, @tmp) IS NULL
BEGIN
    SET @sql = N'ALTER TABLE ' + @full + N' ADD ' + QUOTENAME(@tmp) + N' nvarchar(max) NULL;';
    EXEC sys.sp_executesql @sql;
END;

-- XML base64 encoding works on every supported version (BASE64_ENCODE needs SQL Server 2025 / Azure SQL).
SET @sql = N'
DECLARE @n int = 1;
WHILE @n > 0
BEGIN
    UPDATE TOP (1000) t
    SET ' + QUOTENAME(@tmp) + N' = CAST(N'''' AS xml).value(''xs:base64Binary(sql:column("t.' + @column + N'"))'', ''nvarchar(max)'')
    FROM ' + @full + N' AS t
    WHERE t.' + QUOTENAME(@column) + N' IS NOT NULL AND t.' + QUOTENAME(@tmp) + N' IS NULL;
    SET @n = @@ROWCOUNT;
END;';
EXEC sys.sp_executesql @sql;

-- Safety check before dropping anything: every non-empty value must decode back to the original bytes.
DECLARE @bad int;
SET @sql = N'SELECT @bad = COUNT(*) FROM ' + @full + N' AS t
WHERE (t.' + QUOTENAME(@column) + N' IS NULL AND t.' + QUOTENAME(@tmp) + N' IS NOT NULL)
   OR (t.' + QUOTENAME(@column) + N' IS NOT NULL AND (t.' + QUOTENAME(@tmp) + N' IS NULL
       OR CAST(N'''' AS xml).value(''xs:base64Binary(sql:column("t.' + @tmp + N'"))'', ''varbinary(max)'') <> CAST(t.' + QUOTENAME(@column) + N' AS varbinary(max))));';
EXEC sys.sp_executesql @sql, N'@bad int OUTPUT', @bad = @bad OUTPUT;
IF @bad > 0
BEGIN
    RAISERROR(N'%d row(s) did not round-trip; the original column is kept. Inspect %s.', 16, 1, @bad, @tmp);
    RETURN;
END;

BEGIN TRANSACTION;
    SET @sql = N'ALTER TABLE ' + @full + N' DROP COLUMN ' + QUOTENAME(@column) + N';';
    EXEC sys.sp_executesql @sql;
    DECLARE @old nvarchar(400) = @schema + N'.' + @table + N'.' + @tmp;
    EXEC sys.sp_rename @old, @column, N'COLUMN';
COMMIT TRANSACTION;

PRINT @full + N'.' + QUOTENAME(@column) + N': ' + @type + N' converted to nvarchar(max) (base64), all values verified.';
