# SQL Server 服務實作

`SqlServerService` 是整個系統中程式碼量最大的服務之一（669 行），因為它需要處理 SQL Server 七種不同物件類型的探索、定義重建、相依性分析、資料複製、索引建立和外鍵建立。

## 連線測試

```csharp
public async Task<bool> TestConnectionAsync(string connectionString)
{
    await using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();
    return true;
}
```

最簡單的方法——嘗試開啟連線。如果連線字串有誤、伺服器不可達、帳密錯誤，`OpenAsync()` 會拋出 `SqlException`。

**`await using` 的重要性**：確保連線物件在方法結束時被正確釋放（即使發生例外）。

## 物件清單查詢 — sys.* 目錄檢視

```csharp
public async Task<List<DbObject>> GetDbObjectsAsync(string connectionString)
{
    await using var conn = new SqlConnection(connectionString);
    const string sql = """
        SELECT SCHEMA_NAME(schema_id) AS [Schema], name AS Name, 'Table' AS Type
          FROM sys.tables WHERE is_ms_shipped = 0
        UNION ALL
        SELECT SCHEMA_NAME(schema_id) AS [Schema], name AS Name, 'View' AS Type
          FROM sys.views WHERE is_ms_shipped = 0
        UNION ALL
        SELECT SCHEMA_NAME(schema_id) AS [Schema], name AS Name, 'Procedure' AS Type
          FROM sys.procedures WHERE is_ms_shipped = 0
        UNION ALL
        SELECT SCHEMA_NAME(schema_id) AS [Schema], name AS Name, 'Function' AS Type
          FROM sys.objects WHERE type IN ('FN', 'IF', 'TF') AND is_ms_shipped = 0
        UNION ALL
        SELECT SCHEMA_NAME(schema_id) AS [Schema], name AS Name, 'UserDefinedType' AS Type
          FROM sys.types WHERE is_user_defined = 1 AND is_table_type = 0
        UNION ALL
        SELECT SCHEMA_NAME(schema_id) AS [Schema], name AS Name, 'UserDefinedTableType' AS Type
          FROM sys.table_types WHERE is_user_defined = 1
        UNION ALL
        SELECT SCHEMA_NAME(schema_id) AS [Schema], name AS Name, 'Sequence' AS Type
          FROM sys.sequences
        """;

    var results = await conn.QueryWithLogAsync(logger, sql);
    return results.Select(r => new DbObject
    {
        Schema = r.Schema,
        Name = r.Name,
        Type = Enum.Parse<DbObjectType>(r.Type)
    }).ToList();
}
```

### SQL Server 系統目錄速覽

| 系統檢視 | 內容 | 過濾條件 |
|---------|------|---------|
| `sys.tables` | 使用者資料表 | `is_ms_shipped = 0` 排除系統表 |
| `sys.views` | 檢視 | `is_ms_shipped = 0` 排除系統檢視 |
| `sys.procedures` | 預存程序 | `is_ms_shipped = 0` 排除系統程序 |
| `sys.objects` | 所有物件 | `type IN ('FN','IF','TF')` 篩選函數 |
| `sys.types` | 型別 | `is_user_defined = 1` 篩選自訂型別 |
| `sys.table_types` | 資料表型別 | `is_user_defined = 1` 篩選自訂 |
| `sys.sequences` | 序列 | 無額外過濾（序列都是使用者定義的） |

### 函數的三種子類型

SQL Server 的函數分為三種：
- `FN`：純量函數（Scalar Function）
- `IF`：行內資料表值函數（Inline Table-valued Function）
- `TF`：多陳述式資料表值函數（Multi-statement Table-valued Function）

在 DbCopy 中，我們將這三種都歸類為 `Function` 類型，不做細分。

### Type 欄位的字串映射

注意 SQL 查詢中 `Type` 欄位使用的是字串（如 `'Table'`, `'View'`），然後用 `Enum.Parse<DbObjectType>(r.Type)` 轉換。這要求字串值必須與 `DbObjectType` 列舉的成員名稱完全一致。

## DDL 定義重建

