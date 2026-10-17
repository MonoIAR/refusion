# Refusion 联机加入失败案例记录（2026-10-10 ~ 2026-10-11）

> 状态：**根治方案已实施（2026-10-11，控制包瘦身 + DynamicsAssignment 分片，版本 0.2.0）**；待双端实测确认。核心问题（约 1.3–1.8KB 的入服广播无法送达加入者）的根因分析与修复设计见 §9；实测尺寸验证清单见 §9.5。本文档记录所有已知事实、日志证据与待验证实验，供后续会话与协作者直接接手。**§9 为 2026-10-11 全链路代码审计的结论。**

## 0. 一句话总结

join 请求（~1–1.5KB）能到达 host 并被受理（host 端有日志、有注册），host 受理后发出的**自 `ConnectionResponse` 广播（~1.3–1.8KB）从未被任何加入者处理**（LocalID 永远为空），而 ~0.5KB 的 `LevelLoad` 等小消息能到达。加入者因此永久处于"半加入"状态；此前半加入者还会因 sender=0 消息被 host **自动踢出**，形成"服务器只能容纳一个人"的表象。四人四网络全部复现，同一份广播给 host 自己的回环有时能到、有时不能。

## 1. 参与者与网络（来自 relay 日志 endpoint）

| 玩家 | UUID | 10-10 晚 endpoint | 10-11 凌晨 endpoint | 结果 |
| --- | --- | --- | --- | --- |
| ChakerAt（仓库维护者） | 12b8908a-… | 103.85.86.18（可正常加入） | **103.151.172.47（换了 IP，无法加入）** | 凌晨 3 次尝试全失败 |
| CAitStudio | f485b947-… | 125.79.232.5（可正常加入） | 125.79.232.5（未变） | 凌晨 00:06/00:07 自己两次尝试也失败；之后转为 host，host 侧一切正常 |
| Administrator #1 | 83de37ed-… | 171.14.166.39（可正常加入） | **180.164.149.37（换了 IP，无法加入）** | 整晚 20 秒循环入/退 |
| Administrator #2 | 5f5610f5-… | 222.83.225.164（丢请求，4/4 失败） | 222.83.225.164（未变） | 两天持续失败 |

环境：relay 为**不带 status API 的旧版**（与 10-10 晚正常工作时的 relay 同源，Server ID `415c6d16…` 一致、转发代码未变）；客户端为"修复轮"新构建（含 C1–C7，DEBUG 构建，故所有 `#if DEBUG` 日志可见）。

## 2. 症状清单

| # | 症状 | 章节 |
| --- | --- | --- |
| A | host 只见部分玩家（"4 人只见 2 人"） | §4.1 |
| B | 加入者崩溃/无本地玩家导致位置不同步 | §5.1 |
| C | "只能容纳一个人"（除 host 外无人能进） | §4.2、§5.2 |
| D | 加入时报"host 在加载关卡"（host 实际没在加载） | §5.3 |
| E | relay 日志"没有任何反应" | §5.3 |
| F | 加入时加载两遍关卡，加载完提示 "The server did not respond." | §5.4 |
| G | **核心未解**：入服广播到不了加入者 | §6 |

## 3. 加入流程机制（代码事实，行号以当前工作区为准）

