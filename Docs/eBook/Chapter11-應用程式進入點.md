# Program.cs — 應用程式進入點

`Program.cs`（182 行）是 DbCopy 的起點。它負責設定日誌、註冊服務、配置中介軟體、處理連接埠衝突、啟動 Web 伺服器，並在啟動後自動開啟瀏覽器。

## Serilog 日誌配置

```csharp
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("Logs/log-.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();
```

### 為什麼選擇 Serilog？

ASP.NET Core 內建的 `Microsoft.Extensions.Logging` 提供了日誌抽象層，但它的輸出格式和目的地（Sink）選項有限。Serilog 提供了：

| 功能 | 內建 Logging | Serilog |
|------|-------------|---------|
| 結構化日誌 | 基本支援 | 完整支援 |
| Console 輸出 | 有 | 有（更美觀） |
| 檔案輸出 | 無 | 有（含滾動） |
| 滾動策略 | 無 | Day/Hour/Size |
| 訊息模板 | 基本 | 豐富 |

### 日誌輸出配置

- **Console**：開發時在終端機中即時查看日誌
- **File**：`Logs/log-20260317.txt` 格式的每日滾動檔案，方便事後排查問題

`RollingInterval.Day` 表示每天建立一個新的日誌檔案，檔名包含日期。

## 依賴注入容器設定

```csharp
builder.Services.AddRazorPages();          // 啟用 Razor Pages
builder.Services.AddControllers();          // 啟用 API Controllers
builder.Services.AddScoped<SqlServerService>();    // 每個 HTTP 請求一個實例
builder.Services.AddScoped<PostgreSqlService>();   // 每個 HTTP 請求一個實例
```

### 生命週期選擇：Scoped

| 生命週期 | 說明 | 適用場景 |
|---------|------|---------|
| Singleton | 整個應用程式生命週期只有一個實例 | 無狀態的工具類 |
| **Scoped** | **每個 HTTP 請求一個實例** | **含有 Logger 的服務** |
| Transient | 每次注入都建立新實例 | 輕量級無狀態服務 |

`SqlServerService` 和 `PostgreSqlService` 使用 `Scoped` 是因為它們注入了 `ILogger<T>`。Logger 本身是 Scoped 的，所以使用它的服務也應該是 Scoped 的。

### 中介軟體管線

```csharp
var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.UseHttpsRedirection();     // 僅在非開發環境啟用 HTTPS 重導向
}

app.UseRouting();
// 注意：沒有 UseAuthorization() — 因為沒有配置認證/授權方案

// 嵌入式靜態檔案
var embeddedProvider = new ManifestEmbeddedFileProvider(
    typeof(Program).Assembly, "wwwroot");
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = embeddedProvider
});

app.MapRazorPages();     // 映射 Razor Pages 路由
app.MapControllers();    // 映射 API Controllers 路由
```

**設計決策**：

1. **不使用 `UseAuthorization()`**：DbCopy 不需要認證/授權，因為它是本地工具。加上這個中介軟體只會造成不必要的警告。
2. **HTTPS 重導向僅在生產環境**：開發時使用 HTTP 更方便，避免憑證問題。
3. **路由順序**：先靜態檔案、再 Razor Pages、最後 API Controllers。

## 嵌入式靜態檔案

這是 DbCopy 實現「單一執行檔」的核心技術。

### 問題

傳統的 ASP.NET Core 應用在部署時需要一個 `wwwroot` 資料夾，裡面放著 CSS、JavaScript、圖片等靜態檔案。但 DbCopy 的目標是一個單一的 `.exe` 檔案，不需要額外的資料夾。

### 解決方案

**步驟一**：在 `.csproj` 中將 `wwwroot` 所有檔案標記為嵌入資源：

```xml
<PropertyGroup>
    <GenerateEmbeddedFilesManifest>true</GenerateEmbeddedFilesManifest>
</PropertyGroup>

<ItemGroup>
    <EmbeddedResource Include="wwwroot\**" />
</ItemGroup>
```

**步驟二**：在 `Program.cs` 中使用 `ManifestEmbeddedFileProvider`：

```csharp
var embeddedProvider = new ManifestEmbeddedFileProvider(
    typeof(Program).Assembly, "wwwroot");

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = embeddedProvider
});
```

### 運作原理

1. **建置時**：MSBuild 將 `wwwroot/**` 的所有檔案嵌入到組件（.dll）中
2. **執行時**：`ManifestEmbeddedFileProvider` 從組件中讀取嵌入的檔案
3. **瀏覽器請求**：`/css/site.css` → 中介軟體從組件中找到嵌入的 `wwwroot/css/site.css` → 回傳給瀏覽器