`GetObjectDefinitionAsync` 是最複雜的方法，因為不同的物件類型需要完全不同的 DDL 重建策略：

```csharp
public async Task<string> GetObjectDefinitionAsync(string connectionString, DbObject obj)
{
    await using var conn = new SqlConnection(connectionString);
    if (obj.Type == DbObjectType.Table)
        return await GetTableDefinitionAsync(conn, obj);
    if (obj.Type == DbObjectType.UserDefinedType)
        return await GetUserDefinedTypeDefinitionAsync(conn, obj);
    if (obj.Type == DbObjectType.UserDefinedTableType)
        return await GetUserDefinedTableTypeDefinitionAsync(conn, obj);
    if (obj.Type == DbObjectType.Sequence)
        return await GetSequenceDefinitionAsync(conn, obj);

    // 檢視、程序、函數：使用 OBJECT_DEFINITION()
    const string sql =
        "SELECT OBJECT_DEFINITION(OBJECT_ID(QUOTENAME(@Schema) + '.' + QUOTENAME(@Name)))";
    return await conn.ExecuteScalarWithLogAsync<string>(logger, sql,
        new { obj.Schema, obj.Name }) ?? "";
}
```

### 為什麼有些物件可以用 OBJECT_DEFINITION()，有些不行？

SQL Server 的 `OBJECT_DEFINITION()` 函數可以取得以下物件的原始 SQL 定義文字：
- 檢視（View）
- 預存程序（Procedure）
- 函數（Function）

但以下物件**不被 OBJECT_DEFINITION() 支援**：
- 資料表（Table）——因為資料表沒有「定義文字」，它的結構分散在 `sys.columns`、`sys.indexes` 等多個目錄中
- 使用者定義型別（UDT）——型別資訊存在 `sys.types` 中
- 序列（Sequence）——序列資訊存在 `sys.sequences` 中

### 資料表定義重建

資料表的 DDL 重建是最複雜的，需要查詢三個系統檢視：

```csharp
private async Task<string> GetTableDefinitionAsync(SqlConnection conn, DbObject obj)
{
    // 1. 查詢所有欄位
    var columns = await conn.QueryWithLogAsync(logger, """
        SELECT c.COLUMN_NAME, c.DATA_TYPE, c.CHARACTER_MAXIMUM_LENGTH,
               c.IS_NULLABLE,
               COLUMNPROPERTY(..., 'IsIdentity') AS IsIdentity
        FROM INFORMATION_SCHEMA.COLUMNS c
        WHERE c.TABLE_SCHEMA = @Schema AND c.TABLE_NAME = @Name
        ORDER BY c.ORDINAL_POSITION
        """, new { obj.Schema, obj.Name });

    // 2. 查詢主鍵條件約束名稱
    var pkInfo = await conn.QueryFirstOrDefaultWithLogAsync(logger, """
        SELECT tc.CONSTRAINT_NAME
        FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
        WHERE ... AND tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
        """, new { obj.Schema, obj.Name });

    // 3. 查詢主鍵欄位清單
    var pkColumns = await conn.QueryWithLogAsync<string>(logger, """
        SELECT k.COLUMN_NAME
        FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
        ...
        ORDER BY k.ORDINAL_POSITION
        """, new { obj.Schema, obj.Name });

    // 4. 組裝 CREATE TABLE 語句
    return $"CREATE TABLE [{obj.Schema}].[{obj.Name}] (\n  {columnDefs}{pkSql}\n)";
}
```

**重點設計**：

- 使用 `INFORMATION_SCHEMA` 而非 `sys.columns`，因為 `INFORMATION_SCHEMA.COLUMNS.CHARACTER_MAXIMUM_LENGTH` 已經是字元數，不需要再做位元組換算。
- IDENTITY 欄位透過 `COLUMNPROPERTY()` 函數偵測。
- 主鍵條件約束保留原始名稱（如 `PK_Users`），確保在目標資料庫中保持一致。

### 序列定義重建