1. 客户端 `Hello` → relay 回 `Welcome`（客户端记录 `_smallId/_sessionToken`，置 `_fusionJoinDeadlineUtc`，发一次 `ConnectionRequest`）。
2. 加入者是"半加入"：`_clientId != 0` 但 `ApplyLocalID()` **尚未执行** → `PlayerIDManager.LocalID == null`、`LocalSmallID == 0`。
3. host（伪 host）在 `ConnectionRequestMessage` 里校验并接受；host 的**注册依赖自己广播的 relay 回环**（relay `ServerBroadcast` → `BroadcastAllAsync` 含 host 自身）。
4. host 受理后依次发：`SendPlayerJoin` 广播（**自响应 ConnectionResponse，~1.3–1.8KB，全客户端含 host 与加入者**）→ 逐个 `SendPlayerCatchup`（仅加入者）→ `LoadSender.SendLevelLoad`（仅加入者，~0.5KB）→ `DynamicsAssignment` → `LobbyInfo`。
5. 加入者处理到自己的响应时才 `ApplyLocalID()` → `CreateLocalPlayer()` → `OnJoinServer()`。
6. 客户端重试：`LocalID == null` 期间每 2 秒重发 `ConnectionRequest`，30 秒总超时 → `Disconnect("The server did not respond.")`。
7. host 幂等重接受：同 smallid 重复请求 → 重发同步而非 deny；跨 smallid → 清旧条目；加载中 → 静默丢弃（客户端重试自愈）。

消息尺寸估算（决定是否 IP 分片）：`ConnectionRequest/Response` 含 `SerializedAvatarStats`(固定 420B) + metadata 字典 + 头像 barcode + 装备列表，原始约 650–1300B；base64 ×1.33 + JSON 包裹后 **约 1.0–1.9KB**（＞1472B 即分片为 2 个，1500 MTU 的 UDP 不分片上限为 1472B）。`LevelLoad` 约 0.4–0.6KB，单包。

## 4. 历次测试的日志证据

### 4.1 10-10 晚（旧构建，四人类）

- **"host 只见 2 人"实锤**：host（ChakerAt）断开清场日志只有
  `[21:09:57.821] Unregistered NetworkPlayer with ID 0.` / `…ID 2.`
  ——host 的列表里只有自己(0) 与 Administrator#1(2)。FAIXI（@4/@8/@9 三次）与 Administrator#2（@3/@5/@6/@7 四次）**从未在 host 侧注册过一次**。
- 排除项：host 未拒绝任何人（deny 会留痕）、host 无处理异常（`Failed handling`/`Failed reading` 均为 0 条）、relay 无 reject/限流记录。
- 结论：这些玩家的 `ConnectionRequest` 从未进入 host 处理器（未发出 / 客户端→relay / relay→host 任一跳丢失），relay 仅靠 5 秒心跳维持其在线（15 秒超时判死）。
- 同场其它已确证问题：自定义头像/物品 mod 下载超时（`Mod request ... timed out`）；`MenuLocation.RefreshBanList` NRE；CAit 加入时的 `ConnectionResponseMessage` 内 NRE（见 §5.1）。

### 4.2 10-11 凌晨（新构建）

relay 日志原文（时间 = UTC，+8 为本地）：

```
16:06:44 Player joined: CAitStudio, id=12      ← CAit 第一次尝试
16:07:04 Player left:  CAitStudio, id=12      ← 20.5s 后（= 客户端超时断开）
16:07:40 Player joined: CAitStudio, id=13
16:08:01 Player left:  CAitStudio, id=13      ← 同样 20.8s
16:22:37 Player joined: ChakerAt, id=23        ← 维护者第一次尝试
16:22:38 Server settings updated and broadcast ← +0.42s：host 完成了对他的注册！
16:23:03 Player kicked: ChakerAt (…) by CAitStudio: Kicked from server.  ← +25s 自动踢
16:23:31 Player joined: …id=25 / 16:23:32 Player kicked … ← +0.58s 秒踢
16:24:35 Player joined: …id=26 / 16:24:36 Player kicked … ← +0.58s 秒踢
（整晚两个 Administrator 的 id 连号 14→29，每次恰好约 20 秒生命周期 = 客户端加入超时节奏）
```

CAit 侧（当时他是 host）日志原文：

