#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$ROOT_DIR/DbCopy.csproj"
PUBLISH_DIR="$ROOT_DIR/Publish"
APP_NAME="DbCopy"
BUNDLE_ID="com.dbcopy.app"

RIDS=(
  "win-x64"
  "win-arm64"
  "osx-x64"
  "osx-arm64"
  "linux-x64"
  "linux-arm64"
)

if [[ ! -f "$PROJECT" ]]; then
  echo "找不到專案檔: $PROJECT" >&2
  exit 1
fi

echo "=============================="
echo "  DbCopy 跨平台發佈腳本"
echo "=============================="
echo ""

echo "清除發佈目錄: $PUBLISH_DIR"
rm -rf "$PUBLISH_DIR"
mkdir -p "$PUBLISH_DIR"

# ── 建立 macOS .app bundle ──
create_macos_app() {
  local rid="$1"
  local bin_dir="$PUBLISH_DIR/$rid"
  local app_dir="$PUBLISH_DIR/${APP_NAME}-${rid}.app"
  local contents="$app_dir/Contents"
  local macos_dir="$contents/MacOS"
  local res_dir="$contents/Resources"

  mkdir -p "$macos_dir" "$res_dir"

  # Info.plist
  cat > "$contents/Info.plist" << PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>${APP_NAME}</string>
  <key>CFBundleDisplayName</key>
  <string>資料庫同步工具</string>
  <key>CFBundleIdentifier</key>
  <string>${BUNDLE_ID}</string>
  <key>CFBundleVersion</key>
  <string>1.0.0</string>
  <key>CFBundleShortVersionString</key>
  <string>1.0.0</string>
  <key>CFBundleExecutable</key>
  <string>${APP_NAME}</string>
  <key>CFBundleIconFile</key>
  <string>app</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>LSMinimumSystemVersion</key>
  <string>12.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.developer-tools</string>
</dict>
</plist>
PLIST

  # 複製執行檔到 MacOS 目錄
  cp "$bin_dir/$APP_NAME" "$macos_dir/$APP_NAME"
  chmod +x "$macos_dir/$APP_NAME"

  # 複製 icns 圖示
  if [[ -f "$ROOT_DIR/app.icns" ]]; then
    cp "$ROOT_DIR/app.icns" "$res_dir/app.icns"
  fi

  # 移除散裝的 bin 目錄
  rm -rf "$bin_dir"

  local SIZE
  SIZE=$(du -sh "$app_dir" | cut -f1)
  echo "  ✓ $rid .app bundle 完成 ($SIZE)"
}

for rid in "${RIDS[@]}"; do
  echo "▶ 發佈 $rid ..."
  dotnet publish "$PROJECT" \
    -c Release \
    -r "$rid" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:EnableCompressionInSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "$PUBLISH_DIR/$rid" \
    --nologo \
    -v quiet

  if [[ "$rid" == osx-* ]]; then
    # macOS: 打包為 .app bundle
    create_macos_app "$rid"
  else
    # Windows / Linux: 顯示執行檔大小
    if [[ "$rid" == win-* ]]; then
      EXE="$PUBLISH_DIR/$rid/${APP_NAME}.exe"
    else
      EXE="$PUBLISH_DIR/$rid/${APP_NAME}"
    fi

    if [[ -f "$EXE" ]]; then
      SIZE=$(du -sh "$EXE" | cut -f1)
      echo "  ✓ $rid 完成 ($SIZE)"
    else
      echo "  ✗ $rid 找不到執行檔"
    fi
  fi
  echo ""
done

echo "=============================="
echo "  發佈完成！"
echo "  輸出目錄: $PUBLISH_DIR"
echo "=============================="
echo ""
echo "產出列表:"
for entry in "$PUBLISH_DIR"/*; do
  SIZE=$(du -sh "$entry" | cut -f1)
  printf "  %-35s %s\n" "$(basename "$entry")" "$SIZE"
done