```csharp
private async Task<string> GetSequenceDefinitionAsync(SqlConnection conn, DbObject obj)
{
    var seqInfo = await conn.QueryFirstOrDefaultWithLogAsync(logger, """
        SELECT t.name AS DataType, s.start_value AS StartValue,
               s.increment AS Increment, s.minimum_value AS MinimumValue,
               s.maximum_value AS MaximumValue, s.is_cycling AS IsCycling
        FROM sys.sequences s
        JOIN sys.types t ON s.user_type_id = t.user_type_id
        WHERE SCHEMA_NAME(s.schema_id) = @Schema AND s.name = @Name
        """, new { obj.Schema, obj.Name });

    return $"CREATE SEQUENCE [{obj.Schema}].[{obj.Name}] " +
           $"AS [{seqInfo.DataType}] " +
           $"START WITH {seqInfo.StartValue} " +
           $"INCREMENT BY {seqInfo.Increment} " +
           $"MINVALUE {seqInfo.MinimumValue} " +
           $"MAXVALUE {seqInfo.MaximumValue} " +
           (seqInfo.IsCycling ? "CYCLE" : "NO CYCLE") + ";";
}
```

## 型別長度的位元組與字元換算

SQL Server 的系統目錄中，`max_length` 是以**位元組**為單位存儲的。這在處理 Unicode 型別時會造成混淆：

| 型別 | DDL 定義 | sys.columns.max_length |
|------|---------|----------------------|
| `varchar(50)` | 50 字元 | 50 位元組 |
| `nvarchar(50)` | 50 字元 | **100 位元組** |
| `char(10)` | 10 字元 | 10 位元組 |
| `nchar(10)` | 10 字元 | **20 位元組** |
| `varbinary(100)` | 100 位元組 | 100 位元組 |
| `varchar(MAX)` | 不限 | **-1** |

`FormatSqlServerTypeLength` 方法處理這個換算：

```csharp
private static readonly string[] TypesWithByteLength =
    ["varchar", "nvarchar", "char", "nchar", "varbinary", "binary"];

private static string FormatSqlServerTypeLength(string typeName, int maxLengthBytes)
{
    if (!TypesWithByteLength.Contains(typeName.ToLower())) return "";
    var charLength = typeName.StartsWith("n", StringComparison.OrdinalIgnoreCase)
        ? maxLengthBytes / 2    // Unicode：除以 2
        : maxLengthBytes;       // 非 Unicode：直接使用
    return charLength == -1 ? "(MAX)" : $"({charLength})";
}
```

### 注意：兩個不同的長度來源

在 DbCopy 中，長度資訊來自兩個不同的地方：

1. **`sys.columns.max_length`**（位元組）——用於 `GetUserDefinedTypeDefinitionAsync` 和 `GetUserDefinedTableTypeDefinitionAsync`
2. **`INFORMATION_SCHEMA.COLUMNS.CHARACTER_MAXIMUM_LENGTH`**（字元數）——用於 `GetTableDefinitionAsync`

使用 `INFORMATION_SCHEMA` 時**不需要**除以 2，因為它已經是字元數。使用 `sys.columns` 時才需要透過 `FormatSqlServerTypeLength` 換算。

## 相依性分析

```csharp
public async Task<List<string>> GetDependenciesAsync(string connectionString, DbObject obj)
{
    await using var conn = new SqlConnection(connectionString);

    // 1. 表達式層級的依賴（FROM, JOIN, EXEC 等）
    const string sql = """
        SELECT DISTINCT
            COALESCE(referenced_schema_name, SCHEMA_NAME(o.schema_id), 'dbo')
            + '.' + referenced_entity_name
        FROM sys.sql_expression_dependencies sed
        LEFT JOIN sys.objects o ON sed.referenced_id = o.object_id
        WHERE referencing_id = OBJECT_ID(QUOTENAME(@Schema) + '.' + QUOTENAME(@Name))
          AND referenced_entity_name IS NOT NULL
        """;
    var results = (await conn.QueryWithLogAsync<string>(logger, sql, ...)).ToList();

    // 2. 欄位型別的 UDT 依賴
    const string udtSql = """
        SELECT DISTINCT SCHEMA_NAME(t.schema_id) + '.' + t.name
        FROM sys.columns c
        JOIN sys.types t ON c.user_type_id = t.user_type_id
        WHERE c.object_id = OBJECT_ID(...) AND t.is_user_defined = 1
        """;
    results.AddRange(await conn.QueryWithLogAsync<string>(logger, udtSql, ...));

    // 3. 參數型別的 UDT/UDTT 依賴（僅程序和函數）
    if (obj.Type is DbObjectType.Procedure or DbObjectType.Function)
    {
        const string paramSql = """
            SELECT DISTINCT SCHEMA_NAME(t.schema_id) + '.' + t.name
            FROM sys.parameters p
            JOIN sys.types t ON p.user_type_id = t.user_type_id
            WHERE p.object_id = OBJECT_ID(...) AND t.is_user_defined = 1
            """;
        results.AddRange(await conn.QueryWithLogAsync<string>(logger, paramSql, ...));
    }

    return results.Distinct().ToList();
}
```

