# 构建和本地依赖

Windows，.NET SDK 10，.NET Framework 4.7.2 developer/targeting pack。Freezer/Bridge 为 net472，Host 为 net10.0；RuntimeTests 为 net472 x86。仓库使用本地依赖，不向 NuGet 下载游戏程序集。

完整插件构建需将以下开发引用放到根目录 `Dependencies/`（该目录不提交）：

```text
HacknetPathfinder.exe
FNA.dll
BepInEx.Core.dll
BepInEx.Hacknet.dll
0Harmony.dll
Mono.Cecil.dll
MonoMod.RuntimeDetour.dll
MonoMod.Utils.dll
PathfinderAPI.dll
```

其中 HacknetPathfinder.exe 是暴露相关内部类型的 **publicized 编译引用**，不能保证直接从游戏目录复制的原始 EXE 可以编译此项目。请从自己合法持有的游戏和对应 Pathfinder 开发环境准备引用；不要用 publicized 编译引用覆盖实际游戏 EXE。本次验证用的是 HNC 本地开发引用；其二进制未上传。

```powershell
pwsh -File scripts/Build.ps1 -DependencyDirectory C:\PreparedReferences
```

脚本把指定目录的这九个文件复制到 Dependencies 后构建 Host、Bridge、两组测试及可选测试插件，并运行 66 + 14 项断言；不安装或启动游戏。若 Dependencies 已准备好，可省略参数。依赖版本应匹配实际游戏。本次游戏验证使用 Pathfinder 5.3.4 / HarmonyX 2.5.5 / Freezer 1.1.0。

只构建模型宿主和不依赖游戏引用的测试：

```powershell
pwsh -File scripts/Build.ps1 -HostOnly
```

只复制最终 Plugin DLL 和 Freeze.dll 到 BepInEx；不要把 Dependencies 或 publicized EXE 安装进游戏。TestScenario DLL 仅显式测试时另行安装。它要求扩展名称为 I，节点 XML 应手动放到该扩展 Nodes/BridgeSecurityLab.xml，随后调用适配器 setup。setup 不会重置现有节点的安全状态。

本仓库不包含本机 Invoke-Mcp 操作历史。`HacknetPathfinderBridge/Tools/Invoke-Mcp.ps1` 是可选单次工具调用诊断脚本，输入 JSON 为 `{tool,arguments}`；它会启动一个临时 MCP Host 并将响应存到 Output/Control，正常 agent 接入应让自己的 MCP 客户端管理 Host。
