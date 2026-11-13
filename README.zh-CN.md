# nx-ai-bridge

**让任何 AI 工具直接控制 Siemens NX（UG）建模。** [English](README.md)

> ## ⚠️ Alpha 版 —— 尚未在真机 NX 上验证
>
> 桥接程序（MCP 服务器、命令行、mock）已在 Windows / macOS / Linux 上自动测试通过。但**运行在 NX 内部的那部分代码是在没有 NX 的环境下"盲写"的**，目前只用一个 NX Open 的替身做过编译和流程测试。在第一批[自检](#自检)报告处理完之前，部分工具在真实 NX 上很可能会报错。详见[验证状态](#验证状态)。

对你的 AI 助手说一句：

> 安装 https://github.com/qiuyekai/nx-ai-bridge 这个插件，然后在 NX 里画一个带两个孔的 L 形支架。

AI 会阅读 [AGENTS.md](AGENTS.md)，自己完成安装，请你在 NX 里启动一次接收端，然后直接在你的 NX 窗口里建模，你可以全程看着。

```
AI 工具 ──MCP(stdio)──▶ 桥接程序(Python) ──127.0.0.1──▶ NX 内的接收端(Journal) ──▶ NX Open API
   └───── 或命令行 CLI ────┘
```

- **任何 AI 工具都能用**：为 Claude Code / Claude Desktop / Cursor / Codex / Gemini CLI / VS Code 提供 MCP 服务器；为只会跑命令的 AI 提供功能完全相同的命令行。
- **不往 NX 里装任何东西**：接收端是一个 *Journal 脚本*（Alt+F8 运行），不需要 NX Open 开发授权，不需要编译 DLL，不需要管理员权限。Windows 端的安装就是复制文件夹。
- **AI 能"看到"并验证结果**：每一步建模都返回实体的体积和包围盒；`screenshot` 返回 NX 窗口的截图。
- **AI 能自己纠错**：失败时返回结构化 JSON，带上 NX 的原始报错；失败的步骤会自动回滚。
- **万能后门**：`run_journal` 可以执行 AI 自己写的任意 NX Open 代码；`nx_api_lookup` 让 AI 不查文档也能查到当前 NX 版本的真实 API。

## 支持的版本

| | |
|---|---|
| NX | 首要目标 **NX 10.0**；代码只用 NX 10 已有的 API，并在运行时自适应，NX 11/12/1xxx/2xxx 预计改动很小 —— 均未测试 |
| 操作系统（NX 端） | Windows 10 及以上 |
| Python（桥接程序） | 3.10+（或者只装 [uv](https://docs.astral.sh/uv/)，它会自己准备 Python） |
| AI 工具 | 支持 MCP（stdio）或者能执行命令行的任何工具 |

## 快速开始（装有 NX 的 Windows 电脑）

```powershell
git clone https://github.com/qiuyekai/nx-ai-bridge.git
cd nx-ai-bridge
powershell -ExecutionPolicy Bypass -File install.ps1
```

`install.ps1` 不需要管理员权限。它会自动找 NX 安装路径、把接收端复制到 `%USERPROFILE%\.nx-ai-bridge\nx_receiver`、准备 Python，并输出各 AI 工具的配置。
电脑上没有 Python？运行 `install.ps1 -InstallUv`，它会把 [uv](https://docs.astral.sh/uv/) 装到你的用户目录里。

然后是**唯一需要手动做的一步**，在 NX 里：

1. 启动 NX，打开或新建一个部件。
2. 按 **Alt+F8**（菜单：**工具 > 操作记录 > 播放…**，英文界面 Tools > Journal > Play…）。
3. 选择 `%USERPROFILE%\.nx-ai-bridge\nx_receiver\NxAiBridgeReceiver.cs`，点击**运行**。
4. 屏幕角落出现一个 "NX AI Bridge" 小窗口，保持它开着即可 —— 关掉它接收端就停止。

### 配置你的 AI 工具

`install.ps1` 会输出适合你这台电脑的准确命令。下面是通用写法（用 uv 直接从 GitHub 运行）：

<details open><summary>Claude Code</summary>

```powershell
claude mcp add nx -- uvx --from git+https://github.com/qiuyekai/nx-ai-bridge nx-ai-bridge serve
```
</details>

<details><summary>Cursor、Claude Desktop、Gemini CLI、Windsurf（<code>mcpServers</code> JSON）</summary>

写入 `.cursor/mcp.json`、`claude_desktop_config.json` 或 `~/.gemini/settings.json`：

```json
{
  "mcpServers": {
    "nx": {
      "command": "uvx",
      "args": ["--from", "git+https://github.com/qiuyekai/nx-ai-bridge", "nx-ai-bridge", "serve"]
    }
  }
}
```
</details>

<details><summary>Codex CLI（<code>~/.codex/config.toml</code>）</summary>

```toml
[mcp_servers.nx]
command = "uvx"
args = ["--from", "git+https://github.com/qiuyekai/nx-ai-bridge", "nx-ai-bridge", "serve"]
```
</details>

<details><summary>VS Code（<code>.vscode/mcp.json</code>）</summary>

```json
{
  "servers": {
    "nx": { "type": "stdio", "command": "uvx", "args": ["--from", "git+https://github.com/qiuyekai/nx-ai-bridge", "nx-ai-bridge", "serve"] }
  }
}
```
</details>

<details><summary>AI 工具不支持 MCP？用命令行</summary>

```powershell
nx-ai-bridge tools
nx-ai-bridge call nx_status
nx-ai-bridge call create_block length=100 width=50 height=20
nx-ai-bridge call create_hole '{"position": [50, 25, 20], "diameter": 10, "through": true}'
```

每次调用输出一段 JSON，失败时退出码为 1。
</details>

真机验证通过之后会发布到 PyPI（`pip install nx-ai-bridge` / `uvx nx-ai-bridge`），并去掉 alpha 标注。

### 没有 NX 也能试

任何系统都行，mock 接收端会用简化的几何模拟 NX：

```bash
git clone https://github.com/qiuyekai/nx-ai-bridge.git && cd nx-ai-bridge
NX_AI_BRIDGE_TRANSPORT=mock uv run nx-ai-bridge demo --part /tmp/bracket.prt
```

## 工具清单

所有长度单位为**毫米**，角度单位为**度**。`nx-ai-bridge tools <工具名>` 可查看某个工具的参数。

| 分类 | 工具 |
|---|---|
| 基础 | `nx_status`（连接状态、NX 版本、当前部件）· `get_model_info`（特征列表、包围盒、体积）· `run_journal`（执行任意 NX Open 代码）· `nx_api_lookup` · `nx_batch` |
| 部件 | `new_part` 新建 · `open_part` 打开 · `save_part` 保存 · `save_part_as` 另存为 · `close_part` |
| 基本体 | `create_block` 长方体 · `create_cylinder` 圆柱 · `create_cone` 圆锥 · `create_sphere` 球 |
| 草图 | `create_sketch`（基准面、自定义平面或实体平面）· `sketch_line` 直线 · `sketch_circle` 圆 · `sketch_rectangle` 矩形 · `sketch_arc` 圆弧 |
| 特征 | `extrude` 拉伸 · `revolve` 旋转 · `create_hole` 打孔 · `edge_blend` 边倒圆 · `chamfer` 倒斜角 · `shell` 抽壳 |
| 布尔 | `boolean_unite` 求和 · `boolean_subtract` 求差 · `boolean_intersect` 求交 |
| 管理 | `list_features` 列出特征 · `delete_feature` 删除特征 · `list_expressions` · `set_expression` 修改表达式（参数）· `undo` 撤销 · `list_edges` · `list_faces` |
| 导出 | `export_step` · `export_stl` · `screenshot`（PNG 截图，MCP 会直接把图片返回给 AI） |

每个工具都返回结构化 JSON：

```json
{"ok": true, "result": {"feature": "BLOCK(1)", "body": "B1", "volume": 32000, "bbox": [0, 0, 0, 80, 40, 10]}}
```

失败时带上 NX 的原始报错，方便 AI 自己纠错：

```json
{"ok": false, "error": {"code": "NX_ERROR", "message": "Tool body completely outside target body.",
                        "nx_error": {"type": "NXOpen.NXException", "error_code": 670030, "message": "..."}}}
```

## 两种运行模式

| | 实时模式（默认） | 批处理模式 |
|---|---|---|
| 启动方式 | 你在 NX 里运行接收端 Journal | 设置 `NX_AI_BRIDGE_TRANSPORT=batch`，桥接程序自己调用 `run_journal.exe` |
| NX 界面 | 有，AI 的操作实时显示 | 没有 |
| 速度 | 即时 | 每次调用都要启动一次 NX（用 `nx_batch` 一次执行多步） |
| 截图 | 支持 | 不支持 |
| 结果 | 你当前打开的部件 | 磁盘上的 `.prt` 文件 |

实时模式有两套通信方式，自动选择：`127.0.0.1` 上的 TCP 监听；以及作为后备的文件夹轮询（`%USERPROFILE%\.nx-ai-bridge\ipc`）。细节见 [docs/protocol.md](docs/protocol.md)。

## 自检

因为 NX 端是盲写的，项目自带一个自检工具，把几天的人工测试压缩成两步操作。如果你有 NX，跑一次自检就是对这个项目最有价值的贡献：

```powershell
powershell -ExecutionPolicy Bypass -File selfcheck.ps1
```

然后在 NX 里（Alt+F8）运行 `%USERPROFILE%\.nx-ai-bridge\nx_receiver\NxAiBridgeSelfCheck.cs`，并回答一个问题。
最后会得到**一个**文件 `nx-ai-bridge-selfcheck-<时间>.json`（桌面上也有一份），请把它附到 [GitHub issue](https://github.com/qiuyekai/nx-ai-bridge/issues) 里。报告内容：NX 版本和安装路径、`run_journal.exe` 在当前授权下能否运行、哪种通信方式可用、接收端运行时 NX 界面是否卡死、每个建模工具的结果（带 NX 原始报错）、一个带孔带倒角的支架的体积和包围盒校验，以及 NX Open 的成员名清单（不包含你的任何模型或个人文件；但文件路径里会带有你的 Windows 用户名）。

## 验证状态

| 部分 | 状态 |
|---|---|
| 桥接程序：MCP 服务器、命令行、通信层、mock | ✅ 在 Windows / macOS / Linux、Python 3.10 和 3.12 上自动测试 |
| 接收端内核：JSON、TCP + 文件夹通信、运行时编译、备用实现切换、批处理、自检报告 | ✅ 用 .NET Framework 的 C# 5 编译器编译，并在 CI 里端到端运行 —— **没有 NX** |
| 所有工具脚本能通过编译 | ✅ 针对手写的 NX Open 替身，只能证明语法正确 |
| Journal 能在 NX 10 里加载运行 | ❌ **未验证** |
| 每个建模工具在真实 NX 上的表现 | ❌ **未验证** |
| 接收端运行时 NX 界面保持可操作 | ❌ **未验证** |
| 通过 `run_journal.exe` 的批处理模式 | ❌ **未验证** |

真机上的发现记录在 [docs/nx10-notes.md](docs/nx10-notes.md)。

## 安全和限制

- **`run_journal` 能在 NX 里以你的用户权限执行任意代码** —— 这正是它的用途。只连接你信任的 AI 工具；处理重要数据时请先看一眼它准备执行的代码。想禁用它，删除 `%USERPROFILE%\.nx-ai-bridge\nx_receiver\handlers\run_journal.cs` 即可。
- 接收端**只监听 127.0.0.1**，不对外网开放任何端口。它没有身份验证：接收端窗口开着的时候，以你的 Windows 账户运行的任何程序都可以向它发指令。
- 接收端启动时会用 Windows 自带的编译器编译自己的 C# 源码，并把结果缓存在 `%USERPROFILE%\.nx-ai-bridge\cache` —— 和 NX 运行 Journal 的机制相同。能写这个文件夹的人，就能改变在 NX 里运行的代码。
- 一次只处理一个请求；耗时长的 NX 操作会让后面的请求排队。
- AI 建模会出错。请在副本上操作、经常保存，并在依赖结果之前自己检查。

## 与西门子无关

这是一个独立的开源项目，**不是西门子官方产品**，也没有得到西门子的认可。"NX" 和 "Unigraphics" 是 Siemens Industry Software Inc. 的商标。你需要**自己拥有合法的 NX 授权**；本仓库不包含任何西门子的文件（没有 DLL，没有文档）。它通过 NX 自带的 Journal 机制和你本机安装的 NX Open API 工作；是否允许以及如何对 NX 做自动化，以你的 NX 授权条款为准。

## 仓库结构

```
AGENTS.md            写给 AI 看的说明（安装、使用、排错）
install.ps1          Windows 一键设置，无需管理员权限
selfcheck.ps1        自检第一部分（NX 之外）
nx_receiver/         在 NX 内运行的部分
  NxAiBridgeReceiver.cs    你在 NX 里运行的 Journal（一个很小的加载器）
  NxAiBridgeSelfCheck.cs   自检 Journal
  kernel/                  JSON、通信、运行时编译、调度、控制窗口（不调用 NX Open）
  nxlib/                   共用的 NX Open 辅助函数
  handlers/                每组工具一个小的 C# 文件，单独编译，改完自动重新加载
  vb/                      两个 Journal 的 VB.NET 版本（备用）
src/nx_ai_bridge/    MCP 服务器、命令行、通信层、mock 接收端
tests/               测试，包含 CI 用的 NX Open 替身
docs/                通信协议、设计说明、NX 10 实测记录
```

设计思路见 [docs/design.md](docs/design.md)。

## 开发

```bash
uv venv && uv pip install -e ".[dev]"
uv run pytest                      # 不需要 NX
python scripts/sync_loaders.py     # 修改加载器 Journal 之后运行
```

## 许可证

[MIT](LICENSE)
