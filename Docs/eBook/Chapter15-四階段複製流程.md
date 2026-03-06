# 四階段複製流程的設計哲學

這是本書的最後一章，也是最重要的一章。我們將從高層次回顧 DbCopy 最核心的設計決策——**四階段複製流程**——以及背後的考量。

## 為什麼分階段

如果只是單純地「把來源的東西複製到目標」，為什麼不一步到位，而要分成四個階段？

### 一步到位的問題

假設我們用以下方式複製資料表 `Orders`：

```sql
-- 一步到位
CREATE TABLE Orders (..., CONSTRAINT FK_Orders_Users FOREIGN KEY (UserId) REFERENCES Users(Id));
INSERT INTO Orders SELECT * FROM source.Orders;
CREATE INDEX IX_Orders_Date ON Orders(OrderDate);
```

這看起來很簡單，但在實際場景中會遇到大量問題：

**問題 1：外鍵的雞生蛋問題**

```
Orders.UserId → Users.Id （Orders 依賴 Users）
Users.CreatedBy → Users.Id （Users 自參照）
```

如果 `CREATE TABLE Orders` 包含外鍵定義，而 `Users` 表還沒建立，就會失敗。

**問題 2：大量資料的索引效能**

先建立索引再插入 100 萬筆資料，每一筆 INSERT 都需要更新索引。如果先插入資料再建立索引，只需要一次性掃描並建立索引，效能可以提升 10-100 倍。

**問題 3：部分重試的困難**

如果索引建立失敗了，是否需要重新複製整個資料表（包括 100 萬筆資料）？當然不需要。但如果所有操作綁在一起，就沒辦法只重試失敗的部分。

### 分階段的優勢

| 優勢 | 說明 |
|------|------|
| 解耦外鍵依賴 | 先建所有表的結構，再建外鍵，避免循環依賴 |
| 效能最佳化 | 先插入資料再建索引，大幅提升批次載入效率 |
| 部分重試 | 結構建好了？跳過。資料複製完了？只做索引。 |
| 進度可見 | 使用者可以看到目前在哪個階段 |
| 靈活控制 | 使用者可以選擇只執行特定階段 |

## Phase 1：結構

**目的**：在目標資料庫建立物件的結構定義。

**包含**：
- 資料表的欄位定義、主鍵
- 檢視的 SELECT 定義
- 預存程序的原始碼
- 函數的原始碼
- 使用者定義型別的定義
- 序列的定義

**不包含**：
- 非主鍵索引（Phase 3）
- 外鍵條件約束（Phase 4）
- 資料列（Phase 2）

### 為什麼主鍵在 Phase 1 而不是 Phase 3？

主鍵是資料表定義的一部分——它定義了唯一性約束，影響資料的正確性。把主鍵放在 `CREATE TABLE` 語句中是標準做法：

```sql
CREATE TABLE Users (
    Id INT IDENTITY(1,1) NOT NULL,
    Name NVARCHAR(100) NOT NULL,
    CONSTRAINT PK_Users PRIMARY KEY (Id)    -- 主鍵在此
)
```

非主鍵索引是效能輔助，可以延後建立。

### Phase 1 的前置條件

在執行 Phase 1 之前，`DbController` 會先做兩件事：

```csharp
// 1. 確保 Schema 存在
await targetService.EnsureSchemaExistsAsync(request.Target.ConnectionString,
    request.Object.Schema);

// 2. 確認物件不存在
var exists = await targetService.CheckObjectExistsAsync(request.Target.ConnectionString,
    request.Object);
if (exists) return BadRequest("Object already exists in destination.");
```

## Phase 2：資料

**目的**：將來源資料表的所有資料列複製到目標資料表。

**使用技術**：
- SQL Server：`SqlBulkCopy`
- PostgreSQL：`COPY ... FROM STDIN`

### 為什麼 Phase 2 在 Phase 3 之前？

```
有索引的情況下插入 100 萬筆：
每一筆 INSERT → 更新 B-Tree 索引 → O(N × log N)

無索引的情況下插入 100 萬筆後建索引：
批量 INSERT → O(N)
一次性建索引 → O(N log N)
總計 → O(N + N log N) ≈ O(N log N)

但常數係數差異巨大：逐筆更新索引的 I/O 遠大於一次性排序建索引。
```

實務上，先資料再索引的方式通常快 5-50 倍。

### Phase 2 的後處理

資料複製完成後，需要重設 IDENTITY/序列的種子值：

```
SQL Server: DBCC CHECKIDENT('table', RESEED)
PostgreSQL: setval('sequence', max_value)
```

否則後續的 INSERT 操作會從錯誤的起始值開始，導致主鍵衝突。

## Phase 3：索引

**目的**：建立非主鍵的索引。

**包含**：
- 唯一索引（UNIQUE INDEX）
- 一般索引
- 含 INCLUDE 欄位的索引
- 複合索引

**不包含**：
- 主鍵索引（已在 Phase 1 建立）

### 容錯設計

索引建立採用「逐一嘗試、失敗警告」的策略：

```csharp
foreach (var idx in indexSqls)
{
    try { await conn.ExecuteWithLogAsync(logger, idx); }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to create index ...");
        // 不中斷，繼續下一個索引
    }
}
```

