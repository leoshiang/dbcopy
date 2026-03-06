# 效能與可靠性

DbCopy 需要處理大量的資料庫查詢和資料傳輸。本章討論系統中使用的效能優化策略和可靠性保障機制。

## 非同步設計模式

DbCopy 的所有 I/O 操作都使用 `async/await` 模式：

```csharp
// 每個方法都是非同步的
public async Task<bool> TestConnectionAsync(string connectionString)
{
    await using var conn = new SqlConnection(connectionString);
    await conn.OpenAsync();
    return true;
}
```

### 為什麼非同步很重要？

DbCopy 是一個 Web 應用，每個 HTTP 請求都由一個執行緒處理。如果使用同步 I/O：

```
同步模式：
Thread 1 → 發送 SQL → [等待回應...............] → 處理結果
                       ↑ 執行緒被阻塞，無法處理其他請求

非同步模式：
Thread 1 → 發送 SQL → [釋放執行緒] ← 處理結果
                       ↑ 執行緒可以處理其他請求
```

對於 DbCopy 這種需要大量資料庫往返的工具，非同步模式確保了：
1. **Web 伺服器不會因為長時間查詢而無回應**
2. **同時處理多個使用者的請求**（雖然通常只有一個使用者）
3. **I/O 等待時間不浪費 CPU 資源**

### `await using` 模式

```csharp
await using var conn = new SqlConnection(connectionString);
```

`await using` 確保非同步資源在作用域結束時被正確釋放。這比 `using` 更適合非同步情境，因為資源的釋放本身也可能是非同步操作（如關閉網路連線）。

## Task.WhenAll 平行化策略

DbCopy 在 `Compare` 端點中大量使用 `Task.WhenAll` 來平行化獨立的查詢：

### 策略一：來源與目標平行查詢

```csharp
// 這兩個查詢查的是不同的資料庫，完全獨立
var existsTask = targetService.CheckObjectExistsAsync(request.Target.ConnectionString, obj);
var depsTask = sourceService.GetDependenciesAsync(request.Source.ConnectionString, obj);
await Task.WhenAll(existsTask, depsTask);

var exists = existsTask.Result;
var deps = depsTask.Result;
```

**效果**：如果每個查詢需要 50ms，循序執行需要 100ms，平行執行只需要 ~50ms。

### 策略二：四個資料表查詢平行化

```csharp
var sourceRowsTask = SafeGetRowCount(sourceService, ...);
var sourceIndexesTask = SafeGetIndexes(sourceService, ...);
var targetRowsTask = exists
    ? SafeGetRowCount(targetService, ...)
    : Task.FromResult<long?>(null);
var targetIndexesTask = exists
    ? SafeGetIndexes(targetService, ...)
    : Task.FromResult(new List<DbIndex>());

await Task.WhenAll(sourceRowsTask, sourceIndexesTask, targetRowsTask, targetIndexesTask);
```

**效果**：四個查詢同時發出，總等待時間等於最慢的那個查詢。

### 條件性跳過

```csharp
var targetRowsTask = exists
    ? SafeGetRowCount(targetService, ...)
    : Task.FromResult<long?>(null);  // 目標不存在，直接回傳 null，零成本
```

如果目標物件不存在，使用 `Task.FromResult` 回傳預設值。這避免了不必要的資料庫查詢。

### 注意事項

平行化的前提是查詢之間**真正獨立**。以下情況不能平行化：

```csharp
// ❌ 錯誤：先取定義，再執行定義，有相依性
var definition = await GetObjectDefinitionAsync(...);  // 必須先完成
await conn.ExecuteAsync(definition);                   // 依賴上一步的結果
```

## 批次處理與串流

### SQL Server：SqlBulkCopy 的批次模式

```csharp
using var bulkCopy = new SqlBulkCopy(targetConn, ...);
bulkCopy.BatchSize = batchSize;  // 預設 1000
bulkCopy.BulkCopyTimeout = 600;  // 10 分鐘

await bulkCopy.WriteToServerAsync(reader);
```

`BatchSize` 控制每次提交到伺服器的列數：
- **太小**（如 10）：頻繁的網路往返，效率低
- **太大**（如 100000）：伺服器端暫存大量資料，佔用記憶體和交易日誌空間
- **預設 1000**：在效能和資源使用之間取得平衡

### PostgreSQL：COPY 協定的串流模式

