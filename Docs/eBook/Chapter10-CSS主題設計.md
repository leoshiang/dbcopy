# CSS 主題設計

DbCopy 的視覺設計雖然只有一個 362 行的 CSS 檔案，卻涵蓋了完整的色彩系統、漸層配色、物件類型色碼、響應式設計。

## CSS 變數系統

所有顏色集中定義在 `:root` 偽類中，方便全域管理：

```css
:root {
    /* 主色調 */
    --db-primary: #2563eb;         /* 藍色 */
    --db-primary-hover: #1d4ed8;   /* 深藍色 */
    --db-primary-soft: #dbeafe;    /* 淺藍色 */

    /* 語意色彩 */
    --db-success: #059669;         /* 綠色 */
    --db-warning: #ea580c;         /* 橘色 */
    --db-danger: #dc2626;          /* 紅色 */
    --db-info: #0891b2;            /* 青色 */

    /* 強調色 */
    --db-indigo: #6366f1;          /* 靛藍色 */
    --db-purple: #7c3aed;          /* 紫色 */

    /* 灰階 */
    --db-gray-50: #f9fafb;
    --db-gray-100: #f3f4f6;
    --db-gray-200: #e5e7eb;
    --db-gray-300: #d1d5db;
    --db-gray-500: #6b7280;
    --db-gray-600: #4b5563;
    --db-gray-700: #374151;
    --db-gray-800: #1f2937;

    /* 物件類型色彩 */
    --color-table: #2563eb;        /* 藍色 — 資料表 */
    --color-view: #059669;         /* 綠色 — 檢視 */
    --color-procedure: #7c3aed;    /* 紫色 — 預存程序 */
    --color-function: #db2777;     /* 粉紅色 — 函數 */
    --color-type: #0891b2;         /* 青色 — 使用者定義型別 */
    --color-table-type: #0369a1;   /* 深青色 — 使用者定義資料表型別 */
}
```

### 色彩系統的設計原則

1. **Tailwind CSS 色盤**：所有顏色值取自 Tailwind CSS 的色盤系統，確保色彩的和諧度和對比度
2. **主色調為藍色**：藍色傳遞「專業」、「信任」的視覺訊號，適合工具類軟體
3. **每個物件類型一個顏色**：使用者可以一眼區分不同類型的物件，不需要閱讀文字
4. **三段式色階**：每個主要色彩提供 Normal、Hover、Soft 三個變體（如 `--db-primary`、`--db-primary-hover`、`--db-primary-soft`）

## 漸層配色方案

DbCopy 大量使用 CSS 漸層（Gradient），讓介面更具深度感和現代感：

### 背景漸層

```css
body {
    background: linear-gradient(135deg, #f0f9ff 0%, #f8fafc 50%, #fef3c7 100%);
    min-height: 100vh;
}
```

從左上到右下，由淺藍 → 灰白 → 淺黃的三段漸層，營造出溫暖而專業的背景。

### 按鈕漸層

```css
.btn-primary {
    background: linear-gradient(135deg, var(--db-primary) 0%, var(--db-indigo) 100%);
    border: none;
    box-shadow: 0 4px 6px -1px rgba(37, 99, 235, 0.3),
                0 2px 4px -1px rgba(37, 99, 235, 0.2);
    transition: all 0.3s ease;
}

.btn-primary:hover {
    background: linear-gradient(135deg, var(--db-primary-hover) 0%, var(--db-purple) 100%);
    transform: translateY(-2px);    /* 懸浮時微微上升 */
    box-shadow: 0 6px 8px -1px rgba(37, 99, 235, 0.4),
                0 4px 6px -1px rgba(37, 99, 235, 0.3);
}
```

**Hover 效果三重奏**：
1. 漸層色彩加深
2. 微微上移 2px（`translateY(-2px)`）
3. 陰影加強

### 表頭漸層

```css
.table thead th {
    background: linear-gradient(135deg, var(--db-primary) 0%, var(--db-indigo) 100%) !important;
    color: #ffffff !important;
    text-shadow: 0 1px 2px rgba(0, 0, 0, 0.2);
}
```

漸層表頭搭配白色文字和文字陰影，確保可讀性的同時增加視覺層次。

### 捲軸漸層

```css
::-webkit-scrollbar-thumb {
    background: linear-gradient(135deg, var(--db-primary) 0%, var(--db-indigo) 100%);
    border-radius: 5px;
}
```

即使是捲軸也保持與整體配色一致。

## 物件類型色彩編碼

不同類型的資料庫物件使用不同的色彩標記：

