# 跨平台建置與發佈

## 專案檔設定解析

`DbCopy.csproj` 是整個專案的配置核心：

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <GenerateEmbeddedFilesManifest>true</GenerateEmbeddedFilesManifest>
    <ApplicationIcon>app.ico</ApplicationIcon>
    <Deterministic>false</Deterministic>
    <AssemblyVersion>1.0.0.*</AssemblyVersion>
    <FileVersion>1.0.0.*</FileVersion>
    <NoWarn>$(NoWarn);CS7035</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Dapper" Version="2.1.66" />
    <PackageReference Include="Microsoft.Data.SqlClient" Version="6.1.4" />
    <PackageReference Include="Microsoft.Extensions.FileProviders.Embedded" Version="10.0.0" />
    <PackageReference Include="Npgsql" Version="10.0.1" />
    <PackageReference Include="Serilog.AspNetCore" Version="10.0.0" />
    <PackageReference Include="Serilog.Sinks.File" Version="7.0.0" />
  </ItemGroup>

  <ItemGroup>
    <EmbeddedResource Include="wwwroot\**" />
  </ItemGroup>
</Project>
```

### 各設定項解析

| 設定 | 說明 |
|------|------|
| `TargetFramework=net10.0` | 目標框架為 .NET 10.0 |
| `Nullable=enable` | 啟用 C# 可空參考型別分析 |
| `ImplicitUsings=enable` | 自動匯入常用命名空間 |
| `GenerateEmbeddedFilesManifest=true` | 為嵌入資源產生清單檔 |
| `ApplicationIcon=app.ico` | Windows 執行檔圖示 |
| `Deterministic=false` | 允許 `*` 萬用字元版本號 |
| `AssemblyVersion=1.0.0.*` | 建置版本自動遞增 |
| `NoWarn=CS7035` | 抑制萬用字元版本號的警告 |

### 版本號的萬用字元

```
AssemblyVersion=1.0.0.*
```

`*` 會在每次建置時自動替換為基於日期和時間的數值：
- 第三段（Build）：自 2000-01-01 以來的天數
- 第四段（Revision）：當天午夜以來的秒數 / 2

這讓每次建置都有唯一的版本號，方便追蹤問題。

### 套件依賴

| 套件 | 版本 | 用途 |
|------|------|------|
| Dapper | 2.1.66 | 微型 ORM |
| Microsoft.Data.SqlClient | 6.1.4 | SQL Server 連線驅動 |
| Microsoft.Extensions.FileProviders.Embedded | 10.0.0 | 嵌入式靜態檔案 |
| Npgsql | 10.0.1 | PostgreSQL 連線驅動 |
| Serilog.AspNetCore | 10.0.0 | 結構化日誌 |
| Serilog.Sinks.File | 7.0.0 | 檔案日誌輸出 |

**依賴最小化原則**：只使用必要的套件，不引入不需要的框架（如 Entity Framework、AutoMapper 等）。總共只有 6 個直接依賴。

## 五平台單一執行檔發佈

`publish.sh` 是一個簡潔的 Bash 腳本（37 行），為五個目標平台產出獨立的單一執行檔：

```bash
#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$ROOT_DIR/DbCopy.csproj"
PUBLISH_DIR="$ROOT_DIR/publish"

RIDS=(
    "win-x64"
    "linux-x64"
    "linux-arm64"
    "osx-x64"
    "osx-arm64"
)

rm -rf "$PUBLISH_DIR"
mkdir -p "$PUBLISH_DIR"

for rid in "${RIDS[@]}"; do
    echo "Publishing for $rid..."
    dotnet publish "$PROJECT" \
        -c Release \
        -r "$rid" \
        --self-contained true \
        -p:PublishSingleFile=true \
        -p:EnableCompressionInSingleFile=true \
        -o "$PUBLISH_DIR/$rid"
