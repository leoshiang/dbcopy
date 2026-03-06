# 介面導向設計 — IDbService

## 為什麼需要介面

DbCopy 需要同時支援 SQL Server 和 PostgreSQL。這兩個資料庫引擎的語法、系統目錄、大量載入機制都截然不同，但從使用者的角度來看，操作流程是完全相同的：

1. 測試連線
2. 取得物件清單
3. 比較來源與目標
4. 複製物件

**介面（Interface）** 正是為了解決這個問題而存在的。它定義了「做什麼」（What），而不關心「怎麼做」（How）。

```
                    ┌─────────────┐
                    │  IDbService  │  ← 定義契約
                    └──────┬──────┘
                           │
              ┌────────────┼────────────┐
              ↓                         ↓
    ┌──────────────────┐      ┌──────────────────┐
    │ SqlServerService │      │ PostgreSqlService │
    │  (用 sys.* 實作)  │      │  (用 pg_* 實作)   │
    └──────────────────┘      └──────────────────┘
```

### 為什麼不用抽象類別？

抽象類別適合「有共通實作」的場景。但 SQL Server 和 PostgreSQL 的查詢語法幾乎沒有任何重疊：

| 功能 | SQL Server | PostgreSQL |
|------|-----------|------------|
| 系統目錄 | `sys.tables`, `sys.columns` | `pg_class`, `pg_attribute` |
| 物件定義 | `OBJECT_DEFINITION()` | `pg_get_functiondef()` |
| 大量載入 | `SqlBulkCopy` | `COPY (FORMAT BINARY)` |
| 識別符引用 | `[name]` | `"name"` |
| Schema 存在 | `sys.schemas` | `information_schema.schemata` |

兩者之間**沒有可共用的實作邏輯**，所以介面比抽象類別更適合。

## IDbService 的九個方法

```csharp
public interface IDbService
{
    // 連線管理
    Task<bool> TestConnectionAsync(string connectionString);

    // 物件探索
    Task<List<DbObject>> GetDbObjectsAsync(string connectionString);
    Task<string> GetObjectDefinitionAsync(string connectionString, DbObject obj);
    Task<bool> CheckObjectExistsAsync(string connectionString, DbObject obj);

    // Schema 管理
    Task EnsureSchemaExistsAsync(string connectionString, string schema);

    // 相依性分析
    Task<List<string>> GetDependenciesAsync(string connectionString, DbObject obj);

    // 資料表統計
    Task<long> GetRowCountAsync(string connectionString, DbObject obj);
    Task<List<DbIndex>> GetTableIndexesAsync(string connectionString, DbObject obj);

    // 複製操作
    Task CopyObjectAsync(string sourceConnectionString, string targetConnectionString,
        DbObject obj, int phase = 0, int batchSize = 1000);
}
```

## 每個方法的職責與契約

### TestConnectionAsync — 連線測試

```csharp
Task<bool> TestConnectionAsync(string connectionString);
```

**契約**：嘗試開啟連線。成功回傳 `true`；失敗拋出例外（由呼叫端捕捉並顯示錯誤訊息）。

**為什麼回傳 `bool` 而不是 `void`？** 語意上「測試」應該有結果。雖然目前失敗會拋例外，但保留 `bool` 回傳值讓未來可以改為回傳 `false` 而不拋例外。

### GetDbObjectsAsync — 物件清單

```csharp
Task<List<DbObject>> GetDbObjectsAsync(string connectionString);
```

**契約**：回傳資料庫中所有使用者定義的物件，不含系統物件。回傳的 `DbObject` 不包含 `Definition`（延遲載入）。

**這是整個系統的起點**——所有後續操作都基於這個方法的回傳值。

### GetObjectDefinitionAsync — DDL 定義

```csharp
Task<string> GetObjectDefinitionAsync(string connectionString, DbObject obj);
```

**契約**：回傳物件的完整 DDL 定義字串，可直接在目標資料庫執行。回傳空字串表示無法取得定義。

**這是最複雜的方法**——不同的物件類型需要完全不同的 DDL 重建策略。

### CheckObjectExistsAsync — 存在性檢查

```csharp
Task<bool> CheckObjectExistsAsync(string connectionString, DbObject obj);
```

**契約**：檢查物件是否已存在於指定的資料庫中。以物件的 Schema + Name + Type 三個維度精確比對。

