# Assistant

全部功能、源码、发布产物、配置和探测日志都位于本 `assistant` 目录。程序面向 Windows 版 Navicat Premium Lite 17 和 MySQL 8 SQL，运行环境是 Windows x64 .NET 10。

## 当前功能

- 优先通过 Navicat 编辑器原生 `WM_GETTEXT` 读取当前查询页全文，并从状态栏得到光标行列；UI Automation 作为控件定位与回退通道。
- Navicat 的 UI Automation `Name` 属性最多只返回 4096 字符。若原生全文读取失败并命中该上限，程序会把数据标记为不完整并暂停 AI，不再根据截断前缀生成误导建议。
- 输入停止约 850ms 后请求 DeepSeek；刚新起一行时约 120ms 触发。继续输入会取消旧请求。
- 普通补全优先发送可读取的整篇 SQL。当前 `deepseek-v4-flash` 支持 1M token 上下文，因此程序保守使用 80 万字符文档预算；超出后以光标为中心保留前后各约 40 万字符，靠近首尾时会把剩余额度补给另一侧。
- 本地 SQL 光标语法分析器会识别字段、表级约束、外键列和父表等状态，拦截明显不符合当前语法位置的建议。
- 在 Navicat 中按 `Ctrl+Alt+R`，按当前光标生成 3 个续写候选；再次按键只在本地循环候选。
- 在 Navicat 中选中 SQL 后按 `Ctrl+Alt+E`，复制选区并诊断所有可确认问题。
- 两个输出通道：Windows 11 任务栏内双行建议和局域网手机网页。可拖动悬浮窗已经移除。
- 任务栏提示无边框，宽度可配置为 200–1400；空白位置不足时自动缩窄。
- 任务栏提示使用不激活的 Win32 工具窗口样式，不进入 Alt+Tab/任务视图；切换应用时仍保持在任务栏区域，并且不修改或注入 Explorer。
- `Ctrl+Alt+S` 是整套 Assistant 的总开关。关闭时停止探测、键盘兜底、在途 AI 请求、任务栏提示和手机服务；开启时恢复已配置通道，并立即读取当前 Navicat 开始分析。
- 应用、主窗口、手机页和托盘统一显示为 `Assistant`，使用 Windows 内置应用图标。
- 手机页显示短建议、完整建议 SQL、解释和问题列表；任务栏宽度与两端样式可从手机反向同步。
- DeepSeek API Key 只存入 Windows 凭据管理器，不进入 `settings.json`、源码或日志。

DeepSeek 上下文口径来自[官方模型与价格页](https://api-docs.deepseek.com/quick_start/pricing/)。

## 运行

发布产物位于：

```text
assistant\dist\Assistant\Assistant.dll
```

可以双击 `run-probe.cmd`。如果 Windows 从 WSL 网络路径启动脚本时仍有限制，请在 Windows PowerShell 执行：

```powershell
& "C:\Program Files\dotnet\dotnet.exe" "\\wsl.localhost\Ubuntu-24.04\home\t\projects\personal\02learn-sql\assistant\dist\Assistant\Assistant.dll"
```

关闭主窗口只会缩到系统托盘；真正退出请右键托盘图标选择“退出”。

## 第一次配置

1. 打开“设置 → AI 与提示词”，填入 DeepSeek API Key 并保存。
2. 默认模型是 `deepseek-v4-flash`，API 地址是 `https://api.deepseek.com`。
3. 可在“全局补充提示词”中写公司表名前缀、业务规则和 SQL 风格。
4. 在“展示与样式”中调整任务栏开关、宽度、字号、行高、颜色和透明度。
5. 在“手机、监听与快捷键”中配置手机服务、键盘兜底和快捷键。

默认快捷键：

| 功能 | 快捷键 |
|---|---|
| 整体显示/隐藏并启停 Assistant | `Ctrl+Alt+S` |
| 生成/切换当前光标的 3 个续写候选 | `Ctrl+Alt+R` |
| 分析 Navicat 当前选中 SQL | `Ctrl+Alt+E` |

关闭 Assistant 后只保留总开关快捷键，另外两个快捷键会注销；重新开启后自动注册回来。

## 手机访问

开启“局域网手机页面”后，主界面会显示包含随机 `token` 的完整地址。手机和电脑需连接同一局域网：

- “电脑打开”使用 `localhost` 和正确令牌。
- “复制手机地址”优先选择有默认网关的真实 WLAN/以太网，过滤 Tailscale、Mihomo、Wintun、WSL、APIPA 等地址。
- 手机打不开时点击“修复手机访问”，同意 UAC 后只对 `Private,Public` 配置文件的 `LocalSubnet` 放行当前 TCP 端口。
- 默认端口是 `17832`；错误令牌返回 HTTP 401。
- 不要把带令牌的完整地址发给不受信任的人。

## 键盘兜底边界

键盘兜底不是默认数据源。只有同时满足“用户主动允许、Navicat 在前台、UI Automation 无法读取全文”才会临时启用。它只在内存中维护临时文本，不写原始按键日志；全文恢复、切出 Navicat 或关闭 Assistant 时会立即卸载。

## 日志与隐私

探测日志位于 `assistant\logs`。成功捕获记录包含 SQL 全文；AI 开启时，整篇或裁剪后的 SQL 会发送给配置的 DeepSeek API。请勿在 SQL 中输入密码、API Key 或禁止外发的数据。

单个本地文档探测上限是 500 万字符。全文日志会在输入停顿约 1.4 秒后合并写入，避免每次按键同步写盘；每分钟另记录一次低开销资源健康指标。

## 重新构建与自检

Windows PowerShell 执行：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
& "\\wsl.localhost\Ubuntu-24.04\home\t\projects\personal\02learn-sql\assistant\build-and-publish.ps1"
```

也可以直接执行：

```powershell
dotnet publish "\\wsl.localhost\Ubuntu-24.04\home\t\projects\personal\02learn-sql\assistant\src\NavicatTextProbe\NavicatTextProbe.csproj" `
  -c Release -r win-x64 --self-contained false -p:UseAppHost=false `
  -o "\\wsl.localhost\Ubuntu-24.04\home\t\projects\personal\02learn-sql\assistant\dist\Assistant"
```

无第三方测试框架的自检项目覆盖建议匹配、注释提示、外键语法状态、三候选、日志队列、手机地址、旧配置迁移，以及 80 万字符上下文的全文优先和超长裁剪：

```powershell
dotnet build "\\wsl.localhost\Ubuntu-24.04\home\t\projects\personal\02learn-sql\assistant\NavicatAssistant.slnx" -c Release
dotnet "\\wsl.localhost\Ubuntu-24.04\home\t\projects\personal\02learn-sql\assistant\tests\NavicatTextProbe.SelfTests\bin\Release\net10.0-windows\NavicatTextProbe.SelfTests.dll"
```
