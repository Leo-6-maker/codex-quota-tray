# Codex Quota Tray

A small Windows tray app for monitoring two Codex accounts. 中文说明见下文。

**Windows 11 · C# / .NET 8 · MIT · Experimental v1.3.2**

两个账号的 Codex 剩余额度，常驻任务栏，一眼查看。

![任务栏圆环示例](docs/images/rings-demo.png)

![悬停详情卡片示例](docs/images/hover-card-demo.png)

图片全部使用演示数据，不含真实账号信息。项目由个人维护，与 OpenAI 无隶属关系。

## 功能

- 左右两组同心圆环分别代表 GPT-A / GPT-B；外圈表示短周期剩余额度，内圈表示周剩余额度。
- 默认短周期通常为 5h，实际时长按服务返回的数据显示；百分比均为**剩余**。
- 悬停显示迷你卡片：两账号额度、重置倒计时、各自更新时间。
- 低于 20% 时对应圆环变为琥珀色；数据过期变灰，保留上次快照。
- 每两分钟刷新；低余额提醒可关闭。
- 任务栏圆环避让 TrafficMonitor 和系统控件；仅显示在主屏任务栏。
- 通过当前用户的系统任务独立运行，支持开机启动；关闭面板后保留托盘。

## 环境

- Windows 11 x64，支持 .NET 8 Windows Desktop Runtime。
- 从源码编译需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。SDK 已包含运行所需组件。
- 已安装 [Codex CLI](https://github.com/openai/codex)，并且两个监控账号分别具备 Codex 使用权限。

程序优先使用 `CODEX_QUOTA_CLI` 指定的原生 `codex.exe`，然后查找 npm 安装目录和 PATH。仅安装 npm 包时，程序会查找包内的原生可执行文件。如果无法自动找到，在启动程序的环境中设置 `CODEX_QUOTA_CLI` 为该文件的完整路径。

## 从源码运行

在 PowerShell 中运行：

```powershell
git clone https://github.com/Leo-6-maker/codex-quota-tray.git
cd codex-quota-tray
./build.ps1 -SelfTest
./Start.cmd
```

如果 Windows 阻止运行脚本，可在上述目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File ./build.ps1 -SelfTest
```

保持 `dist` 和它的父目录位置不变，放在当前用户可写的位置。正式启动器必须位于 `dist` 内；普通启动会注册或更新本用户的 `CodexQuotaTray-<Windows SID>` 系统任务，并立即启动后台监控。默认不开机自启，可在面板勾选“开机启动”。无需管理员权限。

首次使用点击托盘图标打开完整面板，分别为 A、B 点击“登录 / 更换账号”，在浏览器中选择对应账号。两个位置需要分别授权，授权后核对面板中的邮箱。ChatGPT 网页上的多账号登录不会自动授权给本工具。

悬停查看额度，双击圆环或点击托盘图标打开面板。关闭面板只是收起；右键托盘选择“退出”结束监控。Windows 可将托盘图标放进隐藏区，请在任务栏设置中调整。

本工具只查看额度；开发用 Codex 的账号切换仍由用户手动完成。

## 账号数据与隐私

程序通过已安装 Codex CLI 的 app-server 查询 `account/read` 和 `account/rateLimits/read`，不发送模型推理请求。

- 两个监控位置使用独立的 `CODEX_HOME`：`data/accounts/A` 与 `data/accounts/B`，不写入开发用 Codex 的账号目录。
- 查询或登录期间，受限目录中短暂存在 `auth.json`。操作结束后停止查询进程，通过 Windows DPAPI CurrentUser 加密为 `auth.dpapi`，并移除工作文件。
- DPAPI 凭据绑定 Windows 用户及目录；移动安装目录后可能需要重新授权，不能当作可迁移账号备份。
- `data/state.json` 包含邮箱、额度和偏好；`data/health.json` 含运行状态。不要上传整个 `data` 目录或未经检查的诊断文件。
- 当前开发账号标记只读本地认证文件中的邮箱；仅存在于系统凭据存储中的登录不会显示标记。
- 不读取 Chrome Cookie，不搭建云端中转服务。协议正文、OAuth 地址和 token 不写入诊断日志。
- 强制结束、断电或加密失败时可能留下受限工作文件，下一次启动会尝试恢复封存。

本工具只在当前用户目录未占用时尝试迁移旧版同名工具的数据；如果电脑曾使用本工具，首次运行可能保留既有账号设置。

## 兼容性与已知限制

任务栏圆环采用 Windows 原生子控件；悬停详情卡片是独立的无激活窗口。Windows 11 任务栏内部结构不是稳定扩展接口，系统更新、任务栏替换软件可能影响挂载。

切屏时如任务栏正在重建或没有可靠空位，圆环保持隐藏并自动重试；托盘仍可打开完整面板。不会退回屏幕顶部浮窗，不会调整 TrafficMonitor 的位置或配置。

v1.3.2 已在一台 Windows 11 设备上验证实际任务栏显示、透明背景和 TrafficMonitor 避让；缩放消息、失效快照、父窗口销毁后的恢复通过模拟检查。真实外接显示器与笔记本屏幕往返切换仍需要人工验证。Windows 10、ARM64 和多显示器分别显示圆环尚未验证。

Codex app-server 协议以开发时 CLI 0.114.0 为基线，CLI 更新后可能需要调整。网络失败时显示过期快照，不把失败当作额度归零。

## 检查与贡献

```powershell
./build.ps1 -SelfTest
./Demo.cmd
./scripts/check-publish.ps1
```

自动构建运行离线自检，不登录账号、不查询真实额度，也不注册系统任务。`Demo.cmd` 显示示例数据。

桌面显示和隔离认证检查见 [开发说明](docs/DEVELOPMENT.md)。提交问题时请提供 Windows 版本、缩放比例、显示器切换方式和 CLI 版本，并遮盖账号信息。请勿提交凭据、OAuth 链接或原始协议日志。

## 卸载

先在托盘右键退出，再按当前用户删除本工具的系统任务：

```powershell
$taskSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
Unregister-ScheduledTask -TaskName ('CodexQuotaTray-' + $taskSid) -Confirm:$false
```

随后可手动删除工具目录。`data` 含本工具的账号凭据；如需保留登录，请保留原位置。此操作不会删除开发用 Codex 的账号。

## 许可与参考

本项目采用 [MIT License](LICENSE)。OpenAI、Codex、Windows 名称归各自权利人所有。

任务栏挂载设计参考 [TrafficMonitor](https://github.com/zhongyang219/TrafficMonitor)，本仓库不包含其源码或可执行文件。Codex CLI 作为外部安装依赖使用，不随本仓库分发。