```
00:22:37.934 Accepted connection from 13765101051402569489 as small id 23.
00:22:41.887 / 43.906 / 45.927 / 47.913 / 49.926 / 51.970 / 54.012 / 23:02.912
             Re-accepting connection request from … (small id 23).   ← 8 次重接受
00:23:02.942 Unregistered NetworkPlayer with ID 23.   ← 与 relay 踢人时刻对应
00:23:31.968 Connection request from … had a stale small id; replacing the old entry.
00:23:31.977 Accepted … as small id 25.
00:23:32.211 Unregistered NetworkPlayer with ID 25.
00:24:35.533 Accepted … as small id 26.
00:24:35.771 Unregistered NetworkPlayer with ID 26.
```

CAit 自己的加入（作为加入者，00:06/00:07，当时 host 是别人）：

```
00:06:43.760 Sent a connection request to the dedicated server.
00:06:43.806 Received level load for …VoidG114!        ← +46ms：host 已受理！
00:06:46…00:07:03 又 8 次重发
00:07:04.205 The server did not complete the Fusion-level join before the timeout.
```

维护者的加入（00:22 起）：同样 9 次重发、`Received level load` 两次（00:22:38 / 00:23:32），永远没有处理到自己的响应，20 秒超时。

**结论**：host 侧新代码（幂等接受/重接受/stale 清理）**全程按预期工作**；加入者的请求到达 host；小消息（LevelLoad）到达加入者；自响应广播从未被加入者处理；随后发生自动踢人（见 §5.2）。

## 5. 已确证的次级故障与修复

### 5.1 本地玩家崩溃（Bug B，已修）

加入者在**场景加载中途**处理自己的 `ConnectionResponse` 时崩溃（`RigAbstractor.FillTransformArray → Component.get_transform()` NRE，rig 处于过渡中间态），导致会话无本地 NetworkPlayer（姿态从不外发）、无重试。修复：`NetworkPlayer.FindRigManager` 增加就绪守卫（rig/控制器非空、非加载中）、延迟绑定协程、场景钩子先订阅后绑定、`OnFoundRigManager` 幂等守卫、`NetworkPlayerManager` 场景初始化安全网（重建缺失的本地玩家）。

### 5.2 半加入者的自动踢人（"只能容纳一个人"的执行者，已修）

链条（三方日志互证）：
1. 半加入客户端 `LocalSmallID == 0`，其游戏发出的任何普通消息都带 Sender=0；
2. host 的防伪造校验 `ValidateReceivedID` 发现"已注册 id(23/25/26) ≠ 发送者(0)"→ `DisconnectUser`；
3. relay 记 `Player kicked: … Kicked from server.`（此文案 = 游戏内踢人路径的固定字符串）；host 端 `Unregistered NetworkPlayer with ID 23/25/26` 与三条 kick 一一对应（时间差 <0.4s）。

修复：
- `NativeMessageHandler`：校验失败**只丢包 + 5 秒限频警告，不再踢人**；
- `DedicatedServerNetworkLayer`：**半加入期间（`LocalID == null`）只放行 `ConnectionRequest`**（按消息 Tag 门控 `SendToServer`/`BroadcastMessage`），从源头消灭 sender=0 消息。

### 5.3 加入报"host 在加载关卡" / relay"无反应"（已修）

- relay 的 `_isLoading` 门禁：host 开始加载发一次 `ServerLoadingState(true)`、加载完发一次 `(false)`——**均为单发 UDP，false 丢失即永久卡 true**；relay 在 `_isLoading=true` 时**静默拒绝**所有 Hello（Reject 不写日志）→ 加入者看到"host 在加载关卡"而 host 无加载、relay 日志无反应。极端情况：卡住时连 host 自己都连不上，无人可复位（只能重启 relay）。
- 修复（relay）：无 owner 时忽略、切换 owner 清零、会话重置清零、5 分钟自动过期；**所有 Hello 拒绝（version/loading/full/uuid）限频写日志**；`Loading state: True/False` 转换日志。
- 修复（host 侧）：`ConnectionRequestMessage` 的加载检查由"deny"改为**静默丢弃**（deny 是致命的：客户端收到即断开放弃），客户端重试将在加载结束后的下一次（≤2s）自动成功；Fusion 级加入总超时 20s → **30s**（覆盖 host 开服时的场景重载窗口）。

