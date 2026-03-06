# 技術選型與架構總覽

## 為什麼選擇 ASP.NET Core

DbCopy 是一個需要使用者介面的工具。在技術選型時，我們面臨了兩個方向：

| 方案 | 優點 | 缺點 |
|------|------|------|
| 桌面應用（WPF/WinForms） | 原生體驗、豐富控制元件 | 僅 Windows、部署複雜 |
| Web 應用（ASP.NET Core） | 跨平台、零安裝、瀏覽器即用 | 需要內建 Web 伺服器 |
| 命令列工具（Console App） | 最輕量 | 互動體驗差、難以呈現表格比較 |

最終選擇 **ASP.NET Core** 的原因：

1. **跨平台**：.NET 10 原生支援 Windows、Linux、macOS，一套程式碼打天下。
2. **Self-Hosted**：Kestrel 內建 Web 伺服器，不需要 IIS 或 Nginx，單一執行檔即可運行。
3. **內建 DI 容器**：依賴注入是框架內建功能，不需要額外引入 IoC 容器。
4. **嵌入式靜態檔案**：透過 `ManifestEmbeddedFileProvider`，所有前端資源可以嵌入到執行檔中。
5. **Razor Pages + Controllers 混合架構**：頁面用 Razor Pages 渲染、API 用 Controllers 處理，各司其職。

## 為什麼用 Dapper 而不是 Entity Framework

DbCopy 的核心工作是「讀取資料庫的系統目錄（System Catalog）」，這與一般的 CRUD 應用完全不同。

### Entity Framework 不適合的原因

```
sys.tables、sys.columns、pg_class、pg_attribute...
```

這些系統目錄：
- **沒有固定的 Entity Model**：我們查詢的是中繼資料，不是業務資料
- **需要大量原始 SQL**：因為查詢系統目錄需要精確控制 SQL 語法
- **回傳結構多變**：不同的查詢回傳完全不同的欄位組合
- **效能優先**：不需要 Change Tracking、Lazy Loading 等 ORM 功能

### Dapper 的優勢

Dapper 是一個「微型 ORM」（Micro-ORM），它的設計哲學是：

> 你寫 SQL，我幫你把結果映射到 C# 物件。

```csharp
// Dapper 的典型用法 — 簡潔、直覺、零魔法
var results = await conn.QueryAsync<DbObject>(sql, new { Schema, Name });
```

- **零學習曲線**：會寫 SQL 就能用
- **零效能損耗**：幾乎等同於直接使用 ADO.NET
- **彈性映射**：支援強型別和 `dynamic` 兩種回傳模式
- **參數化查詢**：自動處理 SQL 參數，防止注入攻擊

## 前端技術選擇：Bootstrap + jQuery

在前端技術的選擇上，我們刻意選擇了「經典組合」而非現代 SPA 框架：

| 考量 | 選擇 | 理由 |
|------|------|------|
| CSS 框架 | Bootstrap 5 | 元件豐富、學習成本低、單頁工具不需要設計系統 |
| JS 框架 | jQuery | DOM 操作簡單直覺、不需要建置工具鏈 |
| 圖示 | Bootstrap Icons | 與 Bootstrap 無縫整合 |
| 字型 | Google Fonts (Inter) | 專為 UI 設計的現代無襯線字體 |

**為什麼不用 React/Vue/Angular？**

DbCopy 本質上是一個**單頁工具**，不是一個複雜的 Web 應用程式。它只有：
- 一個主頁面
- 幾個 Modal 對話框
- 一個動態表格

引入 SPA 框架意味著：
- 需要 Node.js 建置工具鏈
- 需要額外的打包步驟
- 增加部署複雜度
- 學習成本不成比例

對於這種規模的工具，jQuery 的直接 DOM 操作反而更簡單、更快速。

## 整體架構圖與分層設計

DbCopy 採用經典的**三層式架構**：

