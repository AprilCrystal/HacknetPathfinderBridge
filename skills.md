# Skills

实际 skill：[hacknet-control/SKILL.md](skills/hacknet-control/SKILL.md)。

支持目录式 skills 的客户端可以安装 `skills/hacknet-control`；其他 agent 将其作为上下文文件读取。这个 skill 需要已连接的六个 `hnpb_*` MCP 工具，不依赖某个模型厂商，也不自动安装 MCP 服务。

不要将本文件名当作跨客户端统一的自动加载标准。API-only agent 需要在自己的宿主中实现 MCP 客户端和工具调用循环，详见 [docs/clients.md](docs/clients.md)。
