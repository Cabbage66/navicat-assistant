# Navicat SQL Copilot

全部功能、源码、发布产物、配置和探测日志都位于本 `assistant` 目录。程序面向 Windows 版 Navicat Premium Lite 17 和 MySQL 8 SQL，运行环境是 Windows x64 .NET 10。

## 已实现功能

- 通过 Windows UI Automation 读取 Navicat 当前查询页全文，实测 `TEEAutoCompleteMemo` 编辑器可读取完整 SQL，并从状态栏得到光标行列。
- 输入停止约 850ms 后请求 DeepSeek；刚新起一行时约 120ms 触发。旧请求会在继续输入时取消。
- 本地 SQL 光标语法分析器会先识别当前正在写字段、表级约束、外键列还是父表；明显不符合当前语法状态的建议会被拦截。例如 `CONSTRAINT fk_user_id ` 会立即进入 `FOREIGN KEY (user_id) REFERENCES ...`，不会重复建议已经定义的字段。
- 在 Navicat 中按 `Ctrl+Alt+R`，会按当前精确光标生成 3 个续写候选；再次按同一快捷键只在本地循环候选，不重复请求。模型排序还会经过本地语法一致性复排和最多 20 个上下文的结果缓存。
- 内置 SQL Copilot 系统提示词，覆盖新手常见的建表、约束、逗号/括号、查询、连接、过滤、分组、排序和 DML 安全条件；设置页可以追加全局业务提示词。
- 在 Navicat 选中一段 SQL 后按 `Ctrl+Alt+E`，程序自动复制选区并进入错误诊断模式。
- 全文读取失败时，可选的键盘兜底才会临时启用；默认关闭时不会安装系统级键盘钩子。只有“用户允许 + Navicat 全文不可读”同时成立才临时安装，全文恢复、切出 Navicat 或暂停探测时立即卸载，不记录原始按键日志。
- 三个可独立开关的输出：透明可拖动悬浮窗、Windows 11 任务栏内双行建议、局域网手机网页。
- 任务栏文字放在通知区域左侧的任务栏空白位置，不修改、不注入 Explorer；会根据 DPI、任务栏高度和当前任务按钮范围自动定位。
- 输入内容与当前 AI 建议相同或正在逐字匹配建议时，保持原建议不闪烁、不重新请求；偏离建议后才重新进入预测。
- 普通输入同时使用停止输入防抖和请求最短间隔；新起一行仍走快速预测，后台请求不再用“加载中”覆盖当前建议。
- 长期运行优化：Navicat 没有新输入时复用最近快照，最多每 3 秒做一次完整 UI Automation 心跳；输入时仍按原频率读取。隐藏主窗口后不再持续替换其中的大文本，隐藏的任务栏窗口也会停止定位计时器。
- 日志改为后台有界队列，完整 SQL 只在输入停顿约 1.4 秒后记录最新快照；普通状态记录不再重复 SQL，控件诊断也不会再次复制编辑器全文。队列最多保留 8 项，避免磁盘变慢时无限占用内存。
- 每分钟记录一次低开销 `health` 指标，包括工作集、私有内存、托管堆、GC 碎片、句柄和线程数，方便判断是否存在持续增长。
- `--`、`#`、`/* ... */` 注释会被明确提取为用户意图提示；选区诊断会扫描全部可确认问题并按 P0/P1/P2 排序。
- 系统托盘右键菜单、全局快捷键，以及三端的字号、行高、背景色、字体色和透明度配置。
- 手机页显示短建议、完整建议 SQL、解释和问题列表；配置可反向同步到电脑端。网页必须携带随机访问令牌。
- DeepSeek API Key 只存入 Windows 凭据管理器，不进入 `settings.json`、源码或日志。

## 运行

程序当前发布到：

```text
assistant\dist\NavicatTextProbe\NavicatTextProbe.dll
```

可以双击 `run-probe.cmd`。如果 Windows 从 WSL 网络路径启动脚本时仍有限制，请直接在 Windows PowerShell 执行这条最可靠的命令：

```powershell
& "C:\Program Files\dotnet\dotnet.exe" "\\wsl.localhost\Ubuntu-24.04\home\t\projects\personal\02learn-sql\assistant\dist\NavicatTextProbe\NavicatTextProbe.dll"
```

关闭主窗口只会缩到系统托盘；真正退出请右键托盘图标选择“退出”。

## 第一次配置

1. 先撤销曾经发到聊天中的旧 DeepSeek API Key，并创建新 Key。不要继续使用已经暴露的 Key。
2. 打开程序的“设置 → AI 与提示词”，填入新 Key 并保存。
3. 默认模型已经配置为 `deepseek-v4-flash`，API 地址为 `https://api.deepseek.com`。
4. 可在“全局补充提示词”中写公司表名前缀、业务规则、SQL 风格等；它会追加到内置 SQL Copilot 系统提示词后。
5. 在展示页选择悬浮窗、任务栏内双行建议和手机网页，并配置各自样式。

默认快捷键：

| 功能 | 快捷键 |
|---|---|
| 显示/隐藏透明悬浮窗 | `Ctrl+Alt+1` |
| 显示/隐藏任务栏内建议 | `Ctrl+Alt+2` |
| 开启/关闭手机网页 | `Ctrl+Alt+3` |
| 生成/切换当前光标的 3 个续写候选 | `Ctrl+Alt+R` |
| 分析 Navicat 当前选中 SQL | `Ctrl+Alt+E` |