done
```

### 發佈參數解析

| 參數 | 說明 |
|------|------|
| `-c Release` | 使用 Release 配置（啟用最佳化） |
| `-r win-x64` | 目標執行環境（Runtime Identifier） |
| `--self-contained true` | 包含 .NET Runtime（不需要預先安裝） |
| `-p:PublishSingleFile=true` | 打包成單一執行檔 |
| `-p:EnableCompressionInSingleFile=true` | 壓縮單一檔案（減少約 30% 大小） |

### 五個目標平台

| RID | 作業系統 | 架構 | 常見裝置 |
|-----|---------|------|---------|
| `win-x64` | Windows | Intel/AMD 64bit | 大多數 Windows PC |
| `linux-x64` | Linux | Intel/AMD 64bit | 伺服器、WSL |
| `linux-arm64` | Linux | ARM 64bit | 樹莓派 4、AWS Graviton |
| `osx-x64` | macOS | Intel | 2020 前的 Mac |
| `osx-arm64` | macOS | Apple Silicon | M1/M2/M3/M4 Mac |

### 產出結構

```
publish/
├── win-x64/
│   └── DbCopy.exe          (~60 MB)
├── linux-x64/
│   └── DbCopy              (~60 MB)
├── linux-arm64/
│   └── DbCopy              (~55 MB)
├── osx-x64/
│   └── DbCopy              (~60 MB)
└── osx-arm64/
    └── DbCopy              (~55 MB)
```

每個目標資料夾只有一個檔案。使用者下載後直接執行即可，不需要安裝 .NET Runtime。

## 語意化版本控制

`release.sh`（67 行）是一個互動式腳本，實作了語意化版本控制（Semantic Versioning）：

```bash
#!/usr/bin/env bash
set -euo pipefail

# 1. 確保工作目錄乾淨
if ! git diff --quiet --ignore-submodules HEAD --; then
    echo "Working tree has uncommitted changes." >&2
    exit 1
fi

# 2. 取得最新的版本標籤（比較本地和遠端）
latest_local_tag="$(git tag --list 'v[0-9]*.[0-9]*.[0-9]*' --sort=-v:refname | head -n 1)"
latest_remote_tag="$(git ls-remote --tags --refs origin 'v[0-9]*.[0-9]*.[0-9]*' ...)"

# 3. 取較大的版本號
latest_tag="$(printf '%s\n%s\n' "$latest_local_tag" "$latest_remote_tag" | sort -V | tail -n 1)"

# 4. 建議下一個版本號（Patch +1）
if [[ "$latest_tag" =~ ^v([0-9]+)\.([0-9]+)\.([0-9]+)$ ]]; then
    suggested_tag="v${major}.${minor}.$((patch + 1))"
fi

# 5. 互動式確認
read -r -p "Release tag [${suggested_tag}]: " input_tag
tag="${input_tag:-$suggested_tag}"

# 6. 驗證標籤格式和唯一性
if git rev-parse -q --verify "refs/tags/$tag" >/dev/null; then
    echo "Tag already exists locally: $tag" >&2
    exit 1
fi

# 7. 建立並推送標籤
git tag "$tag"
git push origin "$tag"
```

### 安全機制

1. **工作目錄檢查**：未提交的變更和未追蹤的檔案都會阻止發佈
2. **本地 + 遠端比較**：取兩者中較大的版本號，防止多人同時發佈時版本衝突
3. **格式驗證**：確保標籤符合 `v<major>.<minor>.<patch>` 格式
4. **重複檢查**：確保標籤在本地和遠端都不存在

## 嵌入式資源的運作原理

### 建置時流程

```
原始碼                    建置輸出
─────────                ─────────────
wwwroot/                 DbCopy.dll
├── css/site.css    →    ├── 嵌入資源: DbCopy.wwwroot.css.site.css
├── js/site.js      →    ├── 嵌入資源: DbCopy.wwwroot.js.site.js
├── lib/            →    ├── 嵌入資源: DbCopy.wwwroot.lib.*
└── favicon.ico     →    └── 嵌入資源: DbCopy.wwwroot.favicon.ico
```

`GenerateEmbeddedFilesManifest=true` 會額外產生一個清單（manifest），記錄所有嵌入資源的原始路徑和組件中的名稱對應。

### 執行時流程

```
瀏覽器請求: GET /css/site.css
    ↓
ASP.NET Core Static Files 中介軟體
    ↓
ManifestEmbeddedFileProvider
    ↓
查詢清單: /css/site.css → DbCopy.wwwroot.css.site.css
    ↓
從 Assembly 載入嵌入資源
    ↓
回傳 HTTP Response（Content-Type: text/css）
```

### 與 Single File Publishing 的配合

當使用 `PublishSingleFile=true` 時：

1. 所有 .dll（包含嵌入資源的 DbCopy.dll）被打包進一個 .exe
2. 執行時 .NET Runtime 從 .exe 中解壓 .dll
3. `ManifestEmbeddedFileProvider` 從 .dll 中讀取嵌入資源

整個鏈條確保了「一個檔案即可運行」的目標。

---

> **下一章預告**：程式碼和部署都完成了。第 13 章將回顧安全性設計——DbCopy 如何防範 SQL 注入、處理識別符跳脫、保護連線字串。
