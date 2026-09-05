#!/usr/bin/env bash
# macOS 打包：Release 自包含发布 → 组装 .app → 生成 DMG
# 用法: scripts/package-macos.sh [RID] [版本号]   （RID 默认 osx-arm64）
set -euo pipefail
RID="${1:-osx-arm64}"
VERSION="${2:-1.0.0}"

cd "$(dirname "$0")/.."

OUT="dist/payload"
APP="dist/FlashGithub.app"
rm -rf "$OUT" "$APP"

echo "==> dotnet publish ($RID)"
dotnet publish src/FlashGithub.App -c Release -r "$RID" --self-contained true -o "$OUT"

echo "==> 组装 FlashGithub.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
# 用 mv 保持 inode 不变：macOS 26 会击杀"复制出来"的可执行文件（provenance 强制）
mv "$OUT"/* "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/FlashGithub.App"
rmdir "$OUT"

echo "==> 生成应用图标 icns"
python3 - <<'EOF'
import struct, subprocess, os
data = open('src/FlashGithub.App/Assets/app.ico','rb').read()
offset = struct.unpack('<I', data[18:22])[0]
size = struct.unpack('<I', data[14:18])[0]
png = data[offset:offset+size]
open('/tmp/fg32.png','wb').write(png)
os.makedirs('/tmp/fg.iconset', exist_ok=True)
subprocess.run(['cp','/tmp/fg32.png','/tmp/fg.iconset/icon_32x32.png'],check=True)
for s in (16,128,256,512):
    subprocess.run(['sips','-z',str(s),str(s),'/tmp/fg32.png',
                    '--out',f'/tmp/fg.iconset/icon_{s}x{s}.png'],capture_output=True,check=True)
subprocess.run(['iconutil','-c','icns','/tmp/fg.iconset','-o',
                'dist/FlashGithub.app/Contents/Resources/FlashGithub.icns'],check=True)
EOF

echo "==> 写入 Info.plist"
cat > "$APP/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key><string>com.flashgithub.app</string>
    <key>CFBundleName</key><string>FlashGithub</string>
    <key>CFBundleDisplayName</key><string>FlashGithub</string>
    <key>CFBundleExecutable</key><string>FlashGithub.App</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleIconFile</key><string>FlashGithub</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
</dict>
</plist>
EOF

echo "==> 生成 DMG"
hdiutil create -volname FlashGithub -srcfolder "$APP" -format UDZO \
    -o "dist/FlashGithub-macOS-${RID}.dmg" | tail -1

echo "==> 完成: dist/FlashGithub.app + dist/FlashGithub-macOS-${RID}.dmg"