### 5.4 加入时加载两遍（已修）

relay 在 `Welcome` 时就把服务器场景以 `SceneState` 推给加入者（客户端立即加载，**第一次**）；随后 host 受理消息里的 `LevelLoad` 又重置目标标志位 → 第一次加载完成后自动加载器再次触发（**第二次**）→ 30 秒超时正好落在第二次加载结束后（即"加载完告知 The server did not respond"）。

修复：**半加入期间忽略 relay 的 `SceneState`**（`DedicatedServerNetworkLayer`），加载只由 host 受理后的 `LevelLoad` 驱动——同时符合 audit doc 的"先建档、后场景数据"顺序原则。

## 6. 核心未解问题：入服广播（~1.3–1.8KB）到不了加入者

### 6.1 证据汇总

- 请求（~1–1.5KB）**能**到 host：host 每次受理成功（settings 广播 + `Accepted connection from …` 日志）；
- `LevelLoad`（~0.5KB）**能**到加入者（目击数次）；
- 自响应广播（~1.3–1.8KB，与请求同尺寸级）**从未**被加入者处理（`LocalID` 永远为空）；
- 同一份广播给 **host 自己的回环**有时能到（+0.42s 的 settings 广播）、有时不能（16:06 CAit 加入时 host 侧无注册记录）；
- 四人四网络全部复现；同一台机器（CAit）16 分钟内"同尺寸包"一死一活 ⇒ **不能归结为单一网络路径问题**；
- 接收端代码在广播这条路径上与 10-10 正常工作时一致（唯一新增是 C2 幂等守卫，且已 grep 验证：插入 `PlayerID` 的路径只有 `ConnectionResponseMessage` 与 `OnStartServer` 两处，**首次响应不可能被守卫跳过**）；
- 已排除：路径 MTU 过小（`ping -f -l 1472` 与 `-l 1400` 均通 → MTU ≥ 1500）；host 处理异常（无日志）；deny（会留痕）；relay 限流/拒收（无日志）。

### 6.2 当前最可疑方向

**大于 1472B 的 UDP 分片被沿途设备丢弃**：1.3–1.8KB 必然分片为 2 个 IP 包，任一分片丢失整包报废；小包（~0.5KB）单包直达。尚未验证；需做分片 ping 对照。

### 6.3 待验证实验（优先级从高到低）

1. **分片 ping 对照**：`ping -l 2000 <relay 地址>`（**不带 -f**，会产生分片）vs `ping -l 1400`。2000 丢而 1400 通 = 分片被丢实锤。注意：`-f 1472` 通只说明 MTU≥1500，不能排除分片被丢（此前测试结论需修正）。
2. **裁剪对照实验**：一台机器用**旧 DLL 当加入者** + 新 host，反向再测一次。旧加入者也失败 → 偏环境/网络；旧能进、新不能 → 继续在我方代码里找（重点 C2/C4 路径）。
3. **诊断日志判读**（最新构建已内置）：
   - 加入窗口内有 `Relay packet while joining: <类型> (<字节数>)` 且其中有 ~1.3–1.8KB 的 `ClientMessage` → 大包到达了 socket；
   - 随后无 `Received connection response for …` → 解析/分发环节丢（继续查客户端）；
   - 完全没有大包记录 → 包根本没到（分片/路径问题坐实）。
4. **relay 端判读**：新 relay 会记录 `Player joined` / `Loading state:` / `Rejected a connection …`；若加入者是"连上 relay 但进不去"，必有 `Player joined`，若连 `Player joined` 都没有则是更上游的问题（DNS/地址/客户端未发出）。
5. 注意 relay 按 **UTC 日期**命名日志文件（本地 08:00 才翻页）——查当天的日志别找错文件。

### 6.4 已知根治方向

