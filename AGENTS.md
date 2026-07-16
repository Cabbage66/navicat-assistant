# Assistant 项目开发说明

## 作用域与项目边界

本文件适用于 `assistant/` 下的全部文件。

- `assistant` 是一个独立 Git 仓库。执行 Git、构建、测试和发布命令时，工作目录必须是本目录，不要把父级 SQL 学习仓库的提交混进来。
- 这是 Windows 桌面应用开发项目，不是 SQL 刷题教程。处理应用代码、探测、提示或 UI 缺陷时，不需要维护父目录的 `SQL学习记录.md`；只有用户明确回到 SQL 学习并确实暴露知识错误时，才按父项目规则记录。
- 当前产品名统一为 `Assistant`，内部历史命名空间和项目目录仍叫 `NavicatTextProbe`，不要为了表面统一进行无收益的大规模重命名。
- 开始修改前先运行 `git status --short --branch`，保留用户已有修改，不执行破坏性 Git 命令。

## 项目目标

Assistant 是面向 Windows 版 Navicat Premium Lite 17 的 MySQL 8 SQL 辅助工具，运行于 Windows x64 和 .NET 10。它读取当前 Navicat 查询编辑器全文和精确光标位置，把上下文发送给 DeepSeek，并把续写或诊断结果显示在：

1. Windows 11 任务栏区域内的无边框提示条；
2. 同一局域网内的手机网页。

主程序是 WPF 应用，同时使用少量 Windows Forms 能力。应用关闭主窗口后留在托盘；真正退出必须通过托盘“退出”。

## 不得回退的产品约束

后续开发必须保持以下行为，除非用户明确提出相反的新需求：

