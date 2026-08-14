# Assistant

全部功能、源码、发布产物、配置和探测日志都位于本 `assistant` 目录。程序面向 Windows 版 Navicat Premium Lite 17 的 MySQL 8 SQL 辅助、Microsoft Word 全文光标续写与 SQL 数据库练习题回答，以及任意前台应用的主动屏幕 OCR，运行环境是 Windows x64 .NET 10。

## 当前功能

- 优先通过 Navicat 编辑器原生 `WM_GETTEXT` 读取当前查询页全文，并从状态栏得到光标行列；UI Automation 作为控件定位与回退通道。
- Navicat 的 UI Automation `Name` 属性最多只返回 4096 字符。若原生全文读取失败并命中该上限，程序会把数据标记为不完整并暂停 AI，不再根据截断前缀生成误导建议。
- 输入停止约 850ms 后请求 DeepSeek；刚新起一行时约 120ms 触发。继续输入会取消旧请求。
- DeepSeek 请求超时（30 秒）或网络不可达时，Assistant 会用同一个智谱 API Key 自动切换到智谱 `glm-5.2` 重试一次；冗余开关、API 地址和模型可在“AI 与提示词”设置中调整，Key 与屏幕 OCR 共用，不需要单独配置。
- 普通补全优先发送可读取的整篇 SQL。当前 `deepseek-v4-flash` 支持 1M token 上下文，因此程序保守使用 80 万字符文档预算；超出后以光标为中心保留前后各约 40 万字符，靠近首尾时会把剩余额度补给另一侧。
- 本地 SQL 光标语法分析器会识别字段、表级约束、外键列和父表等状态，拦截明显不符合当前语法位置的建议。
- 可在设置中配置 MySQL 连接。Assistant 启动或保存设置后读取账号可见的业务库目录，并缓存 15 分钟；目录包含库、表/视图、紧凑字段类型、键标记、外键关系和近似表规模，不包含业务行。普通补全、3 候选和 Navicat 选区诊断都会把这份稳定目录提供给 AI；目录过期时当前请求继续使用旧快照并在后台刷新，避免阻塞提示。
- AI 根据当前 SQL 自主决定是否调用 `query_mysql_readonly`，不需要特殊注释。每次请求最多执行 2 条实时查询；模型参数仍经过本地单语句只读校验，并在 `START TRANSACTION READ ONLY`、5 秒超时、默认 50 行/64 KiB 上限下执行。账号权限不作为连接门槛，可跨账号有权访问的业务库读取，但 Assistant 不会发送写入、DDL、事务控制、锁、文件操作或延时函数。
- 数据库密码只存入 Windows 凭据管理器；目录、查询和返回行不写入日志。目录元数据随启用数据库工具的 SQL 请求发送给 DeepSeek，实时业务行只有 AI 确实调用工具时才发送，并先执行敏感列掩码和大小裁剪。
- 在 Navicat 中按 `Ctrl+Alt+R`，按当前光标生成 3 个续写候选；再次按键只在本地循环候选。
- `Ctrl+Alt+A` 是统一“智能分析”：Navicat 有选区时诊断选区，无选区时立即刷新续写；Word SQL/数据库练习文档继续识别光标题目；普通 Word 文档则为当前文档开启 AI 辅助。
- Word 的 `.doc`/`.docx` 位于前台时先在本地读取完整正文、精确光标和选区。普通文档首次按 `Ctrl+Alt+A` 才允许发送 AI，之后根据完整正文和光标自动续写；切换文档或关闭总开关后需要重新授权。
- Word 题目首次使用光标前后各 300 字，题目超出窗口时自动扩展到各 1,000/3,000 字。本地校验 AI 返回的原文块和边界锚点后再保存题目，选择题、填空题和简答题都由 AI 做语义判断。
- 公共题干或 SQL 下的 `(1)(2)(3)` 按一整道顶层大题处理；简答题已有作答时，任务栏只显示光标后尚未写出的续写，不复述已有答案。照着建议输入时会在本地即时缩短剩余内容，写完后再分析下一个得分点；选择或复制文字期间暂停自动刷新。
- 浏览器版钉钉云文档当前仍是只读文档预览，不调用 AI；网页接口无法可靠提供光标或选区时会明确显示“未识别”。
- 在任意前台应用中按 `Ctrl+Alt+O`，Assistant 默认截取文本光标所在显示器，并向智谱 `glm-4.6v` 发送一张干净全图和一张局部图。有可靠光标时局部图带空心红圈；没有可靠光标时仍回答主要可见题目，但禁止代打。设置中可改为仅截取前台窗口。
- `Ctrl+Alt+D` 开关受控逐字代打。仅 Navicat/Word 当前窗口、文档、光标和 `InsertText` 仍匹配时，每个合格普通按键输入答案的下一个 Unicode 文本元素；组合键、功能键、连发、IME 和注入事件不消耗答案，异常时放行真实按键，`Esc` 立即退出，完成后自动关闭。
- `Ctrl+Alt+C` 获取当前题目的完整答案并写入 Windows 剪贴板，随后由用户自行按 `Ctrl+V` 粘贴。Navicat 返回光标处注释/TODO 对应的完整可执行 SQL，并在确有需要时自主使用只读数据库工具；Word 返回当前 SQL/数据库题目的完整参考答案；其他应用使用屏幕 OCR。选择题复制“选项字母 + 内容”，填空题按空格顺序复制全部填充值。Assistant 不会自动执行或粘贴答案。
- `Ctrl+Alt+V` 读取剪贴板中的 SQL/MySQL/数据库题目，使用独立提示词生成完整答案，并在确有需要时自主调用只读数据库工具。成功后答案覆盖原题，用户可直接按 `Ctrl+V` 粘贴；如果回答期间剪贴板内容发生变化，Assistant 会保留新内容，不用迟到答案覆盖它。
- Assistant 默认通过专用消息线程上的低级键盘 Hook 独占全部业务快捷键。命中后吞掉主键的按下、长按重复和抬起事件，因此钉钉等普通应用不会再收到同一个 `Ctrl+Alt+D`；Hook 安装失败时自动降级为普通系统热键并在主窗口提示。Windows 安全桌面、系统保留组合键、权限更高的应用或其他低级 Hook 不承诺绝对优先。
- 两个输出通道：Windows 11 任务栏内双行建议和局域网手机网页。任务栏长内容可用 `Win+PageUp` / `Win+PageDown` 上下翻页，结果变化后自动回到第一页；可拖动悬浮窗已经移除。
- 主窗口、任务栏和手机页都有三个状态灯，分别表示主流程、数据库增强和受控代打；颜色之外同时提供文字或 Tooltip。
- 任务栏提示无边框，宽度可配置为 200–1400；空白位置不足时自动缩窄。
- 任务栏提示使用不激活的 Win32 工具窗口样式，不进入 Alt+Tab/任务视图；切换应用时仍保持在任务栏区域，并且不修改或注入 Explorer。
- `Ctrl+Alt+S` 是整套 Assistant 的总开关。关闭时停止探测、键盘兜底、DeepSeek/智谱在途请求、截图识题、任务栏提示和手机服务；开启时恢复已配置通道，并立即读取当前 Navicat 或 Word 开始分析。
- 应用、主窗口、手机页和托盘统一显示为 `Assistant`，使用 Windows 内置应用图标。
- `Ctrl+Alt+M` 可独立启停手机服务。手机页显示短建议、完整 SQL、Word 完整题目、完整答案、当前续写、解析和问题列表；任务栏宽度、样式与核心快捷键可从手机反向同步。
- DeepSeek、智谱 API Key 和数据库密码分别存入 Windows 凭据管理器，不进入 `settings.json`、源码或日志。