快捷键可在设置页修改。如果被其他程序占用，主界面会显示注册失败的组合。

## 手机访问

开启“局域网手机页面”后，主界面会显示包含 `token` 的完整地址。手机和电脑连接同一个局域网后，在手机浏览器打开该地址即可。请注意：

- 点击“电脑打开”会使用 `localhost` 和正确令牌直接打开，避免误用虚拟网卡地址。
- 点击“复制手机地址”会优先选择带默认网关的真实 WLAN/以太网，过滤 Tailscale、Mihomo、Wintun、WSL、APIPA 等不适合普通手机的地址。
- 手机打不开时点击“修复手机访问”，同意 Windows UAC 后，程序只对 `Private,Public` 配置文件的 `LocalSubnet` 放行当前 TCP 端口；网页仍必须提供随机访问令牌。
- 不要把带令牌的完整地址发给不受信任的人。
- 默认端口是 `17832`，可以在设置中修改。
- 错误令牌会返回 HTTP 401。
- 如果电脑浏览器仍打不开，检查 Mihomo 等代理是否错误代理了 `localhost`；应把 `localhost`、`127.0.0.1` 和真实局域网网段加入直连规则。

## 诊断选中的 SQL

先在 Navicat 查询编辑器中选中报错的 SQL，再保持 Navicat 在前台并按 `Ctrl+Alt+E`。程序会模拟一次 `Ctrl+C` 读取选区，将选中内容和当前完整文档一起交给 AI。悬浮窗优先展示第一处关键修正，主界面和手机页展示完整修正版、原因和问题列表。

## 当前光标的 3 个续写候选

保持 Navicat 查询编辑器在前台，把光标放到需要续写的位置并按 `Ctrl+Alt+R`。第一次会请求 AI 并生成 3 个按推荐度排列的方案；悬浮窗和任务栏只显示当前方案，主界面显示完整代码，手机页会列出全部 3 项。光标和 SQL 未变化时再次按键，会依次切换到第 2、第 3 项，再回到第 1 项，不会重新请求。

如果当前语法状态非常明确，程序会先显示本地即时建议，再等待 AI 给出更完整结果。外键候选中的父表名若只是按命名推断，会在解释中明确提醒核对；文档里已经出现过的真实父表优先级更高。

## 键盘兜底的边界

键盘兜底不是默认的数据源。只有同时满足以下条件才工作：

1. 用户在设置中主动允许；
2. Navicat 是前台程序；
3. UI Automation 当前无法读取全文。

它只在内存中维护临时文本，原始按键不会写日志；中文输入法组合过程无法仅靠低级键盘钩子完整恢复，粘贴操作则会尝试读取剪贴板。因此全文 UI Automation 仍是首选。

## 日志与隐私

探测日志位于 `assistant\logs`。按照当前验证需求，成功捕获记录包含 SQL 全文，便于后续判断 Navicat 兼容性。AI 开启时，光标附近的 SQL 上下文会发送给所配置的 DeepSeek API。请不要在 SQL 中输入密码、API Key 或禁止落盘/外发的数据。

为了避免每敲一个字符都同步写整份文档，全文日志采用停顿合并：最后一次内容变化约 1.4 秒后写入一份最新完整快照；切出 Navicat 或退出程序时会立即补写尚未落盘的快照。`health` 记录可用于长时间运行排查，正常情况下托管堆会随 GC 上下波动，而不是每分钟单调上涨。

单个本地文档的探测上限是 500 万字符。为了控制 API 成本，普通补全发送的上下文最多约 12 万字符，选区诊断会分别限制选区和完整文档上下文；本地界面仍保留已读取的全文。

## 重新构建

Windows PowerShell 执行：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
& "\\wsl.localhost\Ubuntu-24.04\home\t\projects\personal\02learn-sql\assistant\build-and-publish.ps1"
```

也可以直接执行：

```powershell
dotnet publish "\\wsl.localhost\Ubuntu-24.04\home\t\projects\personal\02learn-sql\assistant\src\NavicatTextProbe\NavicatTextProbe.csproj" `
  -c Release -r win-x64 --self-contained false -p:UseAppHost=false `
  -o "\\wsl.localhost\Ubuntu-24.04\home\t\projects\personal\02learn-sql\assistant\dist\NavicatTextProbe"
```

无第三方测试框架的自检项目位于 `tests/NavicatTextProbe.SelfTests`，覆盖建议逐字命中、完整命中、输入偏离、光标移动、光标后文本保护、换行格式差异、注释提示提取、全量优先级诊断提示、外键语法状态识别、重复字段拦截、三候选复排与循环。运行方式：

```powershell
dotnet build "\\wsl.localhost\Ubuntu-24.04\home\t\projects\personal\02learn-sql\assistant\NavicatAssistant.slnx" -c Release
dotnet "\\wsl.localhost\Ubuntu-24.04\home\t\projects\personal\02learn-sql\assistant\tests\NavicatTextProbe.SelfTests\bin\Release\net10.0-windows\NavicatTextProbe.SelfTests.dll"
```
