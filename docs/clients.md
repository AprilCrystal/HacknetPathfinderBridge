# 客户端兼容性

## 当前结论

代码没有调用 Codex API。Host 是 tools-only 的本地 stdio MCP server，固定返回协议版本 `2025-06-18`。能够启动本地子进程、完成初始化并调用 tools/list 和 tools/call 的 MCP 客户端原则上可以使用；具体客户端仍需接入测试。只有 HTTP/SSE 接入能力的客户端、网页聊天窗口和单独的模型 API 不能直接连接本机命名管道。

Host 要与游戏在同一 Windows 环境、同一用户的可互通权限上下文运行。模型可以是云服务；执行 MCP 的 agent 宿主需在游戏电脑运行，或者自行提供经过授权的远程代理。本项目没有远程 HTTP 网关。

## DeepSeek / dpsk

已明确 dpsk 指 DeepSeek，主要宿主为 TUI/DeepSeek Harness。优先使用 [Harness 专用配置](deepseek-harness.md)，不需要自行编写下面的函数转发循环；下文循环只针对自建 API agent。

若 dpsk 指 DeepSeek 模型：需要 `DeepSeek ↔ agent 宿主 ↔ MCP Host`。DeepSeek 的工具调用由宿主实际执行，模型本身不执行函数。若指另一个客户端，应先确认它是否支持本地 stdio MCP、服务端返回的协议版本及图像工具结果。本仓库未使用 DeepSeek key 做端到端验证。

自建宿主的必要循环：

1. 启动 Host，向 stdin 发送 initialize，读取服务端版本；接受该版本后发送 notifications/initialized。
2. tools/list 取得 schema。将每个工具映射成模型接口的函数：name、description、parameters=inputSchema。
3. 模型生成函数调用后，本地验证名称/JSON 参数，并串行转发为 tools/call；将结果连同对应 tool_call_id 回传给模型。
4. 优先读 structuredContent；客户端若只保留 text content，可解析第一段 JSON。若 isError 或结构里的 ok=false，不要把 data 当作成功结果。
5. 不支持图片的模型仅接收 text/structuredContent，避免把 PNG base64 塞入普通文本。自绘界面需要适配器，不以缺少视觉为由猜坐标。
6. 使用所选 DeepSeek API 的当前工具消息格式；若开启 thinking，遵循其推理上下文保留要求。不要把 MCP inputSchema 原样当作 strict 模式兼容声明：这里有可选参数、maxLength，需由宿主做显式 schema 适配或使用非 strict 工具调用。

以下是概念映射，不是一个已实现的 DeepSeek runner：

```python
functions = [{"type": "function", "function": {
    "name": t.name, "description": t.description, "parameters": t.inputSchema
}} for t in mcp_tools]
# model tool call -> JSON parse/validate -> await mcp.call_tool(name, arguments)
# return the matching tool_call_id and textual/structured result to the model
```

API key 只交给宿主，不传给 Bridge。工具转发的普通 agent 不需要访问游戏源码或 GitHub 凭据。

## stdio 细节

每行一个 UTF-8 JSON-RPC 消息，stdout 仅协议，stderr 诊断；不能使用 Content-Length framing，也不能将启动日志混入 stdout。客户端拥有子进程生命周期，关闭 stdin 会结束该 Host。可选 `--registry <directory>` 用于测试隔离注册目录；游戏默认目录是当前用户 TEMP/HacknetPathfinderBridge。

建议连接超时至少覆盖 12.5 秒管道请求窗口；长任务拆成多次有限 advance。不同 Host 可以连接同一游戏，但不会因此隔离游戏状态；只有一个操作者应发出变更动作。

参考（2026-11-03 核对）：

- [MCP 2025-06-18 stdio transport](https://modelcontextprotocol.io/specification/2025-06-18/basic/transports)
- [DeepSeek Tool Calls](https://api-docs.deepseek.com/guides/tool_calls/)
- [DeepSeek Thinking Mode](https://api-docs.deepseek.com/guides/thinking_mode/)