這樣一來，即使只有一個 `.exe` 檔案，所有靜態資源都可以正常存取。

### 需要的 NuGet 套件

```xml
<PackageReference Include="Microsoft.Extensions.FileProviders.Embedded" Version="10.0.0" />
```

## 連接埠衝突自動偵測

當使用者同時運行多個 DbCopy 實例，或其他服務已佔用預設連接埠時，程式會自動尋找可用的連接埠：

```csharp
var configuredUrls = builder.Configuration["urls"];
if (!string.IsNullOrWhiteSpace(configuredUrls))
{
    var adjustedUrls = AdjustUrlsForPortConflicts(configuredUrls);
    if (!string.Equals(adjustedUrls, configuredUrls, StringComparison.Ordinal))
    {
        builder.WebHost.UseUrls(adjustedUrls);
        Log.Warning("Detected occupied port(s). Switched URLs from {OriginalUrls} to {AdjustedUrls}",
            configuredUrls, adjustedUrls);
    }
}
```

### 核心演算法

```csharp
private static string AdjustUrlsForPortConflicts(string urls)
{
    var candidates = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | ...);
    var reservedPorts = new HashSet<int>();  // 已預留的連接埠
    var adjusted = new string[candidates.Length];

    for (var i = 0; i < candidates.Length; i++)
    {
        if (!Uri.TryCreate(current, UriKind.Absolute, out var uri)) { ... continue; }
        if (!IsLocalHost(uri.Host)) { ... continue; }

        var port = uri.Port;
        if (reservedPorts.Contains(port) || !IsPortAvailable(port))
        {
            var newPort = FindNextAvailablePort(port + 1, reservedPorts);
            var builder = new UriBuilder(uri) { Port = newPort };
            adjusted[i] = builder.Uri.ToString().TrimEnd('/');
            reservedPorts.Add(newPort);
        }
        else
        {
            adjusted[i] = current;
            reservedPorts.Add(port);
        }
    }

    return string.Join(';', adjusted);
}
```

### 連接埠可用性檢查

```csharp
private static bool IsPortAvailable(int port)
{
    return TryBind(IPAddress.Loopback, port)         // IPv4: 127.0.0.1
        && TryBind(IPAddress.IPv6Loopback, port);    // IPv6: ::1
}

private static bool TryBind(IPAddress ipAddress, int port)
{
    try
    {
        using var listener = new TcpListener(ipAddress, port);
        listener.Start();
        listener.Stop();
        return true;
    }
    catch (SocketException) { return false; }
}
```

透過實際嘗試綁定 TCP 連接埠來檢查可用性。需要同時檢查 IPv4 和 IPv6，因為某些系統只佔用了其中一個。

### reservedPorts 的作用

當配置了多個 URL（如 `http://localhost:5281;https://localhost:7256`）時，`reservedPorts` 集合確保自動選擇的新連接埠不會與已調整的連接埠衝突。

## 自動開啟瀏覽器

```csharp
app.Lifetime.ApplicationStarted.Register(() =>
{
    var url = app.Urls.FirstOrDefault() ?? "http://localhost:5000";
    Log.Information("Opening browser at {Url}", url);
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = url,
        UseShellExecute = true
    });
});
```

### 設計要點

1. **`ApplicationStarted` 事件**：確保在 Web 伺服器完全啟動後才開啟瀏覽器，避免瀏覽器開啟時伺服器尚未就緒
2. **`UseShellExecute = true`**：告訴作業系統用預設瀏覽器開啟 URL。這在 Windows、macOS、Linux 上都能正確運作
3. **`app.Urls.FirstOrDefault()`**：使用實際綁定的 URL（可能已經被連接埠衝突調整過），而非配置的 URL

### 跨平台相容性

`Process.Start` 搭配 `UseShellExecute = true`：
- **Windows**：等同於 `start http://localhost:5281`
- **macOS**：等同於 `open http://localhost:5281`
- **Linux**：等同於 `xdg-open http://localhost:5281`

.NET 的 `UseShellExecute` 會自動使用對應平台的機制。

### 全域錯誤處理

```csharp
try
{
    Log.Information("Starting web host");
    ...
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();    // 確保所有日誌都寫入磁碟
}
```

`Log.CloseAndFlush()` 非常重要——Serilog 使用非同步緩衝寫入檔案，如果不在應用程式結束前刷新，最後幾行日誌可能會遺失。

---

> **下一章預告**：程式碼已經完成，接下來要把它變成可以發佈的產品。第 12 章將介紹跨平台建置與發佈——如何用一個腳本為五個平台產出單一執行檔。