### EnsureSchemaExistsAsync — Schema 建立

```csharp
Task EnsureSchemaExistsAsync(string connectionString, string schema);
```

**契約**：確保指定的 Schema 存在。若已存在則不做任何事（冪等性）。對於預設 Schema（SQL Server 的 `dbo`、PostgreSQL 的 `public`）直接跳過。

**為什麼需要這個方法？** 來源資料庫可能有 `hr`、`sales` 等自訂 Schema，目標資料庫可能沒有。在建立物件之前必須先確保 Schema 存在。

### GetDependenciesAsync — 相依性分析

```csharp
Task<List<string>> GetDependenciesAsync(string connectionString, DbObject obj);
```

**契約**：回傳此物件所依賴的其他物件的 FullName 清單。

**涵蓋範圍**：

| 依賴類型 | 範例 |
|---------|------|
| 表達式依賴 | 預存程序 SELECT FROM 某資料表 |
| 欄位型別依賴 | 資料表的欄位使用自訂型別 |
| 參數型別依賴 | 預存程序的參數使用自訂型別 |
| 序列依賴 | 欄位的預設值使用序列 |

### GetRowCountAsync — 資料表行數

```csharp
Task<long> GetRowCountAsync(string connectionString, DbObject obj);
```

**契約**：回傳資料表的精確行數。非資料表物件回傳 0。使用 `COUNT_BIG`（SQL Server）或 `COUNT(*)::bigint`（PostgreSQL）以支援超大型資料表。

### GetTableIndexesAsync — 索引清單

```csharp
Task<List<DbIndex>> GetTableIndexesAsync(string connectionString, DbObject obj);
```

**契約**：回傳資料表的所有索引（包含主鍵索引）。每個索引包含名稱、唯一性、鍵欄位列表。

### CopyObjectAsync — 複製操作

```csharp
Task CopyObjectAsync(string sourceConnectionString, string targetConnectionString,
    DbObject obj, int phase = 0, int batchSize = 1000);
```

**契約**：將物件從來源複製到目標。`phase` 參數控制執行哪些階段：

| phase | 說明 |
|-------|------|
| 0 | 全部階段（1→2→3→4） |
| 1 | 僅結構（CREATE） |
| 2 | 僅資料（Bulk Copy） |
| 3 | 僅索引（CREATE INDEX） |
| 4 | 僅外鍵（ALTER TABLE ADD CONSTRAINT） |

**這是最核心的方法**——它整合了定義取得、結構建立、資料複製、索引建立、外鍵建立等所有操作。

## 如何在未來擴展新的資料庫類型

如果要新增 MySQL 支援，只需要三個步驟：

**步驟一**：擴展 `DbType` 列舉

```csharp
public enum DbType
{
    SqlServer,
    PostgreSql,
    MySql        // 新增
}
```

**步驟二**：建立 `MySqlService` 實作

```csharp
public class MySqlService(ILogger<MySqlService> logger) : IDbService
{
    // 實作九個方法...
}
```

**步驟三**：在 `Program.cs` 註冊服務，在 `DbController` 加入分派邏輯

```csharp
// Program.cs
builder.Services.AddScoped<MySqlService>();

// DbController.cs
private IDbService GetService(DbType type) => type switch
{
    DbType.SqlServer => sqlServerService,
    DbType.PostgreSql => postgreSqlService,
    DbType.MySql => mySqlService,        // 新增
    _ => throw new ArgumentException("Unsupported database type")
};
```

**不需要修改任何現有的服務實作**——這就是介面導向設計的威力。

### 目前架構的一個務實折衷

你可能注意到，`DbController` 直接注入了具體的 `SqlServerService` 和 `PostgreSqlService`，而不是注入 `IEnumerable<IDbService>` 或使用 Service Locator 模式。

這是一個務實的設計決策。對於只有兩個實作的情境，直接注入比使用工廠模式更簡單、更容易理解。如果未來支援的資料庫引擎增加到 5 個以上，再重構為工廠模式也不遲。

---

> **下一章預告**：介面定義了「做什麼」，但每一次「做」都伴隨著 SQL 的執行。第 5 章將介紹 DapperExtensions——一個精巧的日誌包裝層，確保每一條 SQL 都被記錄下來。
