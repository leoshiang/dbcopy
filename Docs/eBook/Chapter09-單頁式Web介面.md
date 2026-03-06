# 單頁式 Web 介面

DbCopy 的整個 UI 集中在一個 Razor Page（`Index.cshtml`，約 800 行）中。它結合了 Razor 伺服端渲染和 jQuery 前端互動，實現了一個功能完整的單頁應用。

## Razor Pages 與前端 JavaScript 的協作

DbCopy 採用的是 **Razor Pages 負責結構、JavaScript 負責行為** 的協作模式：

| 職責 | 技術 | 範例 |
|------|------|------|
| HTML 結構 | Razor | 工具列、表格骨架、Modal 對話框 |
| 動態資料 | jQuery + Fetch API | 載入物件清單、更新狀態 |
| 狀態管理 | localStorage | 儲存連線設定 |
| 使用者互動 | jQuery 事件處理 | 按鈕點擊、篩選、全選 |

### 為什麼不用 Razor 的 OnPost Handler？

Razor Pages 提供了 `OnPostAsync` 等伺服端處理方法，但 DbCopy 完全不使用它們。原因是：

1. **即時回饋**：使用者需要看到即時的進度更新（進度條、狀態變更），Form Post 會導致頁面重新載入
2. **批次操作**：同步操作需要逐一發送請求並更新 UI，不適合 Form Post
3. **狀態保持**：使用 JavaScript 管理頁面狀態，比 Razor 的 ViewData 更靈活

## 連線管理系統

連線管理是 DbCopy 的第一道門——使用者必須先設定好連線，才能進行比較和同步。

### 資料結構

連線資訊以 JSON 陣列儲存在 `localStorage` 中：

```javascript
// localStorage 中的格式
[
    {
        "id": "conn_1710000000000",
        "name": "本地開發 SQL",
        "type": 0,                    // 0: SQL Server, 1: PostgreSQL
        "connectionString": "Server=localhost;Database=MyDb;...",
        "readOnly": false
    },
    ...
]
```

### 唯讀連線

連線設定支援「唯讀」標記：

```javascript
// 唯讀連線不會出現在目的地下拉選單中
if (conn.id !== currentSource && !conn.readOnly) {
    targetSelect.append(`<option value="${conn.id}">${conn.name}</option>`);
}
```

這是一個安全機制——標記為唯讀的連線只能作為來源，永遠不會被誤用為同步目標。

### 匯入匯出

連線設定支援匯出為 JSON 檔案和匯入：

```javascript
function exportConnections() {
    const data = JSON.stringify(connections, null, 2);
    const blob = new Blob([data], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `db-connections-${timestamp}.json`;
    a.click();
}
```

匯入時使用智慧合併邏輯——以 `(type, name)` 為鍵：
- 新名稱 → 新增
- 同名稱 → 更新連線字串
- 無效資料 → 略過

```javascript
const keyOf = (c) => `${c.type}|${c.name.trim()}`;
const map = new Map(connections.map(c => [keyOf(c), c]));

imported.forEach(c => {
    const key = keyOf(normalized);
    if (map.has(key)) {
        // 更新現有連線
        existing.connectionString = normalized.connectionString;
        updated++;
    } else {
        // 新增連線
        connections.push(normalized);
        added++;
    }
});
```

## 分析與比較流程

使用者點擊「分析與比較」按鈕後的完整流程：

```
1. 驗證來源和目標已選擇
2. 驗證來源和目標類型相同
3. 顯示載入中動畫
4. 發送 POST /api/db/compare
5. 接收 SyncStatus[] 結果
6. 呼叫 renderMatrixUI() 渲染表格
```