```
┌─────────────────────────────────────────────────────┐
│                    前端 UI 層                        │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────┐  │
│  │ Index.cshtml │  │  site.css    │  │  jQuery    │  │
│  │ (Razor Page) │  │  (Theme)     │  │  (Logic)  │  │
│  └──────────────┘  └──────────────┘  └───────────┘  │
├─────────────────────────────────────────────────────┤
│                    API 控制器層                       │
│  ┌──────────────────────────────────────────────┐   │
│  │              DbController                     │   │
│  │  POST /api/db/test                           │   │
│  │  POST /api/db/objects                        │   │
│  │  POST /api/db/compare                        │   │
│  │  POST /api/db/copy                           │   │
│  └──────────────────────────────────────────────┘   │
├─────────────────────────────────────────────────────┤
│                    服務層                             │
│  ┌────────────────────────────────────────┐         │
│  │            IDbService (介面)            │         │
│  └───────────┬────────────────┬───────────┘         │
│  ┌───────────┴──────┐ ┌──────┴───────────┐          │
│  │ SqlServerService │ │ PostgreSqlService │          │
│  └──────────────────┘ └──────────────────┘          │
│  ┌──────────────────────────────────────────┐       │
│  │         DapperExtensions (日誌包裝)       │       │
│  └──────────────────────────────────────────┘       │
├─────────────────────────────────────────────────────┤
│                    領域模型層                         │
│  ┌─────────┐ ┌──────────┐ ┌──────────┐             │
│  │ DbType  │ │ DbObject │ │SyncStatus│             │
│  │ (Enum)  │ │ DbIndex  │ │ Request  │             │
│  └─────────┘ └──────────┘ └──────────┘             │
├─────────────────────────────────────────────────────┤
│                    基礎設施層                         │
│  ┌─────────┐ ┌──────────┐ ┌───────────────────┐    │
│  │ Serilog │ │  Dapper  │ │ EmbeddedFileProvider│   │
│  └─────────┘ └──────────┘ └───────────────────┘    │
└─────────────────────────────────────────────────────┘
```

### 各層職責

| 層 | 職責 | 代表檔案 |
|---|------|---------|
| 前端 UI | 使用者互動、資料呈現 | `Index.cshtml`, `site.css` |
| API 控制器 | 接收 HTTP 請求、協調服務 | `DbController.cs` |
| 服務 | 封裝資料庫操作邏輯 | `SqlServerService.cs`, `PostgreSqlService.cs` |
| 領域模型 | 定義資料結構與列舉 | `DbModels.cs` |
| 基礎設施 | 日誌、ORM、檔案提供 | `DapperExtensions.cs`, `Program.cs` |

### 資料流

一次典型的「比較」操作，資料流如下：

```
使用者點擊「分析與比較」
    ↓
JavaScript 發送 POST /api/db/compare
    ↓
DbController.Compare() 接收請求
    ↓
根據 DbType 取得對應的 IDbService 實作
    ↓
Service.GetDbObjectsAsync() 查詢來源物件清單
    ↓
對每個物件，平行執行：
  ├─ Service.CheckObjectExistsAsync()  (目標)
  └─ Service.GetDependenciesAsync()    (來源)
    ↓
若為資料表，額外平行查詢：
  ├─ Service.GetRowCountAsync()       (來源 + 目標)
  └─ Service.GetTableIndexesAsync()   (來源 + 目標)
    ↓
組裝 SyncStatus 清單回傳
    ↓
JavaScript 渲染樹狀表格
```

## 專案目錄結構

```
DbCopy/
├── Controllers/           # API 控制器
│   └── DbController.cs
├── Models/                # 領域模型
│   └── DbModels.cs
├── Services/              # 服務層
│   ├── IDbService.cs      # 介面定義
│   ├── DapperExtensions.cs # Dapper 日誌包裝
│   ├── SqlServerService.cs # SQL Server 實作
│   └── PostgreSqlService.cs # PostgreSQL 實作
├── Pages/                 # Razor Pages
│   ├── Index.cshtml       # 主頁面
│   ├── Error.cshtml       # 錯誤頁面
│   └── Shared/
│       └── _Layout.cshtml # 版面配置
├── wwwroot/               # 靜態資源（會嵌入執行檔）
│   ├── css/site.css       # 自訂樣式
│   ├── js/site.js         # 自訂腳本
│   └── lib/               # 第三方函式庫
├── Program.cs             # 應用程式進入點
├── DbCopy.csproj          # 專案檔
├── publish.sh             # 跨平台發佈腳本
└── release.sh             # 語意化版本腳本
```

### 設計原則

1. **單一職責**：每個檔案只負責一件事。`DbModels.cs` 只定義模型、`DbController.cs` 只處理路由。
2. **依賴反轉**：控制器依賴 `IDbService` 介面，而非具體實作。
3. **最小化檔案數**：整個專案只有 7 個 C# 檔案。不過度拆分，不建立無用的抽象層。

---

> **下一章預告**：架構確定後，接下來要設計最核心的資料結構——如何用 C# 的型別系統來表達「資料庫物件」這個概念。
