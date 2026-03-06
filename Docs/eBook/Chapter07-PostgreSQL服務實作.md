# PostgreSQL 服務實作

`PostgreSqlService` 是整個系統中最龐大的服務（約 943 行），因為 PostgreSQL 的系統目錄結構與 SQL Server 截然不同，且 PostgreSQL 的物件類型更加多元（Enum、Composite、Domain 型別、分區表、物化檢視等）。

## pg_catalog 與 information_schema 的選擇

PostgreSQL 提供兩套查詢資料庫中繼資料的方式：

| 方式 | 優點 | 缺點 |
|------|------|------|
| `information_schema` | SQL 標準、可移植性高 | 資訊有限、不含 PostgreSQL 特有功能 |
| `pg_catalog` | 完整資訊、包含所有細節 | PostgreSQL 專有、欄位名稱較不直觀 |

DbCopy 的策略是：**核心查詢使用 `pg_catalog`，輔助查詢視需求選用**。

| 查詢需求 | 使用 | 原因 |
|---------|------|------|
| 物件清單 | `pg_catalog` | 需要 `relkind` 區分分區表/物化檢視 |
| 欄位資訊 | `pg_catalog` (`pg_attribute`) | 需要 `format_type()` 取得完整型別字串 |
| 主鍵資訊 | `information_schema` | 語法較簡單，效能差異不大 |
| 序列資訊 | `information_schema` | 提供了更簡單的查詢方式 |
| 外鍵資訊 | `information_schema` | 查詢語法較直覺 |
| 物件定義 | `pg_catalog` | 需要 `pg_get_functiondef()` 等函數 |

## 排除 Extension 擁有的物件

PostgreSQL 有一個獨特的概念：**Extension**（擴展）。安裝 Extension（如 `postgis`、`uuid-ossp`）會在資料庫中建立物件（函數、型別、運算子等）。這些物件由 Extension 管理，**不應該被複製**。

```sql
-- 排除屬於 Extension 的物件
AND NOT EXISTS (
    SELECT 1
    FROM pg_depend d
    JOIN pg_extension e ON e.oid = d.refobjid
    WHERE d.objid = c.oid AND d.deptype = 'e'
)
```

`pg_depend.deptype = 'e'` 表示「Extension 擁有」。透過這個過濾條件，DbCopy 可以正確地：
- 列出 `postgis` Extension 建立的 `geometry` 型別 → **排除**（由 Extension 管理）
- 列出使用者自己建立的 `PhoneNumber` 型別 → **包含**
- 列出使用 `geometry` 型別的資料表 → **包含**（資料表本身不是 Extension 物件）

## 使用者定義型別的四種子類型

PostgreSQL 的使用者定義型別比 SQL Server 豐富得多，分為四種子類型：

### Enum 型別

```sql
-- PostgreSQL
CREATE TYPE mood AS ENUM ('sad', 'ok', 'happy');
```

DbCopy 從 `pg_enum` 表取得列舉值：

```csharp
var enumValues = await conn.QueryWithLogAsync<string>(logger, """
    SELECT e.enumlabel
    FROM pg_type t
    JOIN pg_enum e ON t.oid = e.enumtypid
    JOIN pg_namespace n ON n.oid = t.typnamespace
    WHERE t.typname = @Name AND n.nspname = @Schema
    ORDER BY e.enumsortorder
    """, new { obj.Name, obj.Schema });

if (enumList.Count != 0)
    return $"CREATE TYPE \"{obj.Schema}\".\"{obj.Name}\" AS ENUM " +
           $"('{string.Join("', '", enumList)}')";
```

注意 `ORDER BY e.enumsortorder`——Enum 值的順序很重要，因為 PostgreSQL 的 Enum 是有序的，可以做大小比較。

### Composite 型別（複合型別）

```sql
-- PostgreSQL
CREATE TYPE address AS (
    street TEXT,
    city TEXT,
    zip_code VARCHAR(10)
);
```

```csharp
var compositeCols = await conn.QueryWithLogAsync(logger, """
    SELECT a.attname as Name, format_type(a.atttypid, a.atttypmod) as DataType
    FROM pg_type t
    JOIN pg_class c ON t.typrelid = c.oid
    JOIN pg_attribute a ON a.attrelid = c.oid
    JOIN pg_namespace n ON n.oid = t.typnamespace
    WHERE t.typname = @Name AND n.nspname = @Schema
      AND a.attnum > 0 AND NOT a.attisdropped
    ORDER BY a.attnum
    """, new { obj.Name, obj.Schema });
```