```css
.badge-table     { background-color: var(--color-table); }      /* 藍色 */
.badge-view      { background-color: var(--color-view); }       /* 綠色 */
.badge-procedure { background-color: var(--color-procedure); }  /* 紫色 */
.badge-function  { background-color: var(--color-function); }   /* 粉紅色 */
.badge-type      { background-color: var(--color-type); }       /* 青色 */
.badge-table-type { background-color: var(--color-table-type); } /* 深青色 */
```

### 樹狀表格的列樣式

```css
/* Schema 列 — 淺藍色背景 + 左邊框 */
.schema-row {
    background: linear-gradient(90deg, #dbeafe 0%, #e0e7ff 100%) !important;
    border-left: 3px solid var(--db-primary);
    font-weight: 600;
}

/* 類型列 — 更淺的背景 */
.type-row {
    background: linear-gradient(90deg, #f0f9ff 0%, #fef3c7 100%) !important;
    border-left: 3px solid var(--db-indigo);
}

/* 物件列 — 白色背景 */
.object-row {
    border-left: 4px solid transparent;
    background-color: #ffffff;
}

/* 物件列 Hover — 淺藍到淺黃的漸層 */
.object-row:hover {
    background: linear-gradient(90deg, #dbeafe 0%, #fef9c3 100%) !important;
    border-left-color: var(--db-warning);
    transition: all 0.2s ease-in-out;
}

/* 索引列 — 淺黃色背景 + 斜體 */
.index-row {
    background-color: #fefce8 !important;
    font-style: italic;
    border-left: 3px solid var(--db-warning-soft);
}
```

每種列類型使用不同的左邊框顏色和背景漸層，讓使用者一眼就能區分層級。

### 狀態 Badge

```css
/* 「已存在」— 橘色外框 */
.badge-outline-warning {
    color: #92400e;
    border: 1px solid #f59e0b;
    background: linear-gradient(135deg, #fffbeb 0%, #fef3c7 100%);
    font-weight: 600;
}

/* 「不存在」— 綠色外框 */
.badge-outline-success {
    color: #065f46;
    border: 1px solid #10b981;
    background: linear-gradient(135deg, #ecfdf5 0%, #d1fae5 100%);
    font-weight: 600;
}

/* 「準備就緒」— 灰色外框 */
.badge-outline-secondary {
    color: var(--db-gray-700);
    border: 1px solid var(--db-gray-300);
    background: linear-gradient(135deg, #f9fafb 0%, #e5e7eb 100%);
    font-weight: 600;
}
```

## 響應式設計考量

### 字型大小適應

```css
html {
    font-size: 14px;
}

@media (min-width: 768px) {
    html {
        font-size: 16px;
    }
}
```

小螢幕使用 14px 基礎字型大小，平板以上使用 16px。使用 `rem` 單位的元素會自動按比例縮放。

### 表格滾動區域

```html
<div class="table-responsive" style="max-height: calc(100vh - 310px); overflow-y: auto;">
```

表格區域使用 `calc(100vh - 310px)` 計算最大高度，確保在任何螢幕大小下都不會超出可視範圍。`310px` 是工具列 + 篩選列 + 統計列的約略高度。

### 字型選擇

```css
body {
    font-family: 'Inter', -apple-system, BlinkMacSystemFont, "Segoe UI",
                 Roboto, Helvetica, Arial, sans-serif;
}
```

**Inter** 字型是專為 UI 設計的開源字型，數字字距等寬（tabular numbers），非常適合顯示資料表格。透過 Google Fonts CDN 載入，並提供系統字型作為 fallback。

### Modal 對話框

```css
.modal-header {
    background: linear-gradient(135deg, var(--db-primary) 0%, var(--db-indigo) 100%);
    color: #ffffff;
    border-bottom: 2px solid var(--db-primary-hover);
}

.modal-header .btn-close {
    filter: brightness(0) invert(1);    /* 白色關閉按鈕 */
}

.modal-header.bg-danger {
    background: linear-gradient(135deg, var(--db-danger) 0%, #ef4444 100%) !important;
}
```

一般 Modal 使用藍色漸層表頭，錯誤 Modal 使用紅色漸層表頭。`filter: brightness(0) invert(1)` 是一個巧妙的技巧——將深色關閉按鈕反轉為白色，不需要替換圖示。

---

> **下一章預告**：UI 層的設計告一段落。第 11 章將深入 `Program.cs`——應用程式的進入點，包括 Serilog 配置、嵌入式靜態檔案、連接埠衝突自動偵測等基礎設施。
