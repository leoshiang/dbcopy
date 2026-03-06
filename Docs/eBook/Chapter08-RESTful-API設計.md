# RESTful API 設計 — DbController

## API 端點規劃

DbCopy 的 API 層只有一個控制器 `DbController`，提供四個端點：

| 方法 | 路徑 | 功能 | 輸入 | 輸出 |
|------|------|------|------|------|
| POST | `/api/db/test` | 測試連線 | `DbConnectionInfo` | `{ success: true }` |
| POST | `/api/db/objects` | 列出物件 | `DbConnectionInfo` | `DbObject[]` |
| POST | `/api/db/compare` | 比較差異 | `CompareRequest` | `SyncStatus[]` |
| POST | `/api/db/copy` | 複製物件 | `CopyRequest` | `{ success: true }` |

### 為什麼全部用 POST？

傳統的 RESTful 設計會用 GET 讀取、POST 建立。但 DbCopy 的所有請求都包含連線字串（敏感資訊），不適合放在 URL 參數中。因此統一使用 POST，將資料放在 Request Body 中。

### 路由配置

```csharp
[ApiController]
[Route("api/[controller]")]
public class DbController(...) : ControllerBase
```

- `[ApiController]`：啟用 API 行為（自動模型驗證、自動 `[FromBody]` 綁定）
- `[Route("api/[controller]")]`：`[controller]` 會被替換為類別名稱 `Db`，生成路由前綴 `api/db`

## Primary Constructor 注入

DbCopy 使用 C# 12 的 **Primary Constructor** 語法進行依賴注入：

```csharp
public class DbController(
    SqlServerService sqlServerService,
    PostgreSqlService postgreSqlService,
    ILogger<DbController> logger)
    : ControllerBase
{
    private IDbService GetService(DbType type) => type switch
    {
        DbType.SqlServer => sqlServerService,
        DbType.PostgreSql => postgreSqlService,
        _ => throw new ArgumentException("Unsupported database type")
    };
    ...
}
```

### Primary Constructor 的優勢

傳統寫法需要宣告欄位、撰寫建構子、賦值：

```csharp
// 傳統寫法（冗長）
public class DbController : ControllerBase
{
    private readonly SqlServerService _sqlServerService;
    private readonly PostgreSqlService _postgreSqlService;
    private readonly ILogger<DbController> _logger;

    public DbController(SqlServerService sqlServerService,
        PostgreSqlService postgreSqlService, ILogger<DbController> logger)
    {
        _sqlServerService = sqlServerService;
        _postgreSqlService = postgreSqlService;
        _logger = logger;
    }
}
```

Primary Constructor 將以上程式碼壓縮成一行，參數自動成為整個類別可用的變數。

### GetService — 服務分派

```csharp
private IDbService GetService(DbType type) => type switch
{
    DbType.SqlServer => sqlServerService,
    DbType.PostgreSql => postgreSqlService,
    _ => throw new ArgumentException("Unsupported database type")
};
```

這個私有方法是 API 層與服務層之間的**橋梁**。根據請求中的 `DbType`，選擇對應的服務實作。上層程式碼完全不需要知道具體用的是哪個服務。

## 四大端點的實作

### test — 連線測試

```csharp
[HttpPost("test")]
public async Task<IActionResult> TestConnection([FromBody] DbConnectionInfo connection)
{
    try
    {
        var service = GetService(connection.Type);
        var success = await service.TestConnectionAsync(connection.ConnectionString);
        return Ok(new { success });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "TestConnection failed");
        return BadRequest(ex.Message);
    }
}
```

最簡單的端點——委派給服務層測試連線，成功回傳 `{ success: true }`，失敗回傳錯誤訊息。

### objects — 列出物件

```csharp
[HttpPost("objects")]
public async Task<IActionResult> GetObjects([FromBody] DbConnectionInfo connection)
{
    var service = GetService(connection.Type);
    var objects = await service.GetDbObjectsAsync(connection.ConnectionString);
    return Ok(objects);
}
```

同樣是簡單的委派。回傳的 `DbObject[]` 會被 ASP.NET Core 自動序列化為 JSON。

### compare — 比較差異（核心端點）

```csharp
[HttpPost("compare")]
public async Task<IActionResult> Compare([FromBody] CompareRequest request)
{
    var sourceService = GetService(request.Source.Type);
    var targetService = GetService(request.Target.Type);

    var sourceObjects = await sourceService.GetDbObjectsAsync(request.Source.ConnectionString);
    var syncStatuses = new List<SyncStatus>();

    foreach (var obj in sourceObjects)
    {
        // 獨立查詢平行化
        var existsTask = targetService.CheckObjectExistsAsync(...);
        var depsTask = sourceService.GetDependenciesAsync(...);
        await Task.WhenAll(existsTask, depsTask);

        if (obj.Type == DbObjectType.Table)
        {
            // 四個資料表查詢也平行化
            var sourceRowsTask = SafeGetRowCount(sourceService, ...);
            var sourceIndexesTask = SafeGetIndexes(sourceService, ...);
            var targetRowsTask = exists ? SafeGetRowCount(targetService, ...) : Task.FromResult<long?>(null);
            var targetIndexesTask = exists ? SafeGetIndexes(targetService, ...) : Task.FromResult(new List<DbIndex>());
            await Task.WhenAll(sourceRowsTask, sourceIndexesTask, targetRowsTask, targetIndexesTask);
            ...
        }

        syncStatuses.Add(new SyncStatus { ... });
    }

    return Ok(syncStatuses);
}
```

