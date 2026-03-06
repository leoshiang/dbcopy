# 安全性設計

資料庫工具本身就是高權限的軟體——它可以讀取結構、複製資料、建立物件。因此安全性設計不是事後補強，而是從第一行程式碼就需要考慮的事。

## SQL 注入防護

SQL 注入是資料庫應用最常見的安全漏洞。DbCopy 的防護策略分為兩個層次。

### 第一層：參數化查詢（Dapper）

所有使用者可控的值（Schema 名稱、物件名稱）都透過 Dapper 的參數化查詢傳入：

```csharp
// 安全：@Schema 和 @Name 是參數化的
const string sql = "SELECT 1 FROM sys.tables WHERE SCHEMA_NAME(schema_id) = @Schema AND name = @Name";
await conn.ExecuteScalarWithLogAsync<int?>(logger, sql, new { obj.Schema, obj.Name });
```

Dapper 會將 `@Schema` 和 `@Name` 轉換為 SQL 參數（`SqlParameter` / `NpgsqlParameter`），確保值永遠不會被當作 SQL 程式碼執行。

### 哪些場景無法使用參數化？

SQL 的語法限制使得某些位置**不能使用參數**：

```sql
-- ❌ 不合法：表名不能用參數
SELECT * FROM @TableName

-- ❌ 不合法：Schema 名不能用參數
CREATE SCHEMA @SchemaName

-- ❌ 不合法：DDL 語句不能用參數
CREATE TABLE @TableName (...)
```

這些場景只能使用**識別符跳脫**來防護。

## 識別符跳脫策略

### SQL Server：方括號跳脫

SQL Server 使用方括號 `[...]` 引用識別符。方括號內的 `]` 需要雙重為 `]]`：

```csharp
// SQL Server 的識別符跳脫
$"SELECT * FROM [{obj.Schema.Replace("]", "]]")}].[{obj.Name.Replace("]", "]]")}]"

// 範例：
// Schema = "dbo"     → [dbo]
// Name = "Users"     → [Users]
// Name = "User]s"    → [User]]s]     ← 內部的 ] 被跳脫
```

這個跳脫在整個 `SqlServerService` 中被一致地使用：

```csharp
// CopyTableDataAsync 中的 SELECT
var sql = $"SELECT * FROM [{obj.Schema.Replace("]", "]]")}].[{obj.Name.Replace("]", "]]")}]";

// GetRowCountAsync 中的 COUNT
$"SELECT COUNT_BIG(*) FROM [{obj.Schema.Replace("]", "]]")}].[{obj.Name.Replace("]", "]]")}]"

// SqlBulkCopy 的目的表名
bulkCopy.DestinationTableName = $"[{obj.Schema.Replace("]", "]]")}].[{obj.Name.Replace("]", "]]")}]";

// EnsureSchemaExistsAsync
$"CREATE SCHEMA [{schema.Replace("]", "]]")}]"
```

### PostgreSQL：雙引號跳脫

PostgreSQL 使用雙引號 `"..."` 引用識別符。雙引號內的 `"` 需要雙重為 `""`：

```csharp
// PostgreSQL 的識別符跳脫
$"CREATE SCHEMA \"{schema.Replace("\"", "\"\"")}\""

// 但在多數地方，PostgreSQL 直接使用雙引號而不做額外跳脫
// 因為 Schema/Name 來自 pg_catalog，本身就是合法的識別符
$"SELECT COUNT(*)::bigint FROM \"{obj.Schema}\".\"{obj.Name}\""
```

### PostgreSQL 特有：PL/pgSQL 中的 format('%I', ...)

在 PL/pgSQL 匿名區塊中，使用 `format('%I', ...)` 函數來安全地引用識別符：

