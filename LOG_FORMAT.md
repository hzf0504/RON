# 日志格式与分析边界

助手 0.3.0-experimental，日志 schema 2。所有诊断保存在便携目录内，不自动上传。

## 延迟记录

自动记录使用独立的 `*-latency-*` 会话文件夹。

- `latency-session.json` 保存延迟 schema 1、会话 ID、游戏 PID、起止 UTC 毫秒、来源、单位、游戏哈希、结束原因和错误。`OfflineFixture=true` 表示合成离线验证数据。
- `latency-000001.jsonl` 起为顺序编号的 UTF-8 日志，每段约 16 MB，旧段保留。
- `rtt` 事件保存 `sequence`、`utcMs`、本机单调时钟 `tickMs`、`peerId`、`playerLabel`、`side` 和 `rttMs`。玩家名称默认仅保存在本机，导出时替换为包内一致的标签。
- `peerId` 组合游戏分配的 PlayerId 和对象索引，标识本次连接。重连或切换世界后产生的新对象会形成新的连接条目，同名玩家也保持独立。
- `gap` 事件显式记录队列溢出丢弃数量和无效样本数量。曲线中的空缺和三秒未更新状态用于表达数据缺失，不能据此计算网络丢包率。
- `source_start` 与 `source_end` 保存来源及停止原因。游戏直接退出可能缺少最终来源事件，此时以会话元数据的 `game_exited` 为准。

采集点是当前构建 `APlayerState::UpdatePing(float InPing)` 的虚函数入口，传入秒数乘 1000 转为 ms，在原函数执行后的回调中保存原始参数。两个继承该实现的虚表槽位为 RVA `0x6DA26D8`、`0x743B5F8`，方法 RVA 为 `0x331C140`。构建的完整 SHA-256、方法签名和原始槽位均核对通过后才启用采集。原函数输入、调用次数和返回控制流保持其原有语义。

此入口和字段偏移来自本机游戏文件的静态分析。合成夹具验证了回调、单位、尖峰、异常恢复、分段读取及历史界面，真实游戏的多人连接、玩家归属和采样频率仍待运行时核验。

## 文件

| 文件 | 内容 |
|---|---|
| `assistant.jsonl` / `.1`～`.3` | 每行一个事件，UTF-8。操作开始/完成/取消、异常类型/HResult/堆栈、附加与停止结果。每段约 4 MB，总计约 16 MB，旧段会轮换。 |
| `test-context.json` | 共同测试编号、房主/客机角色、网络类型、助手版本、本机会话 ID。 |
| `markers.jsonl` | 用户手动标记的阶段、备注、UTC 和单调时钟。 |
| `session.json` | 游戏 PID、观测/候选模式、所选接口、朋友列表、开始时间。 |
| `network.json` | 本地接口 IPv4、MTU、状态、隧道客户端是否运行、对朋友地址的预测路由。只读系统状态，不读取喵服配置。 |
| `native.jsonl` | 游戏进程中的 EOS/WinSock/网卡枚举事件，默认每秒汇总；64 MB 后停止观测并恢复。 |
| `files-before.json` / `files-after.json` | 游戏 EXE、EOS 和已有 Steam 补丁相关文件的哈希与未变判断，不含文件内容。 |
| `game-settings.json` | 只读取跨平台开关；不导出整个游戏 INI。 |
| `udp-probe.json` | 助手测试 UDP 的独立结果，`kind=helper_udp_only`。 |
| `report.md` | 易读摘要和证据限制，不自动宣布适配或性能验收成功。 |

## 时间与关联

助手事件有 `utc`（带 UTC 偏移的 ISO 时间）、`tickMs`（本机启动后的毫秒）、`sequence` 和 `sessionId`。原生日志有 `utcMs`（Unix UTC 毫秒）和 `tickMs`；`start` 提供 PID、模式和接口。

不同电脑用共同 `testRunId`、角色和 UTC 对齐。`tickMs` 不能跨电脑比较。原生流量是约一秒的聚合，记录时间为汇总写出时间，并非每个数据包的精确时间。机器时钟未同步时需将误差列为限制。

## 原生事件

- `start`：schema、时钟、游戏 PID、mode（1 观测 / 2 候选筛选）、接口。
- `eos_loaded`：实际加载的 SDK 版本，不代表正在对局。
- `sample`：`hooks`、`eosPolls`、`eosReceiveCalls`、`eosSendCalls`、`eosSendAccepted`、`eosReceivePackets`/Bytes、网卡枚举/筛选/缺失次数、`metadataDropped`。
- `adapter_view`：最近实际返回给 EOS 的接口视图，包含原列表数量、family/flags、是否筛选、可见接口索引/类型/状态/IPv4。只在视图变化时写出；并发时可能合并中间变化。
- `eos_udp`：成功返回的同步 UDP 发送/接收调用，方向、实际 socket 本地端点、远端端点、包数/字节数、`predictedInterface`。当前覆盖 EOS 导入的 sendto/recvfrom 和已连接 UDP send/recv，不声称覆盖全部异步网络 API。
- `restored`：恢复槽位数/总数、`controller_stop` / `heartbeat_expired` / 日志问题等原因。助手心跳失去约 5 秒后自动停止干预；不会在其他线程可能持有 hook 指针时强行卸载 DLL。

`GetBestRoute2` 返回的是预测路由；`sendto` 成功返回表示本机栈接受数据。二者都不是线上的捕获证明。EOS UDP 可能是连接探测，P2P API 调用可能只存在于排队阶段。因此还需双端匹配和实际对局时段/接口证据。没有事件不一定代表没有该流量：晚附加、其他 API、日志丢失或版本差异都需考虑。

## 导出

“导出脱敏诊断 ZIP”只使用代码中的文件白名单，生成 `bundle-manifest.json`，其中含导出文件 SHA-256、字节数和省略的不完整/损坏 JSONL 行数量。运行中导出固定当时文件长度，不会一直追读增长文件；推荐停止会话后导出，以包括最后的恢复记录。

默认 IP 用每包随机盐的标签替代，标签只在一个 ZIP 内稳定，跨 ZIP 不相同；端口、接口索引、计数、版本仍保留。绝对路径、自定义网卡名和敏感字段会被隐藏。原始本机日志不会因导出而更改。所有原始捕获、游戏文件、存档、喵服目录、内存转储和其它会话均排除。上限为原文件总计 96 MB。

如果仅有离线快照或报错，报告明确说明没有游戏流量证据。不要用原始 `sessions` 文件夹替代脱敏 ZIP 直接发给不需要这些网络信息的人。
