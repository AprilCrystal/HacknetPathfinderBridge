# DeepSeek Harness / TUI

Bridge 不需要按 DeepSeek 模型重新编译。DeepSeek Harness 官方 MCP 客户端插件支持本地 stdio；本仓库提供的配置按其文档编写，尚未在本机安装 Harness 或调用 DeepSeek API 进行端到端验证。

## 配置

1. 在游戏电脑上构建 Host，安装 Bridge + Freezer 后启动 Pathfinder。
2. 复制 [overlay 示例](../examples/deepseek-harness.cordis.yml)，替换 command 和 cwd 的绝对路径。不要直接覆盖现有 Harness 配置。
3. 将该 overlay 交给现有 Harness profile。例如：

```powershell
dsh web --patch C:\Your\Repo\examples\deepseek-harness.cordis.yml
# 已安装并配置 TUI profile 时：
dsh --profile tui --patch C:\Your\Repo\examples\deepseek-harness.cordis.yml
```

TUI 是交互界面的称呼，不是一个固定 MCP 配置格式。这里的 tui 命令要求该 profile 已存在；其他 TUI 客户端按自身的 stdio MCP 配置接入。通用 [mcpServers JSON](../examples/mcp-stdio.json) 只用于接受这种结构的客户端，不能替代 Harness 的 Cordis overlay。

Harness 与游戏尽量都在 Windows 原生环境、同一用户和权限级别运行。直接运行在 Linux/WSL/容器的 Host 无法按本项目当前实现访问 Windows 当前用户命名管道；本项目没有添加远程网关。

## 首次验收提示

```text
请读取当前仓库 AGENTS.md、user.md 和 skills/hacknet-control/SKILL.md。
先列出 mcp__hacknet__hnpb_instances，确认我要看的游戏 PID。
调用 hnpb_state 暂停；返回当前 SessionEpoch、Trace、OSReady。
再 capture 一张新画面。若图片不可用，报告原因并使用通用控件文字。
先不要删除账号、重建存档或修改扩展。
```

Harness 公开工具名带服务器前缀，例如 `mcp__hacknet__hnpb_state`；实际 MCP wire 名仍是 `hnpb_state`，前缀由客户端处理。没有出现工具时先检查 profile、子进程启动错误和协议协商日志；不要根据“模型能回答游戏问题”判断工具已连接。

Harness 的图片传递还依赖附件能力和所选模型路线的图像输入声明。无法接收 PNG 时仍可读取终端、Trace、按钮、输入框和适配器 DTO；自绘界面另需适配器。

这条接入路径使用用户自己的 DeepSeek API/账户与额度，不使用 ChatGPT Plus 的会话额度；费用和限额由所选 DeepSeek 服务及宿主配置决定，本仓库不作额度承诺。

## 文档依据

2026-11-03 核对的官方资料：

- [MCP client plugin](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/mcp/mcp-client/README.md)
- [官方 overlay 示例及 patch 使用方式](https://github.com/deepseek-ai/deepseek-harness/blob/master/docs/user/guide/mcp-memory.md)
- [CLI profiles，包括可选 TUI profile](https://github.com/deepseek-ai/deepseek-harness/blob/master/apps/cli/README.md)

不同 Harness 版本的配置可变化；先以自己安装版本的 schema 和帮助为准。
