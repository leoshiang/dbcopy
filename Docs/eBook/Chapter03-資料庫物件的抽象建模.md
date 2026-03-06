# 資料庫物件的抽象建模

在開始寫任何服務邏輯之前，我們需要先回答一個根本問題：**如何用 C# 的型別系統來表達「資料庫物件」這個概念？**

好的模型設計是整個系統的基石。模型設計得好，後續的服務層、API 層、前端層都會自然而然地變得簡潔。

## 資料庫物件有哪些種類

不論是 SQL Server 還是 PostgreSQL，資料庫中的物件大致可以分為以下幾類：

| 類別 | SQL Server | PostgreSQL |
|------|-----------|------------|
| 資料表 | `sys.tables` | `pg_class (relkind='r','p')` |
| 檢視 | `sys.views` | `pg_class (relkind='v','m')` |
| 預存程序 | `sys.procedures` | `pg_proc (prokind='p')` |
| 函數 | `sys.objects (FN,IF,TF)` | `pg_proc (prokind='f')` |
| 使用者定義型別 | `sys.types` | `pg_type (typtype='c','e','d')` |
| 使用者定義資料表型別 | `sys.table_types` | *(無直接對應)* |
| 序列 | `sys.sequences` | `pg_class (relkind='S')` |

DbCopy 需要一個**統一的抽象**，讓同一套 API 和 UI 能夠處理所有這些物件類型。

## 設計 DbObjectType 列舉

```csharp
public enum DbObjectType
{
    UserDefinedType,        // 0 — 使用者定義型別
    UserDefinedTableType,   // 1 — 使用者定義資料表型別
    Sequence,               // 2 — 序列
    Table,                  // 3 — 資料表
    View,                   // 4 — 檢視
    Procedure,              // 5 — 預存程序
    Function                // 6 — 函數
}
```

### 列舉值的順序至關重要

注意列舉值的排列順序：**UserDefinedType → UserDefinedTableType → Sequence → Table → View → Procedure → Function**。

這個順序不是隨意決定的，而是反映了**物件之間的依賴關係**：

```
UserDefinedType (0)      ← 最底層，不依賴其他自訂物件
    ↑
UserDefinedTableType (1) ← 可能使用 UDT 作為欄位型別
    ↑
Sequence (2)             ← 獨立物件，但資料表的 IDENTITY/serial 可能依賴它
    ↑
Table (3)                ← 可能使用 UDT 作為欄位型別，可能使用 Sequence
    ↑
View (4)                 ← 依賴 Table 或其他 View
    ↑
Procedure (5)            ← 可能依賴 Table、View、UDT
    ↑
Function (6)             ← 可能依賴 Table、View、UDT
```

當前端 UI 依照列舉值排序顯示物件時，自然就會呈現出正確的複製順序：先建立型別，再建立序列，然後是資料表，最後是檢視和程式碼物件。

## 設計 DbObject 模型

```csharp
public class DbObject
{
    public string Schema { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DbObjectType Type { get; set; }
    public string? Definition { get; set; }

    public string FullName => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
    public List<DbIndex> Indexes { get; set; } = [];
}
```

### 設計決策解析

**Schema + Name 的組合**

資料庫物件的唯一識別不是只靠名稱，而是 Schema 加上名稱。在 SQL Server 中，`dbo.Users` 和 `hr.Users` 是兩個不同的物件。因此我們用 `Schema` 和 `Name` 兩個屬性來識別物件。

`FullName` 是一個計算屬性，提供 `"schema.name"` 格式的便利存取。

**Definition 為可空型別**

`Definition` 存放物件的 DDL 定義（例如 `CREATE TABLE ...`）。它設計為 `string?`（可空），因為：

1. 在**比較階段**，我們只需要知道物件的名稱和類型，不需要取得完整定義
2. 在**複製階段**，才會按需載入定義

這是一種**延遲載入**的設計思維——不在列表查詢時就載入所有定義，避免不必要的效能開銷。

**Indexes 集合**

索引是資料表的附屬物件，而非獨立的資料庫物件。因此它作為 `DbObject` 的一個屬性存在，而不是作為 `DbObjectType` 的一個值。

使用 `[]`（C# 12 的集合運算式）初始化為空列表，避免 null 檢查。

## 設計 DbIndex 與 SyncStatus

### DbIndex — 索引模型

```csharp
public class DbIndex
{
    public string Name { get; set; } = string.Empty;
    public string Columns { get; set; } = string.Empty;
    public bool IsUnique { get; set; }
    public bool ExistsInDestination { get; set; }
}
```

`DbIndex` 模型的設計刻意保持簡單：

- `Name`：索引名稱，用於在來源和目標之間比對
- `Columns`：索引鍵欄位的字串表示（包含 INCLUDE 欄位）
- `IsUnique`：是否為唯一索引
- `ExistsInDestination`：比較階段由 Controller 設定，告知 UI 此索引是否需要同步

