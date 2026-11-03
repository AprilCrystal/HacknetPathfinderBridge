# MCP 工具契约

最终 schema 以运行时 tools/list 为准。所有响应的 structuredContent 为 `{ok, requestId, data, error}`，并有相同 JSON 的 text content；截图另有 PNG image content。错误使用 isError=true 和 ok=false。不要只检查 JSON-RPC 是否成功。

| 工具 | 参数与行为 |
|---|---|
| hnpb_instances | `{}`；列出 InstanceId、ProcessId 等，不保证仍可连接 |
| hnpb_state | `{instanceId,pause?:true}`；返回 data 中的状态，默认暂停；首次观察不传旧 epoch |
| hnpb_control | `{instanceId,sessionEpoch,action:"pause"/"resume"/"advance",updates?,draws?}`；resume/advance 需要 epoch；advance 各预算 0..300 且总计非零 |
| hnpb_command | `{instanceId,sessionEpoch,command}`；暂停时单行语义派发，返回 data.State 等；后台业务需要另推进 |
| hnpb_ui | inspect 读缓存；capture 执行一次 Draw 并返回 PNG；activate/fill 需要 key、revision、epoch；fill 还需 text |
| hnpb_adapters | action=list/observe/invoke；observe 指定 adapterId；invoke 另需 operation、arguments、epoch，且游戏暂停 |

工具还支持可选 requestId（instances 除外）。去重不是跨重启永久日志；不要用同一 ID 表示不同动作。UI 返回 data.Ui 的 Revision、SessionEpoch、Controls；控件有 Key/Id/Kind/Text/Masked/InputBlocked 等。密码控件不返回明文。terminal 类型允许 activate 提交，textbox 填值后应寻找所属界面的提交按钮。

```json
{"name":"hnpb_state","arguments":{"instanceId":"<live instance>"}}
{"name":"hnpb_command","arguments":{"instanceId":"<live instance>","sessionEpoch":"<observed epoch>","command":"probe"}}
{"name":"hnpb_control","arguments":{"instanceId":"<live instance>","sessionEpoch":"<observed epoch>","action":"advance","updates":120,"draws":120}}
```

这些是 tools/call 的 params 示例，不是三个连在一起的 JSON 文档。消息外层由 MCP 客户端添加 JSON-RPC request id。

成功进入游戏会改变 SessionEpoch，导致原先创建/继续游戏的 UI 操作报告会话变化。此时重新 state 验证 OSReady，不能立即重复点击创建。超时也同样不表示回滚。