`format_type(a.atttypid, a.atttypmod)` 是 PostgreSQL 提供的神器函數——它會自動將型別 OID 和修飾符轉換為完整的型別字串（如 `character varying(100)`、`numeric(10,2)`），省去了我們手動拼湊的麻煩。

### Domain 型別

```sql
-- PostgreSQL
CREATE DOMAIN positive_integer AS integer CHECK (VALUE > 0);
```

```csharp
var domainInfo = await conn.QueryFirstOrDefaultWithLogAsync(logger, """
    SELECT format_type(typbasetype, typtypmod) as BaseType,
           typnotnull, typdefault
    FROM pg_type t
    JOIN pg_namespace n ON n.oid = t.typnamespace
    WHERE t.typname = @Name AND n.nspname = @Schema AND t.typtype = 'd'
    """, new { obj.Name, obj.Schema });
```

### Base 型別（Shell 定義）

完整的 Base 型別需要 C 語言函數，DbCopy 僅產生 Shell 宣告：

```csharp
return $"CREATE TYPE \"{obj.Schema}\".\"{obj.Name}\"";
```

### 自動偵測型別子類型

DbCopy 透過**逐一嘗試**的方式偵測型別子類型，而非事先查詢 `typtype`。程式碼邏輯：

```
嘗試 Enum 查詢 → 有結果？→ 回傳 Enum DDL
   ↓ 無結果
嘗試 Composite 查詢 → 有結果？→ 回傳 Composite DDL
   ↓ 無結果
嘗試 Domain 查詢 → 有結果？→ 回傳 Domain DDL
   ↓ 無結果
嘗試 Base 查詢 → 有結果？→ 回傳 Shell DDL
   ↓ 無結果
回傳空字串
```

## COPY 協定：二進位模式與文字模式

PostgreSQL 提供了 `COPY` 命令用於大量資料匯入匯出，效率遠高於逐列 INSERT。Npgsql（.NET 的 PostgreSQL 驅動程式）提供了兩種模式：

### 二進位模式（Binary Format）

```csharp
await using var importer = await targetConn.BeginBinaryImportAsync(
    $"COPY \"{obj.Schema}\".\"{obj.Name}\" ({colList}) FROM STDIN (FORMAT BINARY)");

while (await reader.ReadAsync())
{
    await importer.StartRowAsync();
    for (var i = 0; i < reader.FieldCount; i++)
        await importer.WriteAsync(reader.GetValue(i));
}
await importer.CompleteAsync();
```

**優點**：效能最高，型別對應由 Npgsql 自動處理
**缺點**：不支援某些特殊型別（如空間資料 `geometry`）

### 文字模式（Text Format）

```csharp
await using var writer = await targetConn.BeginTextImportAsync(
    $"COPY \"{obj.Schema}\".\"{obj.Name}\" ({colList}) FROM STDIN");

while (await reader.ReadAsync())
{
    var values = new string[reader.FieldCount];
    for (var i = 0; i < reader.FieldCount; i++)
    {
        var val = reader.GetValue(i);
        if (val == DBNull.Value)
            values[i] = "\\N";           // NULL 表示法
        else if (val is byte[] bytes)
            values[i] = "\\\\x" + BitConverter.ToString(bytes).Replace("-", "");
        else if (val is bool b)
            values[i] = b ? "t" : "f";   // boolean 表示法
        else
        {
            var s = val.ToString() ?? "";
            values[i] = s.Replace("\\", "\\\\")   // 跳脫反斜線
                         .Replace("\t", "\\t")     // 跳脫 Tab
                         .Replace("\n", "\\n")     // 跳脫換行
                         .Replace("\r", "\\r");    // 跳脫回車
        }
    }
    await writer.WriteLineAsync(string.Join("\t", values));
}
```

**優點**：支援所有型別，包括空間資料
**缺點**：需要手動處理跳脫字元

### 模式選擇邏輯

```csharp
var hasGeometry = columns.Any(c =>
    c.data_type.Contains("geometry", ...) ||
    c.data_type.Contains("geography", ...) ||
    c.data_type.Contains("raster", ...) ||
    c.data_type.Contains("USER-DEFINED", ...));

if (hasGeometry)
    // 使用文字模式，以 ST_AsEWKT() 序列化空間資料
else
    // 使用二進位模式（效率更高）
```

## 空間資料的特殊處理

當資料表包含空間欄位（`geometry`、`geography`、`raster`）時，DbCopy 會：

1. **讀取時**：使用 `ST_AsEWKT()` 將空間資料轉換為 EWKT 文字格式
2. **寫入時**：透過文字模式 COPY，讓 PostgreSQL 自動從 EWKT 解析