```csharp
var setValSql = $"""
    DO $$
    DECLARE max_val bigint;
    BEGIN
        EXECUTE format('SELECT MAX(%I) FROM %I.%I',
            '{escapedCol}', '{escapedSchema}', '{escapedName}') INTO max_val;
        IF max_val IS NULL THEN
            PERFORM setval(format('%I.%I', '{escapedSeqSchema}', '{escapedSeqName}'), 1, false);
        ELSE
            PERFORM setval(format('%I.%I', '{escapedSeqSchema}', '{escapedSeqName}'), max_val, true);
        END IF;
    END $$;
    """;
```

這裡有兩層防護：

1. **`EscapePgLiteral()`**：將單引號 `'` 跳脫為 `''`，防止從字串常值中逃逸
2. **`format('%I', ...)`**：PostgreSQL 內建函數，自動為識別符加上雙引號並處理跳脫

### QUOTENAME — SQL Server 內建的安全識別符

在某些 SQL 查詢中，DbCopy 使用 `QUOTENAME()` 函數：

```sql
SELECT OBJECT_DEFINITION(OBJECT_ID(QUOTENAME(@Schema) + '.' + QUOTENAME(@Name)))
```

`QUOTENAME()` 是 SQL Server 內建的函數，等同於 `[` + 跳脫 `]` + `]`。它與參數化配合使用——`@Schema` 和 `@Name` 是參數化的值，`QUOTENAME()` 在 SQL 伺服器端處理識別符引用。

## 連線字串的安全考量

### 傳輸安全

連線字串包含帳號密碼，屬於高度敏感資訊。DbCopy 的設計：

| 面向 | 做法 |
|------|------|
| 存儲 | 存在瀏覽器的 `localStorage` 中（僅存在本地） |
| 傳輸 | 透過 POST Request Body 傳送（不在 URL 中） |
| 日誌 | 不記錄連線字串（僅記錄 SQL 語句） |
| 顯示 | 在連線管理 Modal 中可見，但不在主頁面顯示 |

### 未實作認證機制

DbCopy **刻意不實作**認證/授權機制：

```csharp
// Program.cs
// Fix #13: UseAuthorization removed — no auth scheme or policies are configured
```

原因：DbCopy 設計為**本地工具**，預設綁定到 `localhost`。如果需要在網路上暴露，應該透過反向代理（Nginx/Caddy）或 VPN 來保護，而不是在工具本身加入簡陋的認證機制。

### 唯讀連線的保護

前端提供了「唯讀」標記功能：

```javascript
// 唯讀連線不會出現在目標選擇下拉中
if (conn.id !== currentSource && !conn.readOnly) {
    targetSelect.append(`<option>${conn.name}</option>`);
}
```

這是一個**前端層級的保護**，防止使用者誤將生產資料庫設為同步目標。但請注意，這不是一個安全機制——惡意使用者可以透過直接呼叫 API 繞過此限制。它的目的是**防誤操作**，而非防攻擊。

### DDL 語句的安全性

`GetObjectDefinitionAsync` 產生的 DDL 語句會直接在目標資料庫中執行：

```csharp
var definition = await GetObjectDefinitionAsync(sourceConnectionString, obj);
await conn.ExecuteWithLogAsync(logger, definition);
```

這是否構成安全風險？

**不構成**，因為 DDL 定義來自**來源資料庫的系統目錄**，而非使用者輸入。如果攻擊者能夠控制來源資料庫的系統目錄，那麼他已經擁有了 DBA 權限，不需要透過 DbCopy 來發動攻擊。

### 安全設計的哲學

DbCopy 的安全設計遵循一個原則：

> **保護層級應與威脅模型匹配。**

- 本地工具 → 不需要網路層認證
- 系統目錄查詢 → 不需要防禦 SQL 注入（系統目錄不是使用者輸入）
- 動態識別符 → 需要識別符跳脫
- 連線字串 → 不記錄到日誌

過度的安全措施會增加複雜度而不增加安全性。

---

> **下一章預告**：安全性之後，第 14 章將探討效能與可靠性——非同步設計模式、平行化策略、批次處理，以及如何優雅地處理部分失敗。