- 不恢复可拖动悬浮窗。当前只有任务栏提示和手机网页两个输出通道。
- 任务栏提示无边框，宽度可配置为 200–1400，并在可用空间不足时自动缩窄。
- 任务栏提示必须使用不激活的工具窗口：保留 `WS_EX_TOOLWINDOW`、`WS_EX_NOACTIVATE`，移除 `WS_EX_APPWINDOW`，不进入 Alt+Tab 或任务视图，不抢 Navicat 焦点。
- 切换应用后提示仍应位于任务栏区域。不要注入、替换或修改 Explorer；通过独立、置顶且不激活的窗口实现。
- `Ctrl+Alt+S` 是整个 Assistant 的总开关。关闭时必须停止探测、键盘兜底、在途 AI 请求、候选请求、任务栏提示和手机服务，并注销除总开关外的热键；开启时恢复已配置通道并立即重新捕获。
- 默认热键：总开关 `Ctrl+Alt+S`，生成/切换 3 个候选 `Ctrl+Alt+R`，诊断 Navicat 选区 `Ctrl+Alt+E`。
- 应用、托盘、主窗口和手机页的可见名称统一为 `Assistant`，使用 Windows 内置应用图标。
- DeepSeek API Key 只能存入 Windows 凭据管理器，不能写入源码、`settings.json` 或日志。
- 自动补全结果只能应用于产生它的文档哈希和光标上下文。输入或光标变化时取消旧请求，不能让迟到结果覆盖新位置。
- `DisplayText` 是给人看的任务栏短提示，`InsertText` 是真正续写的 SQL。二者可以不同，例如任务栏显示“表名后缺少英文左括号：(”，实际续写只应是 `(`。

## 核心数据流

自动补全主链路如下：

1. `MainWindow` 的定时器触发捕获；
2. `NavicatProbeService` 定位前台 Navicat 编辑器并生成 `ProbeResult`；
3. 原生 `WM_GETTEXT` 读取全文，Navicat 状态栏提供行列，再换算成全文光标偏移；
4. `MainWindow.SchedulePrediction` 做去重、延迟、取消和本地即时语法提示；
5. `PromptTemplates` 构建上下文，`DeepSeekClient` 请求模型；
6. `SqlContextAnalyzer` 检查或修正与当前语法位置不兼容的建议；
7. `UpdateSuggestion` 同步任务栏窗口和手机服务。

选区诊断走 `NavicatSelectionService`，通过用户配置的热键复制当前选区，再调用诊断提示词。键盘兜底不是常规数据源，只能在用户主动开启且全文接口确实不可用时临时启用。

## 关键文件

| 文件 | 职责 |
|---|---|
| `src/NavicatTextProbe/MainWindow.xaml.cs` | 应用编排、捕获状态、请求取消、候选切换、总开关和输出同步 |
| `src/NavicatTextProbe/Services/NavicatProbeService.cs` | Navicat/UIA 控件定位、全文候选选择、光标行列与偏移计算 |
| `src/NavicatTextProbe/Native/NativeMethods.cs` | Win32 P/Invoke、`WM_GETTEXT`、窗口样式、任务栏定位所需原生接口 |
| `src/NavicatTextProbe/Services/PromptTemplates.cs` | DeepSeek 系统提示、补全/候选/诊断上下文以及超长裁剪 |
| `src/NavicatTextProbe/Services/DeepSeekClient.cs` | DeepSeek HTTP 调用与 JSON 结果解析 |
| `src/NavicatTextProbe/Services/SqlContextAnalyzer.cs` | 光标处本地 SQL 状态、即时确定性提示、结果兼容性检查和候选排序 |
| `src/NavicatTextProbe/Services/SuggestionAcceptanceMatcher.cs` | 判断用户输入是否仍在跟随当前建议，防止无意义重复请求 |
| `src/NavicatTextProbe/TaskbarSuggestionWindow.*` | 无边框、不激活、不进 Alt+Tab 的任务栏提示窗口及定位 |
| `src/NavicatTextProbe/Services/MobileServerService.cs` | 带访问令牌的局域网手机页、样式和设置反向同步 |
| `src/NavicatTextProbe/Services/SettingsService.cs` | 配置读取、迁移、默认值和范围校验 |
| `src/NavicatTextProbe/Models/AppSettings.cs` | 当前配置模型和默认值 |
| `src/NavicatTextProbe/Services/ProbeLogger.cs` | 异步 JSONL 探测、建议、诊断和健康日志 |
| `tests/NavicatTextProbe.SelfTests/Program.cs` | 无第三方测试框架的回归自检入口 |
| `build-and-publish.ps1` | Windows x64 framework-dependent 发布到 `dist/Assistant` |
| `run-probe.cmd` / `run-probe.ps1` | 启动已发布的 `Assistant.dll` |
| `docs/需求1.md` | 当前任务栏化改造的原始产品需求 |

## Navicat 全文与光标：高风险区域

这部分已经出现过真实回归，修改前必须理解：

- Navicat 编辑器类通常是 `TEEAutoCompleteMemo`。UI Automation 的 `AutomationElement.Name` 在真实环境中会固定截断为 4096 字符，不能把它当成全文。
- 优先使用 `NativeMethods.TryReadControlText` 通过 `WM_GETTEXTLENGTH` 和 `WM_GETTEXT` 读取编辑器全文。当前单文档上限为 500 万字符。
- UIA `TextPattern`、`ValuePattern` 和 `Name` 只能作为回退。若 `Name` 达到 4096 字符，必须标记 `IsTruncated = true`；截断时自动 AI 预测必须暂停，不能根据固定前缀持续给出旧建议。
- 曾经的真实故障是：文档超过 4096 字符后，无论光标在哪都一直提示 `PRIMARY KEY (id)`。原因就是把 UIA 上限误判成完整文档。
- 光标行列来自 Navicat 状态栏 `StatusBar.Pane5`，格式类似 `Ln N, Col M`；全文偏移由 `CalculateCaretOffset` 换算。Navicat 返回的文本可能混合 `\n` 与 `\r\n`，不要按固定两字符换行计算。
- 排查光标问题必须同时核对 `CaretLine`、`CaretColumn`、`CaretOffset` 和光标附近短片段，不能只看全文长度，也不能只凭任务栏上的旧建议判断。
- 确定性的局部语法应优先做成本地即时提示。例如光标紧跟 `CREATE TABLE 表名` 时，立即提示缺少英文左括号，不应等待远端模型返回单字符。

## DeepSeek 上下文规则

- 普通补全优先发送可读取的完整 SQL 文档。
- 当前 `PromptTemplates.CompletionDocumentCharacterLimit` 为 800,000 个 UTF-16 字符。文档不超过预算时保留全文；超过时以光标为中心保留前后各约一半，靠近首尾时把未使用额度让给另一侧。
- `<CURSOR>` 必须插入精确光标位置，裁剪后也不能丢失或移动。
- 诊断要避免同时重复发送相同的“完整选区”和“完整文档”。
- 当前默认模型是 `deepseek-v4-flash`，API 地址是 `https://api.deepseek.com`。模型能力、上下文或接口属于可能变化的外部事实；调整预算或模型前，应查 DeepSeek 官方文档，不凭记忆修改。
- 本地语法状态属于硬约束。模型结果与外键、约束类型或建表括号等明确语法位置冲突时，应由 `SqlContextAnalyzer` 拦截或替换。

## 设置、网络与隐私

- `config/settings.json` 是运行时用户配置，已被 Git 忽略。不要把本机配置或访问令牌提交为默认配置。
- 新增或删除设置字段时，更新 `AppSettings`、设置 UI、手机设置补丁、`SettingsService` 的迁移版本和范围校验，并补充迁移自测。
- 手机服务默认端口为 `17832`，访问必须校验随机 token；错误 token 返回 401。
- 手机地址筛选应排除 WSL、虚拟隧道、APIPA 等不适合手机访问的网卡，优先有默认网关的真实 WLAN/以太网。
- 防火墙修复会触发 UAC，只允许当前 TCP 端口、`Private,Public` 和 `LocalSubnet`，不要扩大规则作用域。
- `logs/*.jsonl` 可能包含用户完整 SQL，属于敏感数据。不要提交日志，不要在回复或工具输出中无必要地展示整份 SQL、手机 token、API Key 或凭据内容。

## 日志诊断方式

日志位于 `logs/`，`logs/latest-log.txt` 指向最近一次会话。成功捕获日志会经过短暂防抖后写入全文；状态变化和建议结果单独记录。

遇到“提示不更新、提示位置错误、一直重复旧内容”时按以下顺序检查：

1. 确认用户已退出旧进程并运行最新发布包；旧 WPF 进程不会热加载新的 DLL。
2. 查看最新日志时间，确认本次操作确实进入新会话。
3. 捕获源应优先为 `Win32 WM_GETTEXT（Navicat 编辑器全文）`；若是 UIA 回退，检查长度和 `IsTruncated`。
4. 只抽取最新事件的来源、长度、哈希、光标行列/偏移和光标前后几十个字符，避免把整篇 SQL 打到终端。
5. 对照 suggestion 的触发类型、上下文长度、耗时、错误和返回文本，区分“没有发请求”“请求被取消”“模型失败”和“结果没有展示”。
6. 最后检查任务栏通道是否启用、总开关是否开启，以及 `UpdateSuggestion` 是否因相同 payload 合理跳过刷新。

推荐使用 `jq` 读取 JSONL 元数据，不要直接对所有历史日志全文 `rg`，否则很容易输出大量用户 SQL。

## 开发与代码约定

- 保持 `Nullable` 和隐式 using 开启，构建必须做到 0 警告、0 错误。
- Win32 声明集中放在 `NativeMethods.cs`；新增 P/Invoke 时明确字符集、超时、错误和窗口失去响应时的行为。
- WPF UI 更新只能在 Dispatcher/UI 线程执行；后台探测和 HTTP 调用不得阻塞 UI。
- 每个自动请求都必须有取消路径，并在写入 UI 前再次验证上下文仍是当前上下文。
- 不要轻易引入第三方包。当前自测是一个可执行项目，适合覆盖纯逻辑、设置迁移和可隔离的 Windows 行为。
- 修复真实缺陷时添加最小回归用例，优先复现用户给出的原始文本和精确光标位置。
- 修改任务栏窗口时，除了视觉效果，还要验证扩展样式、无激活、Alt+Tab 排除和切换应用后的可见性。
- 修改手机页内嵌 HTML/JavaScript 时，同时检查服务端转义、token 校验、浏览器 JavaScript 语法和桌面/手机设置同步。
- `dist/`、`bin/`、`obj/`、`logs/` 和本机 `config/settings.json` 都是生成或运行时内容，不手工编辑、不提交。

## 构建、测试与发布

当前 WSL 环境没有可直接使用的 Linux `dotnet`；项目目标又是 `net10.0-windows`。从 WSL 调用 Windows SDK：

```bash
'/mnt/c/Program Files/dotnet/dotnet.exe' build "$(wslpath -w ./NavicatAssistant.slnx)" -c Release
```

构建成功后运行全部自测：

```bash
'/mnt/c/Program Files/dotnet/dotnet.exe' "$(wslpath -w ./tests/NavicatTextProbe.SelfTests/bin/Release/net10.0-windows/NavicatTextProbe.SelfTests.dll)"
```

发布最新 Windows x64 产物：

```bash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w ./build-and-publish.ps1)"
```

发布结果应位于：

```text
dist/Assistant/Assistant.dll
```

运行时行为有变化时，最低交付标准是：

1. `git diff --check` 通过；
2. Release 构建 0 警告、0 错误；
3. `NavicatTextProbe.SelfTests` 全部通过；
4. 执行发布脚本；
5. 涉及 Navicat、任务栏、热键或手机访问时，尽可能做对应 Windows 实机只读验证；
6. 告知用户必须从托盘退出旧 Assistant，再运行 `run-probe.cmd` 测试新产物。

只修改文档时无需发布运行包，但仍要执行 `git diff --check` 并核对 Git 状态。

## Git 与交付

- 仓库基线在 `main`，任务栏化功能目前沿 `feature/dock-assistant` 演进。不要假设分支永远不变，以实际 `git status` 为准。
- 不提交被忽略的发布包、日志、设置或凭据。
- 不覆盖用户未提交的修改；发现重叠修改时先理解差异再编辑。
- 除非用户要求，不重写历史、不强推、不执行 `reset --hard` 或类似破坏性操作。
- 最终回复先说明结果和用户需要执行的动作，再简要列出验证结果、提交（如果有）和关键文件位置。

## 当前需求与历史回归入口

- 当前产品需求原文见 `docs/需求1.md`，实现和运行口径见 `README.md`。当文档与当前代码测试冲突时，先查 Git 历史和用户最新要求，不机械恢复旧设计。
- 已修复的两类问题必须保留回归保护：
  - UIA 4096 字符截断导致光标之后内容永远不可见、持续显示旧 `PRIMARY KEY (id)`；
  - 光标在 `CREATE TABLE 表名` 后时，明确缺少 `(` 却等待远端模型，造成延迟且单字符提示不明显。
- 需求变化后同步更新 `README.md`、相关设置说明和自测，让“当前功能”只有一个可信入口。