索引失敗的常見原因：
- 目標資料庫已有同名索引
- 索引定義包含了目標資料庫不支援的功能
- 資料不符合唯一索引的約束

## Phase 4：外鍵

**目的**：建立外部索引鍵條件約束。

### 為什麼外鍵是最後一個階段？

外鍵是最「危險」的操作，因為它有外部依賴——參考的資料表必須已經存在且有正確的資料。

```
Orders.UserId → Users.Id

要建立這個外鍵，需要：
1. Orders 表已存在（Phase 1）     ✓
2. Users 表已存在（Phase 1）      ← 可能不在這次複製範圍內！
3. Orders.UserId 的每個值在 Users.Id 中都存在（Phase 2） ← 如果 Users 沒有資料？
```

把外鍵放在最後，確保了：
- 所有資料表的結構都已建立
- 所有資料都已複製
- 所有索引都已建立（外鍵參考欄位通常有索引）

### 特殊錯誤處理

```csharp
// SQL Server: Error 4902 = 參考物件不存在
catch (SqlException ex) when (ex.Number == 4902)
{
    logger.LogInformation("Skipped - referenced object does not exist (Error 4902)");
}

// PostgreSQL: 42P01 = undefined_table
catch (PostgresException ex) when (ex.SqlState == "42P01")
{
    logger.LogInformation("Skipped - referenced table does not exist (42P01)");
}
```

使用 `LogInformation`（而非 `LogWarning`）是因為這是**預期行為**：使用者可能只複製了部分資料表。

## 相依性排序與複製順序

### 列舉值蘊含的順序

回顧第 3 章的 `DbObjectType` 列舉：

```csharp
public enum DbObjectType
{
    UserDefinedType,        // 0
    UserDefinedTableType,   // 1
    Sequence,               // 2
    Table,                  // 3
    View,                   // 4
    Procedure,              // 5
    Function                // 6
}
```

前端 UI 依據 Schema → Type（列舉值）→ Name 排序，自然形成正確的複製順序。

### 建議的複製順序

```
第一輪：基礎型別
  1. UserDefinedType (Enum, Domain, Composite)
  2. UserDefinedTableType

第二輪：獨立物件
  3. Sequence

第三輪：資料容器
  4. Table (Phase 1: 結構)
  5. Table (Phase 2: 資料)
  6. Table (Phase 3: 索引)

第四輪：程式碼物件
  7. View
  8. Procedure
  9. Function

第五輪：關聯
  10. Table (Phase 4: 外鍵)
```

### 相依性資訊的呈現

`Compare` 端點會回傳每個物件的依賴清單：

```json
{
    "sourceObject": { "schema": "dbo", "name": "Orders", "type": 3 },
    "dependencies": ["dbo.Users", "dbo.PhoneNumber"],
    "existsInDestination": false,
    "status": "Pending"
}
```

前端可以利用這個資訊提醒使用者：「Orders 依賴 Users 和 PhoneNumber，請確保它們先被複製。」

### 完整流程圖

```
使用者選擇要複製的物件
         ↓
依照列舉值排序（型別 → 序列 → 表 → 檢視 → 程序 → 函數）
         ↓
┌────────────────────────────────────────────┐
│ 對每個物件：                                 │
│                                             │
│  EnsureSchemaExists (目標)                   │
│         ↓                                   │
│  CheckObjectExists (目標)                    │
│         ↓                                   │
│  如果是 Table：                               │
│    Phase 1: GetDefinition → Execute (CREATE)  │
│    Phase 2: CopyTableData (BulkCopy/COPY)     │
│    Phase 3: GetIndexDefs → Execute (各索引)    │
│    Phase 4: GetFKDefs → Execute (各外鍵)      │
│                                             │
│  如果是其他物件：                               │
│    Phase 1: GetDefinition → Execute           │
│                                             │
│  更新 SyncStatus (Success / Error)            │
└────────────────────────────────────────────┘
         ↓
顯示同步結果摘要
```

### 設計的自洽性

整個四階段複製流程的設計是**自洽的**：

1. **列舉排序**確保型別在資料表之前建立
2. **Phase 1**確保所有結構在資料之前建立
3. **Phase 2**確保所有資料在索引之前插入
4. **Phase 3**確保所有索引在外鍵之前建立
5. **Phase 4**在最後建立外鍵，此時所有前提都已滿足

任何一個環節的失敗都可以獨立重試，不影響已完成的環節。

---

## 本書總結

DbCopy 是一個小而精的工具——整個系統只有 7 個 C# 檔案、1 個 HTML 頁面、1 個 CSS 檔案。但在這精簡的體積中，蘊含了大量的設計決策：

- **介面導向設計**讓我們能夠支援多個資料庫引擎
- **Dapper 包裝層**確保了日誌的完整性
- **四階段複製**解決了外鍵依賴和效能問題
- **嵌入式資源**實現了零安裝部署
- **連接埠衝突偵測**提升了使用者體驗
- **結構化日誌**方便了偵錯和維運
- **平行化查詢**提升了比較操作的效能
- **優雅的部分失敗**確保了系統的健壯性

希望本書能幫助讀者理解——一個好的工具不需要複雜的架構，但需要每一個設計決策都經過深思熟慮。

> **最佳的程式碼不是最聰明的程式碼，而是最容易理解、最難出錯的程式碼。**