```csharp
var selectListForRead = string.Join(", ", columns.Select(c =>
{
    bool isSpatial = dt.Contains("geometry", ...) || ...;
    return isSpatial
        ? $"ST_AsEWKT(\"{c.column_name}\") AS \"{c.column_name}\""  // 轉為文字
        : $"\"{c.column_name}\"";                                     // 原樣讀取
}));
```

## Extension 自動偵測與安裝

`EnsureExtensionsIfUsedAsync` 是 PostgreSQL 服務獨有的方法。它會分析來源資料表使用的型別和索引，自動在目標資料庫安裝所需的 Extension：

```csharp
private async Task EnsureExtensionsIfUsedAsync(NpgsqlConnection targetConn,
    string sourceConnectionString, DbObject obj)
{
    var extsToEnsure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // 1. 欄位型別偵測
    // geometry/geography/raster → postgis
    // citext → citext
    // hstore → hstore

    // 2. 預設值偵測
    // uuid_generate_* → uuid-ossp

    // 3. 索引運算子類別偵測
    // gin_trgm_ops/gist_trgm_ops → pg_trgm

    foreach (var ext in extsToEnsure)
    {
        var exists = await targetConn.ExecuteScalarWithLogAsync<bool?>(logger,
            "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = @ext)",
            new { ext }) ?? false;
        if (!exists)
        {
            await targetConn.ExecuteWithLogAsync(logger,
                $"CREATE EXTENSION IF NOT EXISTS \"{ext}\"");
        }
    }
}
```

### 偵測規則

| 偵測條件 | 需要的 Extension |
|---------|----------------|
| 欄位型別含 `geometry` / `geography` / `raster` | `postgis` |
| 欄位型別為 `citext` | `citext` |
| 欄位型別為 `hstore` | `hstore` |
| 欄位預設值含 `uuid_generate_*` | `uuid-ossp` |
| 索引使用 `gin_trgm_ops` / `gist_trgm_ops` | `pg_trgm` |

## 序列值重設

PostgreSQL 的序列（Sequence）等同於 SQL Server 的 IDENTITY。資料複製完成後需要重設序列值：

```csharp
// 查詢與此資料表欄位關聯的所有序列
const string seqSql = """
    SELECT s.relname AS SequenceName, n.nspname AS SequenceSchema,
           a.attname AS ColumnName
    FROM pg_class s
    JOIN pg_depend d ON d.objid = s.oid
    JOIN pg_class t ON d.refobjid = t.oid
    JOIN pg_namespace n ON n.oid = s.relnamespace
    JOIN pg_namespace tn ON tn.oid = t.relnamespace
    JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = d.refobjsubid
    WHERE s.relkind = 'S' AND t.relname = @Name AND tn.nspname = @Schema
    """;

foreach (var seq in sequences)
{
    var setValSql = $"""
        DO $$
        DECLARE max_val bigint;
        BEGIN
            EXECUTE format('SELECT MAX(%I) FROM %I.%I',
                '{escapedCol}', '{escapedSchema}', '{escapedName}') INTO max_val;
            IF max_val IS NULL THEN
                PERFORM setval(format('%I.%I', '{escapedSeqSchema}', '{escapedSeqName}'),
                    1, false);
            ELSE
                PERFORM setval(format('%I.%I', '{escapedSeqSchema}', '{escapedSeqName}'),
                    max_val, true);
            END IF;
        END $$;
        """;
    await targetConn.ExecuteWithLogAsync(logger, setValSql);
}
```

### 設計要點

**使用 PL/pgSQL 匿名區塊（`DO $$...$$`）**

為什麼不直接用 `SELECT setval(..., (SELECT MAX(...)))`？因為當資料表為空時，`MAX()` 回傳 NULL，`setval(seq, NULL)` 會拋出錯誤。使用匿名區塊可以先判斷是否為 NULL，空表時使用 `setval(seq, 1, false)`（重設為起始值但尚未使用）。

**`format('%I', ...)` 防止注入**

在 PL/pgSQL 中，`format('%I', identifier)` 等同於 `QUOTENAME()` / `"identifier"`，會自動為識別符加上雙引號並處理跳脫。

**`EscapePgLiteral()` 處理字串常值中的單引號**

```csharp
private static string EscapePgLiteral(string s) => s.Replace("'", "''");
```

傳入 `format()` 的參數是字串常值，需要跳脫單引號。

---

> **下一章預告**：服務層的兩大實作已經完成。第 8 章將介紹 API 控制器層——如何將服務層的功能暴露為 RESTful API，以及平行化查詢的策略。
