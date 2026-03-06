# Dapper 擴充方法與結構化日誌

## 為什麼要包裝 Dapper

DbCopy 的所有服務層程式碼都會透過 Dapper 執行 SQL 查詢。在偵錯和維運時，有一個非常重要的需求：**我想要在日誌中看到每一條被執行的 SQL**。

如果不做包裝，每個查詢呼叫前都需要手動寫一行日誌：

```csharp
// 不好的做法：每個呼叫前都要記得寫 log
logger.LogInformation("Executing SQL: {Sql}", sql);
var results = await conn.QueryAsync<string>(sql, param);
```

這有三個問題：
1. **容易遺漏**：開發者可能忘記加日誌
2. **重複程式碼**：相同的 log 邏輯散布在整個程式碼庫
3. **格式不一致**：不同開發者可能用不同的日誌格式

解決方案：**建立擴充方法，將日誌邏輯封裝在 Dapper 呼叫的外面**。

## DapperExtensions 的設計

完整的 `DapperExtensions.cs`（80 行）提供了五個包裝方法：

```csharp
public static partial class DapperExtensions
{
    // 強型別查詢
    public static Task<IEnumerable<T>> QueryWithLogAsync<T>(
        this IDbConnection conn, ILogger logger, string sql, object? param = null)
    {
        logger.LogExecutingSqlSql(sql);
        return conn.QueryAsync<T>(sql, param);
    }

    // 動態查詢（回傳 dynamic）
    public static Task<IEnumerable<dynamic>> QueryWithLogAsync(
        this IDbConnection conn, ILogger logger, string sql, object? param = null)
    {
        logger.LogExecutingSqlSql(sql);
        return conn.QueryAsync(sql, param);
    }

    // 單列查詢
    public static Task<dynamic?> QueryFirstOrDefaultWithLogAsync(
        this IDbConnection conn, ILogger logger, string sql, object? param = null)
    {
        logger.LogExecutingSqlSql(sql);
        return conn.QueryFirstOrDefaultAsync(sql, param);
    }

    // 純量查詢
    public static Task<T?> ExecuteScalarWithLogAsync<T>(
        this IDbConnection conn, ILogger logger, string sql, object? param = null)
    {
        logger.LogExecutingSqlSql(sql);
        return conn.ExecuteScalarAsync<T>(sql, param);
    }

    // 非查詢執行（INSERT/UPDATE/DELETE/DDL）
    public static Task<int> ExecuteWithLogAsync(
        this IDbConnection conn, ILogger logger, string sql, object? param = null)
    {
        logger.LogExecutingSqlSql(sql);
        return conn.ExecuteAsync(sql, param);
    }

    [LoggerMessage(LogLevel.Information, "Executing SQL: {Sql}")]
    static partial void LogExecutingSqlSql(this ILogger logger, string Sql);
}
```

### 設計要點

**擴充方法（Extension Method）**

透過 `this IDbConnection conn` 讓所有 `IDbConnection` 物件都能直接呼叫這些方法：

```csharp
// 使用前：直接呼叫 Dapper
await conn.QueryAsync<string>(sql, param);

// 使用後：透過擴充方法呼叫
await conn.QueryWithLogAsync<string>(logger, sql, param);
```

差異只在方法名稱和多一個 `logger` 參數，呼叫端幾乎不需要改變習慣。

**額外傳入 `ILogger` 參數**

你可能會問：為什麼不在擴充方法內部自己建立 Logger？

答案是 **依賴注入原則**。每個 Service 類別都有自己的 `ILogger<T>`，日誌訊息會標記為來自 `SqlServerService` 或 `PostgreSqlService`。如果在擴充方法內部建立 Logger，所有日誌都會變成來自 `DapperExtensions`，失去了來源追蹤的能力。

**不包裝回傳值**

擴充方法直接 `return` Dapper 的回傳值，不做任何額外處理。這確保：
- 零效能損耗
- 保留 Dapper 的原始行為
- 異常（Exception）自然向上傳播

## LoggerMessage 原始碼產生器

檔案最底部有一行看似普通的程式碼，實際上蘊含了 .NET 的高效能日誌技術：

```csharp
[LoggerMessage(LogLevel.Information, "Executing SQL: {Sql}")]
static partial void LogExecutingSqlSql(this ILogger logger, string Sql);
```

### 為什麼不直接用 `logger.LogInformation()`？

