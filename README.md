# Hacknet Pathfinder Bridge

通过 MCP 控制 Hacknet Pathfinder 的实验性 BridgeMod、本地 MCP Host 和 Freezer。模型不绑定 Codex：支持本地 stdio MCP 的 agent 客户端可以连接，使用 DeepSeek 的 agent 框架也可以转发工具调用。DeepSeek 端到端接入尚未实测。

```text
模型（例如 DeepSeek） ↔ agent/MCP 客户端 ↔ Host（stdio）
                                              ↕ 当前用户命名管道
                              BridgeMod + Freezer ↔ Hacknet Pathfinder
```

## 已实现

- 状态观察：终端最后 60 行、节点、Daemon 类型、原版 Trace。
- 暂停、恢复、独立的有限 Update/Draw 预算；默认观察会暂停。
- 游戏终端语义命令派发、通用按钮激活、输入框填值/提交。
- PNG 截图、通用控件清单、第三方 `IBridgeAdapter` 注册接口。
- 会话 epoch、UI revision、当前用户管道、有界请求和超时。

这是最小 MCP tools server：协议固定 `2025-06-18`，不提供 HTTP/SSE、resources、prompts、远程服务或模型 API。客户端需接受协商返回的版本。截图需要客户端和所选模型支持图片；纯文本模型仍能使用状态、控件和适配器。

## 构建

需要 Windows、.NET SDK 10、.NET Framework 4.7.2 targeting pack，以及本机 Hacknet/Pathfinder 的开发引用。详见 [构建与依赖](docs/build.md)。仓库不含游戏源码、游戏 DLL、存档、账号或 API 密钥。

```powershell
pwsh -File scripts/Build.ps1 -DependencyDirectory C:\Your\PreparedReferences
```

将产物 `Output/HacknetPathfinderBridge/Plugin/Freeze.dll` 和 `HacknetPathfinderBridge.Plugin.dll` 放到游戏 `BepInEx/plugins/HacknetPathfinderBridge/`，只加载各一份，然后重启游戏。原版 Freezer 1.0.0 不含桥需要的 1.1.0 API。

Host 目录必须保留 `.exe/.dll/.deps.json/.runtimeconfig.json` 等运行文件，主机需 .NET 10 runtime。将客户端配置中的绝对路径改成自己的路径：

```json
{
  "mcpServers": {
    "hacknet": {
      "command": "C:\\Your\\Repo\\Output\\HacknetPathfinderBridge\\Host\\HacknetPathfinderBridge.Host.exe",
      "args": []
    }
  }
}
```

由 MCP 客户端启动 Host 子进程；无需另外启动一个独立 Host 等待连接。Host 与游戏应使用同一 Windows 用户、可互通的权限环境。推荐两者均普通权限运行。

## Agent 接入资料

- [AGENTS.md](AGENTS.md)：仓库开发与游戏控制约定；[agent.md](agent.md) 是入口说明。
- [user.md](user.md)：复制给其他 agent 的用户任务模板，不会自动授权删除存档。
- [skills.md](skills.md)：skill 入口；实际文件为 [SKILL.md](skills/hacknet-control/SKILL.md)。
- [客户端兼容性与 DeepSeek](docs/clients.md)：协议要求、工具转发、纯文本模式。
- [DeepSeek Harness / TUI](docs/deepseek-harness.md)：Cordis overlay、启动示例与首次接入验收。
- [工具与返回值](docs/tools.md)、[第三方适配器](HacknetPathfinderBridge/Plugin/Adapters.cs)。

这些 Markdown 文件不会自动变成所有 agent 的系统提示。将文件显式载入上下文，或按客户端的 skill 机制安装。

## 操作边界

Freezer 暂停正常 Update/Draw 和 SDL 文本输入；**不冻结后台线程、网络或墙钟**。截图本身执行一次 Draw，可能推进绘制驱动的逻辑。帧预算使用实际 dt，不是虚拟时间。自绘控件和未知 mod 变量需要适配器。超时不代表动作没有发生；先观察，再决定是否重试。

多实例必须用 PID 和截图匹配用户正在看的窗口。一次只让一个控制者修改同一实例；后台实例的成功不等于用户当前窗口的成功。

## 验证状态

本机已通过 66 项 Host/管道/预算/适配器断言和 14 项 Framework/Harmony/管道断言；真实游戏完成账户输入、截图、端口破解、代理过载、防火墙分析求解和文件读取。安全测试节点为 90 秒 Trace、四端口、15 秒代理和防火墙，约 45 秒游戏时间内破解成功。没有执行真实 DeepSeek API 测试，未宣称兼容所有 MCP 客户端或 mod。

可选的 `TestScenario` 是显式测试夹具，默认不要安装到日常游戏；仅适用于名为 `I` 的扩展。详见其目录说明。

## 来源

Bridge/Host 在 HNC 工作区中开发，现独立整理。Freezer 源码来自用户提供的 Freezer.rar，并在该基础上加入 1.1.0 帧预算接口；保留原作者标识。原压缩包及其二进制未包含。第三方依赖遵循各自许可；本仓库未替原作者授予额外许可。参见 [THIRD_PARTY.md](THIRD_PARTY.md)。