注意 `ExistsInDestination` 不是索引本身的屬性，而是**比較結果**。這是一個務實的設計選擇——把比較結果直接附著在模型上，避免建立額外的對應表。

### SyncStatus — 同步狀態模型

```csharp
public class SyncStatus
{
    public DbObject SourceObject { get; set; } = null!;
    public bool ExistsInDestination { get; set; }
    public string Status { get; set; } = "Pending";
    public string? Message { get; set; }
    public List<string> Dependencies { get; set; } = [];
    public long? SourceRowCount { get; set; }
    public long? TargetRowCount { get; set; }
}
```

`SyncStatus` 是**比較操作的結果模型**，它回答了以下問題：

| 屬性 | 回答的問題 |
|------|----------|
| `SourceObject` | 來源有什麼物件？ |
| `ExistsInDestination` | 目標有沒有這個物件？ |
| `Status` | 目前的同步狀態是什麼？（Pending / Exists / Success / Error） |
| `Message` | 如果出錯了，錯誤訊息是什麼？ |
| `Dependencies` | 這個物件依賴哪些其他物件？ |
| `SourceRowCount` / `TargetRowCount` | 來源和目標各有多少筆資料？（僅資料表） |

**Status 的狀態機**

```
初始狀態
    ↓
Pending (目標不存在，等待複製)
    ↓ 執行複製
Success (複製成功)  或  Error (複製失敗)

Exists (目標已存在，無需複製)
```

**RowCount 為 `long?`**

- `long` 而非 `int`：因為資料表可能超過 21 億列（SQL Server 使用 `COUNT_BIG`）
- `?`（可空）：因為只有資料表才有行數，非資料表物件不適用此屬性；且查詢失敗時也可能返回 null

## 設計 API 請求模型

### DbConnectionInfo — 連線資訊

```csharp
public class DbConnectionInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public DbType Type { get; set; }
    public string ConnectionString { get; set; } = string.Empty;
}
```

`Id` 使用 GUID 自動產生，確保即使名稱重複也能唯一識別。`DbType` 是一個簡單的列舉：

```csharp
public enum DbType
{
    SqlServer,    // 0
    PostgreSql    // 1
}
```

### CompareRequest 與 CopyRequest

```csharp
public class CompareRequest
{
    public DbConnectionInfo Source { get; set; } = null!;
    public DbConnectionInfo Target { get; set; } = null!;
}

public class CopyRequest
{
    public DbConnectionInfo Source { get; set; } = null!;
    public DbConnectionInfo Target { get; set; } = null!;
    public DbObject Object { get; set; } = null!;
    public int Phase { get; set; } = 0;
    public int BatchSize { get; set; } = 1000;
}
```

**設計要點**：

1. **`null!` 的使用**：這些屬性在反序列化時由 JSON 填入，使用 `null!` 告訴編譯器「我知道它在建構時是 null，但使用時不會是 null」。
2. **Phase 預設為 0**：Phase 0 表示「全部階段」，是最常用的情境。
3. **BatchSize 預設為 1000**：在效能和記憶體之間取得平衡的預設值。

## 列舉排序的重要性：依賴順序

讓我們用一個實際的例子來說明為什麼列舉順序這麼重要。

假設來源資料庫有以下物件：

```sql
-- 1. 自訂型別
CREATE TYPE dbo.PhoneNumber FROM VARCHAR(20) NOT NULL;

-- 2. 資料表（使用了自訂型別）
CREATE TABLE dbo.Contacts (
    Id INT IDENTITY PRIMARY KEY,
    Name NVARCHAR(100),
    Phone dbo.PhoneNumber    -- 依賴 UDT
);

-- 3. 檢視（依賴資料表）
CREATE VIEW dbo.vContactList AS
SELECT Id, Name, Phone FROM dbo.Contacts;

-- 4. 預存程序（依賴資料表）
CREATE PROCEDURE dbo.GetContact @Id INT AS
SELECT * FROM dbo.Contacts WHERE Id = @Id;
```

正確的複製順序必須是：

```
1. PhoneNumber (UserDefinedType, 列舉值 0)
2. Contacts    (Table, 列舉值 3)
3. vContactList (View, 列舉值 4)
4. GetContact   (Procedure, 列舉值 5)
```

如果順序錯了——例如先建立資料表再建立型別——就會得到錯誤：

```
Msg 2715: Column or parameter #3: Cannot find data type dbo.PhoneNumber.
```

透過讓 `DbObjectType` 列舉值本身就蘊含正確的依賴順序，前端 UI 只需要按照列舉值排序，就能自然呈現出安全的複製順序。

---

> **下一章預告**：模型設計完成後，接下來要設計服務層的介面。第 4 章將討論 `IDbService` 介面的設計——九個方法各自的職責是什麼，為什麼要用介面而不是抽象類別。