- **控制包瘦身**：把 `SerializedAvatarStats`(420B) 从 `ConnectionRequest/Response` 拆出，复用已有的 `PlayerRepAvatar` 消息，使握手包落入 ~0.5–0.9KB 单包区间（对"分片丢包"类的直接解）；
- **传输层改造**：二进制帧化 + 分片/重组 + 可靠化 + 准入状态机（audit doc 的规划，终极方案）。

## 7. 修复轮次与文件清单

| 轮次 | 内容 | 状态 |
| --- | --- | --- |
| 轮 1（commit `4827bef0` "修复联机加入可靠性"） | C1 本地玩家守卫+安全网；C2 接收端幂等+LevelLoad 去重；C3 host 幂等接受+日志；C4 客户端重试+超时；C5 mod 请求日志+重试；C6 次级 NRE（CatchupManager/MenuLocation/LocalPlayer）；C7 audit doc | 已提交 |
| 轮 2 | 校验失败不再踢人；半加入期上行门控；诊断日志（含连接响应追踪）；host 加载期改丢弃；超时 30s | 工作区（未提交） |
| 轮 3 | relay：`_isLoading` 自愈 + 全部 Hello 拒绝写日志 + `Loading state` 日志；客户端：半加入忽略 relay `SceneState`（修二次加载） | 工作区（未提交） |
| 附加 | relay Status HTTP API（`GET /status`，只读 JSON，含 `isLoading`/玩家/operators/bans/host 上报的 lobby；默认关闭，`statusPort`+可选 `statusToken`） | 工作区（未提交） |

关键文件：`LabFusion/src/Network/Layers/DedicatedServer/DedicatedServerNetworkLayer.cs`、`LabFusion/src/Network/Messages/Server/{ConnectionRequestMessage,ConnectionResponseMessage}.cs`、`LabFusion/src/Network/Messages/NativeMessageHandler.cs`、`LabFusion/src/Entities/Player/{NetworkPlayer,NetworkPlayerManager}.cs`、`RefusionRelay/Program.cs`、`docs/dedicated-server-network-layer-audit.md`。

## 8. 当前状态与下一步

- **最新一轮测试**（全部最新构建）：维护者作为 host 时**host 侧日志无任何变化**——加入者的请求仍未到达 host。§6 核心问题依旧。
- 下一步：执行 §6.3 的实验 1（分片 ping）与实验 3/4（收集加入者诊断日志 + relay 新日志），二者结合即可把"代码 vs 网络"一刀切开。
- 在结论明确前，建议：每次复现保留 `Latest.log`（加入者与 host 双方）与 relay 当日日志，时间戳对齐 UTC+8。

## 9. 全链路代码审计结论（2026-10-11）

对 22a8580f → HEAD 的全部改动与当前工作区（轮 2/3）做了逐行审计，覆盖客户端收发路径、host 受理路径、relay 转发路径与消息序列化层。结论：**交付路径上不存在任何因修复轮引入的回归**；"修复后更糟"是症状表达方式变化 + 当夜网络路径漂移的叠加，不是代码倒退。

### 9.1 逐项复核（均已通过）

