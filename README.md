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

# 发布单文件（示例：macOS arm64）
dotnet publish src/FlashGithub.App -c Release -r osx-arm64 --self-contained true /p:PublishSingleFile=true
# Windows x64
dotnet publish src/FlashGithub.App -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
# Linux x64
dotnet publish src/FlashGithub.App -c Release -r linux-x64 --self-contained true /p:PublishSingleFile=true
```

启动后：点「一键加速」→ 首次会请求安装本地根证书并写入 hosts（可能弹管理员授权框）→ 正常访问 GitHub。

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