### copy — 複製物件

```csharp
[HttpPost("copy")]
public async Task<IActionResult> Copy([FromBody] CopyRequest request)
{
    var sourceService = GetService(request.Source.Type);
    var targetService = GetService(request.Target.Type);

    // 1. 確保 Schema 存在
    await targetService.EnsureSchemaExistsAsync(request.Target.ConnectionString,
        request.Object.Schema);

    // 2. 檢查物件是否已存在（僅 Phase 0 或 1）
    if (request.Phase is 0 or 1)
    {
        var exists = await targetService.CheckObjectExistsAsync(...);
        if (exists) return BadRequest("Object already exists in destination.");
    }

    // 3. 執行複製
    await sourceService.CopyObjectAsync(
        request.Source.ConnectionString,
        request.Target.ConnectionString,
        request.Object, request.Phase, request.BatchSize);

    return Ok(new { success = true });
}
```

**設計要點**：

- **Schema 先行**：在嘗試建立任何物件之前，先確保目標 Schema 存在
- **存在性檢查有條件**：只在 Phase 0（全部）或 Phase 1（結構）時檢查。Phase 2（資料）、3（索引）、4（外鍵）不需要——它們假設結構已經存在
- **CopyObjectAsync 的來源服務**：注意複製操作是由**來源服務**的 `CopyObjectAsync` 負責，因為它需要讀取來源的資料和定義

## 平行化查詢策略

`Compare` 端點中大量使用了 `Task.WhenAll` 進行平行化查詢。這是 DbCopy 效能的關鍵。

### 第一層平行化：存在性檢查 + 相依性分析

```csharp
// 這兩個查詢完全獨立——一個查來源、一個查目標
var existsTask = targetService.CheckObjectExistsAsync(request.Target.ConnectionString, obj);
var depsTask = sourceService.GetDependenciesAsync(request.Source.ConnectionString, obj);
await Task.WhenAll(existsTask, depsTask);
```

**為什麼可以平行？** 因為它們查的是不同的資料庫（一個來源、一個目標），完全沒有相依性。

### 第二層平行化：資料表的四個查詢

```csharp
var sourceRowsTask = SafeGetRowCount(sourceService, request.Source.ConnectionString, obj, "source");
var sourceIndexesTask = SafeGetIndexes(sourceService, request.Source.ConnectionString, obj, "source");
var targetRowsTask = exists
    ? SafeGetRowCount(targetService, request.Target.ConnectionString, obj, "target")
    : Task.FromResult<long?>(null);
var targetIndexesTask = exists
    ? SafeGetIndexes(targetService, request.Target.ConnectionString, obj, "target")
    : Task.FromResult(new List<DbIndex>());

await Task.WhenAll(sourceRowsTask, sourceIndexesTask, targetRowsTask, targetIndexesTask);
```

四個獨立的查詢同時發出，等待全部完成。如果目標物件不存在，直接用 `Task.FromResult` 回傳預設值，避免不必要的查詢。

### Safe Wrapper 模式

```csharp
async Task<long?> SafeGetRowCount(IDbService svc, string cs, DbObject o, string side)
{
    try { return await svc.GetRowCountAsync(cs, o); }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to get {Side} row count for {Schema}.{Name}",
            side, o.Schema, o.Name);
        return null;
    }
}
```

`Task.WhenAll` 的問題是：如果任何一個 Task 失敗，整個 `await` 就會拋出例外。但行數查詢和索引查詢的失敗**不應該影響**比較結果的回傳。

透過 `SafeGetRowCount` 和 `SafeGetIndexes` 包裝，個別查詢的失敗會被捕捉並記錄為警告，而不是中斷整個比較流程。

## 錯誤處理與回應格式

所有端點都遵循統一的錯誤處理模式：

```csharp
try
{
    // 業務邏輯
    return Ok(result);          // 成功：200 OK
}
catch (Exception ex)
{
    logger.LogError(ex, "操作名稱 failed");
    return BadRequest(ex.Message);  // 失敗：400 Bad Request
}
```

### 回應格式

| 場景 | HTTP 狀態碼 | 回應內容 |
|------|-----------|---------|
| 連線測試成功 | 200 | `{ "success": true }` |
| 比較成功 | 200 | `SyncStatus[]` (JSON 陣列) |
| 複製成功 | 200 | `{ "success": true }` |
| 物件已存在 | 400 | `"Object already exists in destination."` |
| 連線失敗 | 400 | 例外訊息（純文字） |
| 複製失敗 | 400 | 例外訊息（純文字） |

**設計選擇**：使用 `BadRequest` 而非 `500 Internal Server Error`，因為大多數錯誤源自使用者輸入（錯誤的連線字串、目標物件已存在等），而非伺服器內部錯誤。

---

> **下一章預告**：API 層完成後，使用者需要一個友善的介面來操作。第 9 章將介紹 DbCopy 的單頁式 Web 介面——從連線管理到樹狀結果表格的完整設計。