| 审计点 | 结论 |
| --- | --- |
| C2 幂等守卫（`ConnectionResponseMessage`） | 不会跳过首次响应：加入者首次收到响应时 `PlayerIDManager.GetPlayerID(自己)` 必为 null（host 只 `ReserveSmallID` 不 `Insert`，加入者自身条目要靠本条响应插入）。early-return 要求 existing 同 smallid，只可能发生在响应已被处理之后。 |
| 静默吞包可能 | 若包到达且解析成功：要么成功加入（LocalID 置位、停止重试），要么必有 `Failed handling network message of tag 2` 错误日志（非 DEBUG 也记录）。加入者日志两者皆无 → **包没有到达**，非代码吞包。 |
| 轮 2 上行门控（`CanSendGameMessage`） | 判断正确：`NetMessage.Buffer[0]` = `MessagePrefix.Tag`（前缀最先序列化 Tag），ConnectionRequest(tag=1) 被放行；门控只作用于上行发送。 |
| `ValidateReceivedID` 防伪造 | 仅对 `isServerHandled=true` 的包生效；relay 转发的 host 广播/定向包一律 `IsServerHandled=false`（Program.cs `ServerBroadcast`/`ServerTarget` 分支），不经过该校验。 |
| `CheckExpectedConditions(ClientsOnly)` | `isServerHandled=false` 时不抛异常，host 处理自身回环不受阻。 |
| 缓冲区截断 | 客户端与 relay 均用 `UdpClient`（65536B 内部缓冲）整包收发；UDP 数据报要么完整到达要么整体丢失，不存在"到达但截短"。JSON 解析失败静默 continue，但整包丢失时根本不会触发。 |
| relay 限流 | 500 pps + 1500 burst，加入流程每客户端每秒仅几包，远离阈值。 |
| relay 转发改动 | 22a8580f → HEAD 仅新增 status API（204 行纯增量）+ 轮 3 的 loading 自愈/日志；`HandleRawForwardAsync`/`BroadcastAllAsync` 转发逻辑零改动，与"10-10 能正常工作时的转发代码"逐字节一致。 |

### 9.2 消息尺寸复核（"修复使包变大越过分片阈值"假说被否定）

- `ConnectionRequestData` 与 `ConnectionResponseData` 在 22a8580f 与当前版本逐字段一致（`PlayerID` 自始至终把 metadata 字典 + 装备列表整个带回，见 `PlayerID.GetSize()` = 8+1+字典+列表）。
- 精确账目：响应 = 请求 − Version(12B) − BackupID/SmallID 差(−1B) + host 追加的 `PermissionLevel` 条目(~20B) + IsInitialJoin(1B) ≈ **请求 ± 20B**。两者主体相同：`SerializedAvatarStats`(420B 固定) + 头像 barcode + metadata + 装备。
- 最终封装（base64 ×1.34 + RelayPacket JSON 头 ~130B）：原始 ~900B（modded 玩家常见值）→ **~1.35–1.9KB**，随 mod 负载浮动，正好骑在 1472B 单包上限上；裸机玩家 ~1.1–1.2KB 可单包。`LevelLoad` 两个 barcode ≈ **0.3KB** 单包。
- 推论：请求与响应同尺寸级，同一脏路径上两者都该丢——实测"请求通、响应死"指向**方向性不对称**（见 9.3），而非纯尺寸阈值。

### 9.3 S1 与 S2 是同一根因：特定参与者的特定方向丢大 UDP 包

统一两类症状（"ChakerAt 做 host 时加入者请求不到"= S1；"CAit 做 host 时响应到不了加入者"= S2）：

| 时刻 | 事件 | 干净腿 | 脏腿 |
| --- | --- | --- | --- |
| 16:22 (UTC) | ChakerAt 请求 → CAit host | ChakerAt 上行 ✓（8 次受理） | 响应 relay→ChakerAt 下行 ✗ |
| 16:22 | 同一份响应广播回环 | relay→CAit ✓（+0.42s settings 广播即 `OnPlayerJoined` 经回环触发） | — |
| 00:22 起重试序列 | ChakerAt 第 1 次重试(+2s) | 其余 7 次 ✓ | 该次上行 ✗（host 无对应 re-accept 日志） |
| 00:06 | CAit 加入 host X | relay→CAit 下行 ✓（LevelLoad 46ms 达） | 响应未达 → host X 上行 ✗（relay 亦无 X 注册痕迹） |
| 10-10 → 10-11 | ChakerAt、Admin#1 换 IP | 换 IP 前能进 | 换 IP 后不能进 |