```javascript
$('#compareBtn').click(async function() {
    const sourceId = $('#sourceConnSelect').val();
    const targetId = $('#targetConnSelect').val();

    if (!sourceId || !targetId) {
        alert('請選擇來源與目的連線。');
        return;
    }

    const source = connections.find(c => c.id === sourceId);
    const target = connections.find(c => c.id === targetId);

    if (source.type !== target.type) {
        alert('來源與目的資料庫類型必須相同。');
        return;
    }

    // 顯示載入動畫
    $('#syncTable tbody').empty().append(
        '<tr><td colspan="6" class="p-5 text-center">' +
        '<div class="spinner-border text-primary"></div> ' +
        '正在分析來源與目的資料庫...</td></tr>');

    const response = await fetch('/api/db/compare', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ source, target })
    });

    syncItems = await response.json();
    renderMatrixUI();
});
```

## 樹狀結構的結果表格

比較結果以**三層樹狀結構**呈現：

```
Schema (dbo)                          ← Schema 層
├── Tables (3)                        ← 類型層
│   ├── ☑ Users          [不存在]     ← 物件層
│   │   └── IX_Users_Email [✓]       ← 索引層（子項）
│   ├── ☐ Orders         [已存在]
│   └── ☑ Products       [不存在]
├── Views (1)
│   └── ☑ vActiveUsers   [不存在]
└── Procedures (2)
    ├── ☑ GetUser        [不存在]
    └── ☑ UpdateUser     [不存在]
```

### 分組邏輯

```javascript
function renderMatrixUI() {
    const schemaGroups = {};

    syncItems.forEach((item, index) => {
        // 套用篩選條件
        if (filterType !== "" && item.sourceObject.type.toString() !== filterType) return;
        if (filterStatus === "exists" && !item.existsInDestination) return;
        if (filterStatus === "missing" && item.existsInDestination) return;
        if (filterSync !== "" && item.status !== filterSync) return;

        const schema = item.sourceObject.schema;
        if (!schemaGroups[schema]) schemaGroups[schema] = {};

        const type = item.sourceObject.type;
        if (!schemaGroups[schema][type]) schemaGroups[schema][type] = [];

        schemaGroups[schema][type].push({ ...item, originalIndex: index });
    });
    ...
}
```

### 展開/收合

使用 Bootstrap 5 的 Collapse 元件實現樹狀展開/收合：

```html
<!-- Schema 列：點擊可收合所有子項 -->
<tr data-bs-toggle="collapse" data-bs-target=".schema-dbo">
    <td><i class="bi bi-chevron-down"></i></td>
    <td colspan="4">dbo</td>
</tr>

<!-- 子項：透過 CSS class 控制顯示 -->
<tr class="collapse show schema-dbo">...</tr>
```

### 勾選機制

三層勾選盒（全選、Schema 全選、類型全選、個別物件）彼此連動：

```javascript
// Schema 勾選 → 全選該 Schema 下所有物件
$('.schema-check').change(function() {
    const schema = $(this).data('schema');
    const checked = $(this).is(':checked');
    $(`.sync-check[data-schema="${schema}"]:not(:disabled)`).prop('checked', checked);
});

// 類型勾選 → 全選該類型下所有物件
$('.type-check').change(function() {
    const schema = $(this).data('schema');
    const type = $(this).data('type');
    const checked = $(this).is(':checked');
    $(`.sync-check[data-schema="${schema}"][data-type="${type}"]:not(:disabled)`)
        .prop('checked', checked);
});
```

**`disabled` 的物件不會被勾選**——已存在於目標的物件（`existsInDestination = true`）的勾選盒是 disabled 的，無法被選中進行同步。

## 批次同步與進度追蹤

### 同步流程