```csharp
// 方式 A：傳統做法
logger.LogInformation("Executing SQL: {Sql}", sql);

// 方式 B：LoggerMessage 原始碼產生器
LogExecutingSqlSql(logger, sql);
```

兩者的功能完全相同，但效能有顯著差異。

**方式 A 的問題**：
1. 每次呼叫都需要解析格式字串
2. 參數需要 boxing（如果是值型別）
3. 即使 Information 等級未啟用，仍然會分配字串

**方式 B 的優勢**：
1. 編譯時產生最佳化的程式碼
2. 先檢查日誌等級是否啟用，未啟用時完全跳過
3. 避免不必要的記憶體分配

對於 DbCopy 這種會執行大量 SQL 的工具，這個效能差異是有意義的。

### partial class 與 partial method

注意 `DapperExtensions` 類別宣告為 `partial class`，`LogExecutingSqlSql` 方法宣告為 `static partial void`。這是 C# 原始碼產生器（Source Generator）的必要語法：

- **編譯器**看到 `[LoggerMessage]` 屬性，自動在另一個 partial 檔案中產生完整的方法實作
- **開發者**只需要宣告方法簽名和日誌模板

## 確保每條 SQL 恰好記錄一次

DapperExtensions 的設計遵循一個嚴格的規則：

> **設計原則：所有 Service 類別必須使用這些 wrapper，不得直接呼叫 Dapper，如此每一次 SQL 往返恰好在日誌中出現一次，不需在呼叫端重複 log。**

這個規則確保了日誌的**完整性**和**無重複性**：

| 情境 | 日誌行為 |
|------|---------|
| 透過 DapperExtensions 呼叫 | 自動記錄一次 |
| 直接呼叫 Dapper | 不會記錄（違反規則！） |
| 使用 SqlCommand/NpgsqlCommand | 需要手動呼叫 LogExecutingSqlSql |

### 繞過 Dapper 的情況

有些操作無法透過 Dapper 完成，必須直接使用 ADO.NET 的 `SqlCommand` 或 `NpgsqlCommand`：

- **SQL Server 的 SqlBulkCopy**：大量資料載入不走 Dapper
- **PostgreSQL 的 COPY 協定**：二進位資料匯入不走 Dapper
- **SQL Server 的 DBCC CHECKIDENT**：某些系統命令需要直接執行

在這些情況下，Service 類別會定義自己的 `LogExecutingSqlSql` 方法：

```csharp
// SqlServerService.cs
public partial class SqlServerService(ILogger<SqlServerService> logger) : IDbService
{
    // 獨立的 LoggerMessage 定義，用於繞過 Dapper 的程式碼路徑
    [LoggerMessage(LogLevel.Information, "Executing SQL: {Sql}")]
    static partial void LogExecutingSqlSql(ILogger<SqlServerService> logger, string Sql);

    private async Task CopyTableDataAsync(...)
    {
        var sql = $"SELECT * FROM [{obj.Schema}].[{obj.Name}]";
        // 手動記錄 SQL（因為使用 SqlCommand 而非 Dapper）
        LogExecutingSqlSql(logger, sql);
        await using var cmd = new SqlCommand(sql, sourceConn);
        ...
    }
}
```

這種設計讓日誌格式保持一致（`"Executing SQL: {Sql}"`），無論底層是透過 Dapper 還是直接使用 ADO.NET。

### 日誌輸出範例

實際運行時的日誌輸出：

```
[INF] Executing SQL: SELECT SCHEMA_NAME(schema_id) AS [Schema], name AS Name, 'Table' AS Type FROM sys.tables WHERE is_ms_shipped = 0 UNION ALL ...
[INF] Executing SQL: SELECT 1 FROM sys.tables WHERE SCHEMA_NAME(schema_id) = @Schema AND name = @Name
[INF] Executing SQL: SELECT COUNT_BIG(*) FROM [dbo].[Users]
[INF] Executing SQL: SELECT * FROM [dbo].[Users]
[INF] Executing SQL: DBCC CHECKIDENT ('[dbo].[Users]', RESEED)
```

每一條 SQL 恰好出現一次，方便追蹤問題。

---

> **下一章預告**：有了 Dapper 包裝層和統一的日誌機制，接下來就要進入最核心的部分——SQL Server 服務的完整實作。第 6 章將逐一講解如何從 `sys.*` 系統目錄中提取物件定義。