- host 的注册**完全依赖**自己广播的 relay 回环（host 端只 `ReserveSmallID`，`PlayerID.Insert()` 发生在处理回环响应时）→ 16:22 的 settings 广播（`LobbyInfoManager.OnPlayerJoined` 钩子 → `PushLobbyUpdate`）就是回环存活的铁证。
- 脏路径是**方向不对称、按路径决定、且跨版本稳定**的：与 IP 分片被沿途设备（运营商级防火墙/跨境中间盒）过滤的特征完全吻合；mod 下载超时（ModInfo 大包）是同症状的另一处。
- 当夜 host 恰好是唯一路径干净的人（CAit/NZ），其余三人全部脏路径 → "只有一个人能加入"。

### 9.4 对"修复后更糟"的判定

1. **交付路径零回归**：两版本的消息尺寸、收发代码路径一致；轮 2/3（工作区）内容均为正确方向的修复。
2. **观感差异的真实来源**：
   - 22a8580 时期：客户端单发请求、无重试无超时 → 脏路径玩家**静默滞留**（表象 = "host 只见 2 人 + 同步差"）；验证踢人（22a8580 已存在的 `DisconnectUser`）极少触发，因为滞留者很快不再发消息。
   - 4827bef0 后：C4 重试 + 超时让半加入者每 2s 复活一次、C5 让它持续发 mod 请求 → sender=0 消息喂给既有的验证踢人 → **"20 秒循环 + 秒踢"**（表象 = "完全进不来"）。轮 2（未提交）已从两端拆掉该放大器。
3. 22a8580 时期的"同步有大问题"很可能部分同源：大包过不了脏路径。**更正（2026-10-11 实施时核对代码）**：`DynamicsAssignment` 的载荷是 **gamemode 元数据字典**（`GamemodeRegistration.GetExistingMetadata()`，每个 gamemode 的 barcode → string 字典），不是整场景动态实体列表；道具同步的大包风险实际在**逐实体 catchup/spawn 消息**（`EntityDataRequest`/`NetworkPropCreate` 等）与 mod 数据（`ModInfoResponse`）。控制包瘦身已包含 DynamicsAssignment 分片（§9.5 第 4 项）；逐实体消息归终态二进制帧方案。

### 9.5 修订后的验证与修复顺序（取代 §6.3 优先级）

1. **加入者跑轮 2 诊断构建**（现有 `#if DEBUG` 日志即可，无需新代码）：加入窗口找 `Relay packet while joining: ClientMessage (N bytes)`。无大包记录 = 网络丢包坐实；有大包但无 `Received connection response` = 推翻网络结论、回到代码（按 §9.1 审计预期不会发生）。
2. **host 端加一行发送尺寸日志**（`SendRelayPacket` 记录 datagram 字节数）：直接量出响应包是否 >1472，同时校准 §3 的估算。
3. 分片 ping 对照（原 §6.3.1，注意不带 `-f`）。
4. **根治 = 控制包瘦身 —— 已实施（2026-10-11，FusionVersion 0.2.0）**：`ConnectionRequest` 瘦身为 `{BackupPlatformID, Version, Username}`（~0.2KB 数据报）；`ConnectionResponse` 只带 `PlayerID` 裁剪快照（Username + host 权威 PermissionLevel，~0.3KB）；头像复用 `PlayerRepAvatar` 定向补发（~0.8KB）；完整 metadata+装备走新 `ConnectionProfile`（tag 216，~0.25–0.65KB）；`DynamicsAssignment` 经新 `ChunkedPayload`（tag 217）恒定 640B 分片（每片 ~1.0KB 数据报）。全部加入关键消息 ≤~1.1KB，低于 1472B 分片阈值。host 每 2s 重接受重发（dynamics 换新 transferId）天然自愈。方案与消息集详见 audit doc "Join Handshake Slimming (2026-10-11)"。这同时是修复与最终实验——同一脏网络重测立即见分晓；实测尺寸表待双人测试后回填。
5. 中期按 audit doc 规划做二进制帧 + 分片重组 + 可靠化（彻底解决，但工程量大，不阻塞第 4 步）。