DeepSeek 上下文和自主工具调用口径来自[官方模型与价格页](https://api-docs.deepseek.com/quick_start/pricing/)及[官方 Tool Calls 指南](https://api-docs.deepseek.com/guides/tool_calls)。

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
2. 默认模型是 `deepseek-v4-flash`，API 地址是 `https://api.deepseek.com`。冗余默认开启：DeepSeek 超时或不可达时自动改用同一个智谱 Key 调用 `glm-5.2`，可在“智谱 GLM 冗余”区域关闭或换模型。
3. 可在“全局补充提示词”中写公司表名前缀、业务规则和 SQL 风格。
4. 需要数据库上下文时，在“数据库增强”中填写主机、端口、用户名和密码；“优先数据库”可留空。点击“测试只读连接”会显示账号可见的业务库/表数量，保存后 Assistant 自动预热目录；账号权限不阻止连接，但执行链路始终只读。
5. 在“展示与样式”中调整任务栏开关、宽度、字号、行高、颜色和透明度。
6. 在“手机、监听与快捷键”中配置手机服务、键盘读取兜底、受控逐字代打和快捷键。

之后正常编写 SQL 或自然语言注释即可。AI 会先使用缓存目录判断表和字段；只有涉及实时数量、排名、聚合、样例分布、当前状态或目录缺失的精确细节时，才自主调用数据库工具。

默认快捷键：

| 功能 | 快捷键 |
|---|---|
| 整体显示/隐藏并启停 Assistant | `Ctrl+Alt+S` |
| 智能分析当前 Navicat/Word 内容 | `Ctrl+Alt+A` |
| 获取完整答案并复制到剪贴板 | `Ctrl+Alt+C` |
| 回答剪贴板题目并用答案覆盖 | `Ctrl+Alt+V` |
| 强制当前屏幕 OCR | `Ctrl+Alt+O` |
| 开启/关闭受控逐字代打 | `Ctrl+Alt+D` |
| 开启/关闭手机完整答案 | `Ctrl+Alt+M` |
| 生成/切换当前光标的 3 个续写候选 | `Ctrl+Alt+R` |
| 任务栏长内容向上/向下翻页 | `Win+PageUp` / `Win+PageDown` |

关闭 Assistant 后独占拦截器只保留总开关快捷键，其余快捷键立即放行；重新开启后自动恢复。任务栏翻页键固定为 `Win+PageUp` / `Win+PageDown`，其他业务快捷键可在设置页修改。旧版本的 `Ctrl+Alt+E` 诊断已合并到智能分析：Navicat 有选区时按 `Ctrl+Alt+A` 即诊断。

## 手机访问

开启“局域网手机页面”后，主界面会显示包含随机 `token` 的完整地址。手机和电脑需连接同一局域网：

- “电脑打开”使用 `localhost` 和正确令牌。
- “复制手机地址”优先选择有默认网关的真实 WLAN/以太网，过滤 Tailscale、Mihomo、Wintun、WSL、APIPA 等地址。
- 手机打不开时点击“修复手机访问”，同意 UAC 后只对 `Private,Public` 配置文件的 `LocalSubnet` 放行当前 TCP 端口。
- 默认端口是 `17832`；错误令牌返回 HTTP 401。
- 不要把带令牌的完整地址发给不受信任的人。

## 键盘兜底边界

键盘兜底不是默认数据源。只有同时满足“用户主动允许、Navicat 在前台、UI Automation 无法读取全文”才会临时启用。它只在内存中维护临时文本，不写原始按键日志；全文恢复、切出 Navicat 或关闭 Assistant 时会立即卸载。

键盘读取兜底和受控逐字代打是两个独立服务。前者只观察实际输入以恢复临时上下文，后者只消费已经确认的 `InsertText`。代打每次启动、切窗、上下文偏离、总开关关闭或答案完成后都会关闭，不持久化开关和按键内容。

## 日志与隐私

探测日志位于 `assistant\logs`。成功捕获记录包含 SQL 全文；Word 主程序日志只记录文档哈希、长度、有限光标上下文和选区片段，练习题请求日志记录窗口、题号、题型和 token 元数据，不记录完整题目或识别到的用户作答。AI 开启时，整篇或裁剪后的 SQL 会发送给配置的 DeepSeek API；DeepSeek 超时且启用冗余时，同一个请求会改用智谱 GLM 重试一次（同样只发送 SQL 正文）。普通 Word 文档只有按智能分析授权后才发送全文或光标中心裁剪，练习题仍使用 300/1,000/3,000 字自适应题目窗口。按 `Ctrl+Alt+V` 时，剪贴板题目正文会发送给 DeepSeek，但不写入本地日志。数据库工具启用时，可访问业务库的缓存目录元数据会随 SQL 请求发送给 DeepSeek；实时查询和经过掩码/裁剪的返回行只在 AI 自主调用工具时发送。日志只记录工具成功状态、行数、裁剪和错误类别。屏幕 OCR 只在用户主动按 `Ctrl+Alt+O` 或在非 Navicat/Word 场景按 `Ctrl+Alt+C` 时把当前显示器或前台窗口图片发送给智谱；图片和 Base64 只保存在内存，不写入日志或磁盘。剪贴板题目和答案正文同样不写日志，只记录来源、类型、字符数和成功状态。不要对包含密码、API Key、隐私或禁止外发数据的文档、数据库、剪贴板或屏幕使用对应 AI 功能。

单个本地文档探测上限是 500 万字符。全文日志会在输入停顿约 1.4 秒后合并写入，避免每次按键同步写盘；每分钟另记录一次低开销资源健康指标。

## Word SQL 练习题、WPS 与钉钉云文档

Word 使用 COM `Document.Content`、`Range` 和 `Selection` 读取 `.doc`/`.docx` 正文、光标前后文与选区。未按智能分析时任务栏仅展示本地读取预览。SQL/数据库练习文档按 `Ctrl+Alt+A` 继续识别并切换到光标所在题目；其他 Word 文档则把这次按键视为“授权当前文档 AI 辅助”，随后根据完整正文和精确光标自动续写。

首次请求让 AI 在带 `B001` 等编号的 Word 原文块中同时定位题目、判断题型/题号/当前子问和作答状态，并生成答案。本地用返回的块编号与边界锚点从 Word 原文重建题目；窗口不完整时自动按 300 → 1,000 → 3,000 字半径扩展。综合大题会保存公共题干、SQL 和全部子问。之后输入或移动光标会经过防抖生成续写；跟随当前建议时由本地计算并展示剩余后缀，不逐字调用 AI，Word 有选区时暂停自动更新。如果光标已经来到另一题，Assistant 保留当前题并提示再次按 `Ctrl+Alt+A`，不会擅自切题。完整规则见 [docs/需求2.md](docs/需求2.md)。

钉钉使用浏览器 UI Automation 的 `TextPattern` 保留只读预览；网页只暴露部分可访问性内容时会明确标为“可能不完整”，没有可靠接口时显示“未识别”，不会把可见全文误报为选区，也不会调用 AI。WPS 当前只保留脚本验证，尚未接入主程序。

下面的独立脚本仍可用于记录更详细的接口诊断：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "\\wsl.localhost\Ubuntu-24.04\home\t\projects\personal\02learn-sql\assistant\probe-word.ps1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "\\wsl.localhost\Ubuntu-24.04\home\t\projects\personal\02learn-sql\assistant\probe-wps.ps1"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "\\wsl.localhost\Ubuntu-24.04\home\t\projects\personal\02learn-sql\assistant\probe-dingtalk.ps1"
```

每次只运行一个脚本。脚本默认探测 15 分钟；启动后切换到对应文档，依次执行输入但暂不保存、把光标移到文档首部/中部/尾部、选择一小段和跨段文字，最后再保存一次。完成后可回到 PowerShell 按 `Ctrl+C`，也可等待自动结束。若希望持续运行直到手动停止，传入 `-DurationMinutes 0`。

Word 探针连接当前已运行的 `Word.Application`，不会另开 Word。它通过 `Document.Content`、`Range` 和 `Selection` 分别读取正文、光标前后文与选区，并记录 `StoryRanges` 中的页眉、页脚、脚注等内容。普通运行只在 Word 文档位于前台时捕获；`-AllowBackgroundCapture` 仅供自动化短验证使用，不应用于包含敏感信息的文档。

钉钉脚本同时支持桌面客户端和浏览器版。浏览器安全模式要求“受支持的浏览器进程”和窗口标题中的“钉钉文档 / DingTalk Docs / AliDocs”同时匹配，避免误读其他网页。浏览器范围参考[钉钉文档官方说明](https://alidocs.dingtalk.com/i/u/recommend-browsers)。

如果控制台一直显示等待且当前进程确实是目标浏览器，说明该文档没有在窗口标题暴露钉钉标识。此时可显式放宽为任意前台浏览器标签页：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "\\wsl.localhost\Ubuntu-24.04\home\t\projects\personal\02learn-sql\assistant\probe-dingtalk.ps1" -AllowAnyBrowserTab
```

放宽模式下，探测期间只能把无敏感信息的钉钉测试文档切到前台；不要切换到邮箱、密码页或其他私密标签页。

日志分别写入 `logs/document-probe-word-*.jsonl`、`logs/document-probe-wps-*.jsonl` 和 `logs/document-probe-dingtalk-*.jsonl`。Word 日志记录活动文档主正文、Story 内容、内容哈希、对象模型坐标、换算后的纯文本下标、光标前后文、选区以及保存状态；WPS 和钉钉日志记录 UI Automation 候选全文、内容哈希、控件与模式、推算的光标偏移及上下文、选区文本和范围。控制台只显示长度和偏移，不打印正文。

这些验证日志包含文档内容。测试时请使用无密码、密钥和隐私数据的专用文档；日志仅保存在本机，不要公开上传。

## 通用截图识题

屏幕 OCR 默认使用 `https://open.bigmodel.cn/api/paas/v4` 和 `glm-4.6v`，智谱 Key 需要在设置页单独保存。快捷键触发后，Assistant 优先使用 Word `ActiveWindow.GetPoint`，再尝试 UI Automation 文本范围和 Win32 `GetGUIThreadInfo.rcCaret`。无法取得可靠文本插入光标时不会使用鼠标位置冒充光标，而是明确进入“仅根据屏幕回答、不可代打”的模式。

默认截图范围是文本光标所在显示器；没有可靠光标时使用前台窗口所在显示器。设置中可切换为仅前台根窗口。请求包含无标记全图和局部图；有可靠光标时局部图上的红色空心圆及箭头由本地叠加，不会覆盖中心文字。新的截图请求会取消旧请求，总开关关闭时也会立即取消。任务栏显示短答案，手机页显示 OCR 题目、完整参考答案和解析。

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

无第三方测试框架的自检项目覆盖建议匹配、AI 数据库工具自主调用/不调用、目录上下文、只读 SQL 验证、当前题完整答案与剪贴板题目专用提示、剪贴板正文脱敏、独占快捷键、Unicode 代打队列、外键语法状态、三候选、普通 Word 全文光标提示、Word 300/1,000/3,000 题目上下文、顶层大题重建、边界校验、AI JSON/token 映射、DeepSeek 超时切智谱 GLM 冗余（含关闭/无 Key/连接失败/HTTP 错误不切换）、三灯手机页、日志隐私、手机地址、旧配置迁移，以及 80 万字符 SQL 上下文的全文优先和超长裁剪：

```powershell
dotnet build "\\wsl.localhost\Ubuntu-24.04\home\t\projects\personal\02learn-sql\assistant\NavicatAssistant.slnx" -c Release
dotnet "\\wsl.localhost\Ubuntu-24.04\home\t\projects\personal\02learn-sql\assistant\tests\NavicatTextProbe.SelfTests\bin\Release\net10.0-windows\NavicatTextProbe.SelfTests.dll"
```
