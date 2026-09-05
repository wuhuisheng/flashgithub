# FlashGithub — GitHub 网络加速

一个类似 [Watt Toolkit (Steam++)](https://steampp.net/) 的开源网络加速工具，专为 GitHub 常用域名设计，采用与 Watt Toolkit 相同的 **本地反向代理（证书 MITM）** 架构，跨平台（Windows / macOS / Linux），界面为中文。

## 工作原理

```
浏览器 / git / IDE
        │  （hosts 已把 github.com 指向 127.0.0.1）
        ▼
本地反向代理 127.0.0.1:443 / 80
  ├─ 按 SNI 用本地 CA 动态签发的证书完成 TLS 握手
  └─ YARP 把请求转发到真实 GitHub
        │  （TCP 连接由 UpstreamPool 建立）
        ▼
GitHub 真实服务器
   上游 TLS 用真实域名校验 → 确认连到的是真 GitHub
```

1. **hosts 劫持**：开启加速时把启用中的域名以 `127.0.0.1 域名` 写入系统 hosts（带标记块，可安全还原）。
2. **证书 MITM**：本地 CA 按需为每个域名签发证书，浏览器/git 与本地代理之间的 TLS 由本地完成；需要在系统信任库安装本地根证书（macOS 优先装入登录钥匙串，无需管理员密码）。
3. **动态解析 + 故障转移**：每次上游连接都通过 DoH（阿里 DNSPod → Cloudflare → Google 竞速）解析候选 IP，TCP 测速优选，连接失败自动换下一个 IP，不依赖静态 IP 表。
4. **关闭加速**：停止本地代理并自动还原 hosts。

## 功能

- 一键加速 / 关闭加速（关闭自动还原 hosts）
- 内置 16 个 GitHub 常用域名（github.com、api、raw、codeload、avatars、release-assets、githubassets 等），可勾选启用
- **自定义域名**：如 `huggingface.co`、`cdn-lfs.huggingface.co`，添加后纳入同样的加速流程
- 每个域名的实时延迟显示与手动刷新测速
- 中文界面 + 系统托盘（关闭窗口最小化到托盘，退出前自动还原 hosts）
- 完整操作日志

## 运行要求

- .NET 10 SDK（开发）或任意平台的 .NET 10 Runtime（运行）
- **监听 80/443 端口需要管理员/root 权限**：
  - Windows：以管理员身份运行，或在界面中点"以管理员身份重启"
  - macOS：`sudo` 运行，或点界面中的"以管理员身份重启"（会弹出系统授权框）
  - Linux：`sudo` / `pkexec` 运行

## 使用

```bash
# 开发运行
dotnet run --project src/FlashGithub.App

# macOS 打包（组装 .app 并生成 DMG）
bash scripts/package-macos.sh osx-arm64 1.0.1

# Windows x64 / Linux x64：自包含单文件
dotnet publish src/FlashGithub.App -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
dotnet publish src/FlashGithub.App -c Release -r linux-x64 --self-contained true /p:PublishSingleFile=true
```

启动后：点「一键加速」→ 首次会请求安装本地根证书并写入 hosts（可能弹管理员授权框）→ 正常访问 GitHub。

## 从 Release 下载安装

到 [Releases](https://github.com/wuhuisheng/flashgithub/releases) 下载对应平台的产物：

| 文件 | 平台 | 安装方式 |
|---|---|---|
| `FlashGithub-macOS-osx-arm64.dmg` | Apple Silicon Mac | 双击打开，把 FlashGithub 拖进 Applications |
| `FlashGithub-win-x64.zip` | Windows x64 | 解压出单个 exe 直接运行 |
| `FlashGithub-linux-x64.tar.gz` | Linux x64 | 解压出可执行文件，`chmod +x` 后运行 |

## macOS 运行说明（重要，限制较多）

macOS 对本地网络代理的限制比 Windows/Linux 严格得多，请按以下方式使用：

1. **必须以管理员身份运行**：本代理要监听 80/443 端口，macOS 上只有 root 能绑定。
   - 终端方式：`sudo /Applications/FlashGithub.app/Contents/MacOS/FlashGithub.App`
   - 或在应用里点「以管理员身份重启」（会弹系统授权框）。
2. **证书信任必须人工确认**：首次加速会弹出"你正在对证书信任设置进行更改"，**务必点"更新设置"**。取消的话证书不受信任，浏览器会报"连接不是私密连接"。
3. **Chrome 用户请关闭"安全 DNS"**：`chrome://settings/security` → 关闭"使用安全 DNS"。Chrome 的 DoH 会绕过系统 hosts 导致代理失效（Safari 无此问题）。改完彻底退出 Chrome（Cmd+Q）再重开。
4. **不要把程序放在 桌面/文稿/下载 目录**：这三个目录受 TCC 隐私保护，root 子进程无权读取其中的文件，会导致提权重启失败。放在主目录（如 `~/flashgithub`）或 /Applications 均可。
5. **发布格式说明**：macOS 26 会强制击杀"复制出来的"可执行文件（provenance 校验），因此 macOS 产物是 `.app`/`.app` 打包的 DMG，而 Windows/Linux 是自包含单文件。
6. **退出前点「关闭加速」**：关窗口只是最小化到托盘；托盘菜单"退出"或 Dock 退出会自动还原 hosts。若异常退出后 GitHub 打不开，删除 `/etc/hosts` 中 `BEGIN/END FlashGithub` 标记块即可还原。
7. 已知现象：加速期间浏览器证书颁发者显示为 "FlashGithub Local CA" 是本地反代的正常行为。

## 已知限制

- **SSH（git@github.com，22 端口）不走本代理**。请改用 HTTPS 克隆，或给 SSH 配置 `[ssh]` over 443 端口（`Host github.com / HostName ssh.github.com / Port 443`）。
- 个别自带证书固定（cert pinning）的客户端会拒绝本地签发的证书（浏览器、git 均不受影响）。
- 根证书只应安装在受信任的个人设备上（任何人拿到 `~/Library/Application Support/FlashGithub` 下的 CA 私钥都能签发证书）。
- 加速能力取决于网络环境：DoH 解析与 IP 直连全部被阻断时无能为力（那种情况需要真正的代理协议）。

## 项目结构

```
src/FlashGithub.Core          核心库（无 UI 依赖）
  ├─ DomainRegistry.cs        域名清单（内置 + 自定义，持久化 domains.json）
  ├─ AccelerationEngine.cs    总控：证书 → hosts → 代理 → 测速
  ├─ Log.cs                   进程内日志
  └─ Services/
      ├─ DohResolver.cs       DoH 解析（多服务器竞速 + 缓存）
      ├─ UpstreamPool.cs      IP 池：测速优选、失败转移、优质 IP 记忆
      ├─ CertificateAuthority.cs  本地 CA 与按域名签发证书
      ├─ TrustService.cs      证书安装/卸载（跨平台信任库）
      ├─ PrivilegeService.cs  提权执行 / 管理员重启
      ├─ HostsService.cs      hosts 标记块读写
      └─ ProxyService.cs      Kestrel(80/443) + YARP 转发
src/FlashGithub.App           Avalonia 中文界面（MVVM + 托盘）
test/FlashGithub.ConsoleTest  端到端冒烟测试（DoH → 证书 → 转发）
```