### 依賴分析的三個維度

1. **表達式依賴**（`sys.sql_expression_dependencies`）：記錄了 SQL 程式碼中引用的所有物件。例如，檢視 `SELECT * FROM Users` 會記錄對 `Users` 的依賴。

2. **欄位型別依賴**（`sys.columns` + `sys.types`）：如果資料表的某個欄位型別是使用者定義型別，`sys.sql_expression_dependencies` 不會記錄這種依賴，需要額外查詢。

3. **參數型別依賴**（`sys.parameters` + `sys.types`）：預存程序的參數如果使用了使用者定義型別或資料表型別，也需要額外查詢。

## SqlBulkCopy 大量資料複製

```csharp
private async Task CopyTableDataAsync(string sourceConnectionString,
    string targetConnectionString, DbObject obj, int batchSize)
{
    await using var sourceConn = new SqlConnection(sourceConnectionString);
    await sourceConn.OpenAsync();

    var sql = $"SELECT * FROM [{obj.Schema.Replace("]", "]]")}].[{obj.Name.Replace("]", "]]")}]";
    await using var cmd = new SqlCommand(sql, sourceConn);
    LogExecutingSqlSql(logger, sql);  // 手動記錄 SQL
    await using var reader = await cmd.ExecuteReaderAsync();

    await using var targetConn = new SqlConnection(targetConnectionString);
    await targetConn.OpenAsync();

    using var bulkCopy = new SqlBulkCopy(targetConn,
        SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls, null);
    bulkCopy.DestinationTableName =
        $"[{obj.Schema.Replace("]", "]]")}].[{obj.Name.Replace("]", "]]")}]";
    bulkCopy.BulkCopyTimeout = 600;  // 10 分鐘
    bulkCopy.BatchSize = batchSize;

    await bulkCopy.WriteToServerAsync(reader);
    ...
}
```

### SqlBulkCopy 的關鍵選項

| 選項 | 說明 |
|------|------|
| `KeepIdentity` | 保留來源的 IDENTITY 值，不讓目標自動產生新值 |
| `KeepNulls` | 保留 NULL 值，不使用目標欄位的預設值替代 |
| `BulkCopyTimeout = 600` | 10 分鐘逾時（大型資料表需要較長時間） |
| `BatchSize` | 每批次寫入的列數（預設 1000） |

### 串流模式

注意資料複製使用的是**串流模式**：`SqlDataReader` 逐列讀取來源資料，`SqlBulkCopy` 逐批次寫入目標。整個過程不需要將所有資料載入記憶體，因此即使是數百萬列的大型資料表也不會造成記憶體不足。

## IDENTITY 欄位重設

```csharp
// 檢查是否有 IDENTITY 欄位
var hasIdentity = await targetConn.ExecuteScalarWithLogAsync<int?>(logger,
    "SELECT 1 FROM sys.identity_columns WHERE object_id = OBJECT_ID(...)",
    new { obj.Schema, obj.Name }) != null;

if (hasIdentity)
{
    var reseedSql =
        $"DBCC CHECKIDENT ('[{obj.Schema}].[{obj.Name}]', RESEED)";
    LogExecutingSqlSql(logger, reseedSql);
    await using var reseedCmd = new SqlCommand(reseedSql, targetConn);
    await reseedCmd.ExecuteNonQueryAsync();
}
```