```csharp
// 二進位模式 — 逐列串流，不需要一次載入所有資料
await using var importer = await targetConn.BeginBinaryImportAsync(
    $"COPY ... FROM STDIN (FORMAT BINARY)");

while (await reader.ReadAsync())      // 逐列讀取來源
{
    await importer.StartRowAsync();
    for (var i = 0; i < reader.FieldCount; i++)
        await importer.WriteAsync(reader.GetValue(i));  // 逐欄寫入目標
}
await importer.CompleteAsync();
```

這是真正的**串流模式**——來源的每一列讀取後立即寫入目標，記憶體中只存在一列的資料。即使是數百萬列的大型資料表，記憶體使用量也保持恆定。

### 記憶體使用比較

| 方式 | 記憶體使用 | 適用場景 |
|------|----------|---------|
| `SELECT * → List → INSERT` | O(n) — 全部載入 | 小型資料表 |
| `SqlBulkCopy + DataReader` | O(batch) — 批次大小 | SQL Server |
| `COPY + 串流` | O(1) — 一列 | PostgreSQL |

## 優雅的部分失敗處理

DbCopy 的設計原則是：**非關鍵操作的失敗不應該中斷整體流程**。

### 索引建立的容錯

```csharp
var indexSqls = await GetTableIndexDefinitionsAsync(sourceConnectionString, obj);
foreach (var idx in indexSqls)
{
    try
    {
        await conn.ExecuteWithLogAsync(logger, idx);
    }
    catch (Exception ex)
    {
        // 記錄警告，但繼續處理下一個索引
        logger.LogWarning(ex, "Failed to create index for {Schema}.{Name}",
            obj.Schema, obj.Name);
    }
}
```

**為什麼索引失敗可以忽略？** 索引只影響查詢效能，不影響資料正確性。一個索引失敗不應該導致其他索引也無法建立。

### 外鍵建立的特殊處理

```csharp
// SQL Server
catch (SqlException ex) when (ex.Number == 4902)
{
    // Error 4902: 參考的物件不存在
    logger.LogInformation("Skipped foreign key - referenced object does not exist");
}

// PostgreSQL
catch (PostgresException ex) when (ex.SqlState == "42P01")
{
    // 42P01: undefined_table
    logger.LogInformation("Skipped foreign key - referenced table does not exist");
}
```

**為什麼用 `LogInformation` 而不是 `LogWarning`？** 因為這是**預期行為**——當使用者只複製部分資料表時，外鍵參考的資料表可能不在目標中。這不是錯誤，而是正常的操作結果。

### 行數查詢的容錯

```csharp
async Task<long?> SafeGetRowCount(IDbService svc, string cs, DbObject o, string side)
{
    try { return await svc.GetRowCountAsync(cs, o); }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to get {Side} row count ...", side, ...);
        return null;  // 回傳 null 而不是拋出例外
    }
}
```

**回傳 `null` 的語意**：前端 UI 會將 null 顯示為 `?`，讓使用者知道行數未知，但不影響其他功能。

### IDENTITY/序列重設的容錯

```csharp
// SQL Server
try { await reseedCmd.ExecuteNonQueryAsync(); }
catch (Exception ex)
{
    logger.LogWarning(ex, "Failed to reseed identity for {Schema}.{Name}", ...);
}

// PostgreSQL
try { await targetConn.ExecuteWithLogAsync(logger, setValSql); }
catch (Exception ex)
{
    logger.LogWarning(ex, "Failed to reseed sequence for {Schema}.{Name}", ...);
}
```

序列重設失敗不影響已複製的資料，只會影響**後續的 INSERT 操作**。記錄警告讓使用者知道需要手動處理。

### 容錯層級總結

| 操作 | 失敗處理 | 日誌等級 | 繼續執行？ |
|------|---------|---------|----------|
| 結構建立（Phase 1） | 拋出例外 | Error | 否 |
| 資料複製（Phase 2） | 拋出例外 | Error | 否 |
| 索引建立（Phase 3） | 捕捉，繼續 | Warning | 是 |
| 外鍵建立（Phase 4） | 捕捉，繼續 | Warning/Info | 是 |
| 行數查詢 | 捕捉，回傳 null | Warning | 是 |
| 索引查詢 | 捕捉，回傳空列表 | Warning | 是 |
| IDENTITY/序列重設 | 捕捉，繼續 | Warning | 是 |

---

> **下一章預告**：最後一章將從整體視角回顧四階段複製流程的設計哲學——為什麼要分階段、每個階段解決什麼問題、以及相依性排序的策略。
