# 开发与验证

源码直接位于仓库根目录；不引入额外 NuGet 包。`UseWPF` 用于读取任务栏 UI Automation 信息，主界面使用 WinForms。

```text
*.cs / CodexQuotaTray.csproj  应用源码
scripts/                    发布内容检查、常驻部署检查
.github/workflows/          Windows 自动编译与离线自检
docs/images/                经检查的演示图片
dist/                       本地编译输出，不提交
data/                       本地运行数据，不提交
```

## 离线检查

`./build.ps1 -SelfTest` 检查额度解析、空值、剩余比例、倒计时、内外圈区分、过期状态、任务栏避让和授权 URL 校验。不需要 Codex CLI、账号授权或网络。

GitHub Actions 在 Windows 上运行同一检查，仅有仓库读取权限；没有发布二进制或创建 Release 的自动步骤。

`Start.cmd` / `Demo.cmd` 共用 `scripts/launch.ps1`：缺少 `dist` 时编译并自检，再分别启动正式模式或演示。演示不创建认证客户端，关闭时不会接触账号工作文件。`./scripts/launch.ps1 -CheckOnly` 只验证编译准备，不启动程序、注册任务或读取凭据；可用 `-DotnetPath` 指定已有 SDK。

`./scripts/check-demo.ps1` 在临时目录复制干净构建的程序，放置两份非真实的账号工作文件，再运行演示面板导出并退出，确认工作文件保持原样且没有产生密文。测试结束删除它自己创建的临时目录。CI 同时运行此隔离检查及启动脚本的只检查模式。

## 桌面回归

在交互式 Windows 桌面手动运行：

```powershell
New-Item -ItemType Directory -Path ./artifacts -Force
./dist/CodexQuotaTray.exe --display-check ./artifacts/display --background
```

使用示例数据检查：顶部 DPI 建议坐标不会移动任务栏子控件；重复布局保留原生句柄和可见状态；过期任务栏快照反复重试不闪烁，稳定后恢复；连续三次销毁工具自己的临时父窗口后重建；检查账号摘要及按钮在多个 DPI、窗口宽度下仍可见。

输出结果、透明圆环和卡片图片、面板图片、位置与显示检查 JSON。图片使用演示数据，但布局输出可能含本机窗口信息和路径，不应直接提交。该检查不切换系统显示模式。2026-10-02，用户报告 v1.3.2 在其 Windows 11 设备上的真实切屏测试通过；后续版本仍应在实际硬件上观察。

`--render-widget ./artifacts/widget --background` 仅检查和导出圆环及悬停卡片，`--render-preview ./artifacts/panel.png` 导出演示面板。

## CLI 与认证检查

`./dist/CodexQuotaTray.exe --smoke-test` 需要已经安装的 Codex CLI，使用临时、隔离目录验证 app-server 握手、占位凭据的加密封存、目录权限、异常写入保留、账号隔离和注销清理；也会启动并取消 OAuth 流程，不打开浏览器、不授权真实账号、不发起推理任务。此检查可能访问认证服务，不放进离线 CI。

真实授权与额度刷新须由用户在浏览器完成。`scripts/verify-resident.ps1` 仅在该仓库自身的 `dist` 已正式部署，且两个位置已登录时运行；它核对进程独立性、系统任务、真实任务栏显示、额度新鲜度和凭据封存。输出报告含账号更新时间与本机路径，保留在本地。

## 发布流程

1. `./build.ps1 -SelfTest` 并在桌面完成显示回归；检查 GitHub Actions。
2. `./scripts/check-publish.ps1` 并检查 `git diff --cached`。脚本仅检查 Git 已追踪文件，不是完整凭据扫描器；人工检查演示图片。
3. 提交源码、文档和演示图片，排除运行数据、下载 SDK、诊断报告、历史备份。
4. 创建版本标签和 Release。真实切屏验证尚未完成时使用 prerelease，并在说明中明确验证范围。
5. 如提供下载，将干净编译的 `dist` 和启动脚本放在 Release 附件，保留 `dist` 目录结构；不将 exe、ZIP 或任何 data 文件加入 Git 历史。

提交时使用 GitHub noreply 地址；截图使用 Demo 数据，避免把真实账号邮箱放进 Git 历史。