### 為什麼需要重設 IDENTITY？

當使用 `KeepIdentity` 選項複製資料時，目標資料表的 IDENTITY 種子值不會自動更新。如果最後一筆資料的 ID 是 1000，但 IDENTITY 種子值仍然是 1，後續的 INSERT 就會嘗試插入 ID=1，導致主鍵衝突。

`DBCC CHECKIDENT(table, RESEED)` 會將種子值重設為資料表中 IDENTITY 欄位的最大值。

## 索引與外鍵的建立

### 索引定義提取

```csharp
private async Task<List<string>> GetTableIndexDefinitionsAsync(...)
{
    var rows = await conn.QueryWithLogAsync(logger, """
        SELECT i.name AS IndexName, i.is_unique AS IsUnique,
               c.name AS ColumnName, ic.is_included_column AS IsIncluded,
               ic.key_ordinal AS KeyOrdinal
        FROM sys.indexes i
        JOIN sys.index_columns ic ON ...
        JOIN sys.columns c ON ...
        WHERE i.object_id = OBJECT_ID(...) AND i.type > 0 AND i.is_primary_key = 0
        ORDER BY i.name, ic.is_included_column, ic.key_ordinal
        """, new { obj.Schema, obj.Name });

    // 以索引名稱分組，區分 key columns 和 include columns
    var grouped = rows.GroupBy(r => new { Name = r.IndexName, IsUnique = r.IsUnique });

    foreach (var g in grouped)
    {
        var keyCols = g.Where(r => !r.IsIncluded).Select(r => $"[{r.ColumnName}]");
        var incCols = g.Where(r => r.IsIncluded).Select(r => $"[{r.ColumnName}]");
        var includeSql = incCols.Any() ? $" INCLUDE ({string.Join(", ", incCols)})" : "";
        sqls.Add($"CREATE {(g.Key.IsUnique ? "UNIQUE " : "")}INDEX [{g.Key.Name}] " +
                 $"ON [{obj.Schema}].[{obj.Name}] ({string.Join(", ", keyCols)}){includeSql}");
    }
}
```

**關鍵設計**：
- `i.type > 0` 排除 Heap 虛擬索引
- `i.is_primary_key = 0` 排除主鍵索引（主鍵已在 Phase 1 的 CREATE TABLE 中建立）
- `is_included_column` 區分索引鍵欄和 INCLUDE 欄

### 外鍵定義提取

外鍵的提取邏輯類似，但需要額外處理多欄位外鍵的情況：

```csharp
// 以外鍵名稱分組
var grouped = fks.GroupBy(f => (string)f.ForeignKeyName);

foreach (var g in grouped)
{
    var cols = string.Join(", ", g.Select(r => $"[{r.ColumnName}]"));
    var refCols = string.Join(", ", g.Select(r => $"[{r.ReferencedColumnName}]"));
    sqls.Add($"ALTER TABLE [{first.SchemaName}].[{first.TableName}] " +
             $"ADD CONSTRAINT [{g.Key}] FOREIGN KEY ({cols}) " +
             $"REFERENCES [{first.ReferencedSchemaName}].[{first.ReferencedTableName}] ({refCols})");
}
```

### 容錯設計

索引和外鍵的建立採用「盡力而為」策略：

```csharp
// 索引：失敗記錄警告，繼續下一個
catch (Exception ex)
{
    logger.LogWarning(ex, "Failed to create index for {Schema}.{Name}", ...);
}

// 外鍵：特別處理「參考物件不存在」的錯誤
catch (SqlException ex) when (ex.Number == 4902)
{
    logger.LogInformation("Skipped foreign key - referenced object does not exist (Error 4902)");
}
```

Error 4902 是一個特別常見的情況：當複製部分資料表時，外鍵參考的資料表可能尚未存在於目標資料庫中。這不是一個錯誤，而是預期的行為。

---

> **下一章預告**：同樣的九個方法，在 PostgreSQL 上的實作完全不同。第 7 章將介紹 PostgreSQL 服務——從 pg_catalog 目錄查詢到 COPY 協定的大量資料載入。