```javascript
$('#syncSelectedBtn').click(async function() {
    if (!await showConfirm('同步確認', `確定要同步 ${selected.length} 個物件？`, '開始同步'))
        return;

    isSyncing = true;
    stopRequested = false;

    // 顯示進度條
    $('#syncProgressWrapper').removeClass('d-none');
    updateSyncProgress(0, selected.length);

    for (let i = 0; i < selected.length; i++) {
        if (stopRequested) break;  // 支援中途中斷

        const item = syncItems[idx];
        updateSyncProgress(i + 1, selected.length);

        // 更新該列的狀態為「同步中」
        $(`#sync-row-${idx} .status-cell`).html('⏳ 同步中...');

        try {
            const response = await fetch('/api/db/copy', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    source, target,
                    object: item.sourceObject,
                    phase: 0,
                    batchSize: parseInt($('#batchSizeInput').val())
                })
            });

            if (response.ok) {
                item.status = 'Success';
                // 更新 UI 顯示成功
            } else {
                item.status = 'Error';
                item.message = await response.text();
                // 更新 UI 顯示錯誤
            }
        } catch (err) {
            item.status = 'Error';
            item.message = err.message;
        }
    }

    isSyncing = false;
    // 顯示結果摘要
    showSyncResultBanner(...);
});
```

### 中斷機制

同步過程中顯示「中斷」按鈕，使用者可以隨時停止：

```javascript
$('#stopSyncBtn').click(function() {
    stopRequested = true;
});

// 迴圈中檢查
for (let i = 0; i < selected.length; i++) {
    if (stopRequested) break;
    ...
}
```

### 進度條

```javascript
function updateSyncProgress(completed, total) {
    var pct = total > 0 ? Math.round(completed / total * 100) : 0;
    $('#syncProgressBar').css('width', pct + '%').attr('aria-valuenow', pct);
    $('#syncProgressText').text(pct + '%');
}
```

使用 Bootstrap 5 的 Progress Bar 元件，搭配 `progress-bar-striped progress-bar-animated` 類別產生動態條紋效果。

## 匯入匯出連線設定

### 自訂確認對話框

DbCopy 不使用瀏覽器內建的 `confirm()` 對話框，而是使用自訂的 Bootstrap Modal：

```javascript
function showConfirm(title, message, confirmText, confirmClass) {
    return new Promise(function(resolve) {
        var resolved = false;
        function done(result) {
            if (!resolved) { resolved = true; resolve(result); }
        }

        $('#confirmModalTitle').text(title);
        $('#confirmModalMessage').text(message);
        $('#confirmModalConfirmBtn')
            .attr('class', 'btn btn-sm rounded-pill px-3 ' + confirmClass)
            .text(confirmText);

        var modal = bootstrap.Modal.getOrCreateInstance(modalEl, {
            backdrop: 'static', keyboard: false
        });

        $('#confirmModalConfirmBtn').on('click.confirm', () => { modal.hide(); done(true); });
        $('#confirmModalCancelBtn').on('click.confirm', () => { modal.hide(); done(false); });
        $(modalEl).on('hidden.bs.modal.confirm', () => { done(false); });

        modal.show();
    });
}
```

**設計要點**：
- 回傳 `Promise<boolean>`，可以用 `await` 語法等待使用者選擇
- `backdrop: 'static'` 防止點擊背景關閉
- 命名空間事件（`.confirm`）避免事件衝突
- `resolved` 旗標確保 Promise 只被 resolve 一次

### 篩選系統

三個下拉式篩選器可以組合使用：

```html
<select id="filterObjectType">
    <option value="">所有類型</option>
    <option value="0">Type</option>
    <option value="1">Table Type</option>
    <option value="2">Table</option>
    ...
</select>

<select id="filterTargetStatus">
    <option value="">所有目的狀態</option>
    <option value="missing">不存在 (Missing)</option>
    <option value="exists">已存在 (Exists)</option>
</select>

<select id="filterSyncStatus">
    <option value="">所有同步狀態</option>
    <option value="Pending">準備就緒</option>
    <option value="Success">成功</option>
    <option value="Error">錯誤</option>
</select>
```

篩選邏輯在 `renderMatrixUI()` 中實作，每次篩選條件變更時重新渲染表格。

### 統計列

頁面底部顯示即時統計：

```html
<span>共 <strong id="statTotal">0</strong> 個物件</span>
<span>篩選 <strong id="statFiltered">0</strong> 個</span>
<span>已選 <strong id="statSelected">0</strong> 個</span>
```

---

> **下一章預告**：UI 的結構已經完成，但一個好的工具需要好的視覺設計。第 10 章將介紹 DbCopy 的 CSS 主題設計——從 CSS 變數系統到漸層配色方案。
