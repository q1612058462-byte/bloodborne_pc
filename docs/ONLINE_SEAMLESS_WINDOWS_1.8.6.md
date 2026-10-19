# Windows 在线无缝联机：代码、部署边界与验收方案（v1.8.6）

本文件是 `codex/xiyuhanyans-windows` 分支的当前方案入口。旧的
[`ONLINE_SEAMLESS_ARCHITECTURE.md`](ONLINE_SEAMLESS_ARCHITECTURE.md) 记录原型阶段的设计，
[`BBSC_CLOUD_1.8.1_RUNBOOK.md`](BBSC_CLOUD_1.8.1_RUNBOOK.md) 记录 1.8.1 时的云端排障；
其中“尚未完成双机测试”等当时状态不应当作本版结论。

## 当前状态与边界

- 用户已报告：既有云端部署可让不同网络的两台电脑完成无缝联机游玩。这是实际使用反馈，
  不是此分支重新执行的全场景回归；死亡、传送、Boss、重连及长时间运行仍须逐项验收。
- v1.8.6 客户端加入切换联机 ID 的自动申请、BBSC 发铃前的存档副本，以及无缝在线状态上报。
  门户的 `/v1/presence` 代码在仓库中，**尚未确认已部署到云端**；不部署也不影响现有游戏联机，
  但公开在线列表暂时不会显示无缝玩家。客户端对该上报失败只记日志，不阻断游戏。
- 传统在线的 `100.64.0.1` 与 BBSC 的 `100.64.0.2` 是两个独立的 Headscale 节点。
  不需要迁移 `.2`，也不应把两套 shadNet 数据库、私网身份或 Docker 卷合并。
- 此分支保存源代码和部署参考，**不含游戏文件、服务器账号数据、预授权密钥、玩家存档或已构建的
  安装 EXE**。云端实际的账号同步 watcher 和经服务器侧适配的 Compose 未纳入仓库；部署前须与
  `/opt/bbport-seamless` 的现有版本逐项核对，不能用仓库参考文件盲目覆盖。

## 连接拓扑

| 通道 | Windows 本机 | 私网目标 | 云端回环目标 |
| --- | --- | --- | --- |
| 传统 shadNet 游戏 / WebAPI | `127.0.0.2:31313,31315` | `100.64.0.1:31313,31315/tcp` | 传统容器 `31313,31315` |
| BBSC shadNet 游戏 / WebAPI | `127.0.0.3:31313,31315` | `100.64.0.2:31313,31315/tcp` | 隔离容器 `32313,32315` |
| BBSC STUN | 内置私网桥 | `100.64.0.2:31314/udp` | 隔离容器 `32314/udp` |
| BBSC 游戏 API | `127.0.0.3:32443` | `100.64.0.2:18080/tcp` | `127.0.0.1:32443` |
| BBSC 中继 | `127.0.0.3:32444` | `100.64.0.2:31316/tcp` | `127.0.0.1:32444` |
| 玩家间游戏数据 | 每台电脑独立的尾网身份和本地桥 | 对方 `100.64.0.x:31316/udp` | 不经过云端 TCP 游戏服务 |

内置网络实现见 [`tools/embedded_net`](../tools/embedded_net/README.md)。服务端桥仅从已认证的
尾网 STUN 来源构造 `BBST` 来源信息，使 shadNet 看见玩家实际的 `100.64.0.x:31316`，而非
Docker 网桥地址。TCP 健康检查或分配到私网 IP 都不能代替玩家间 UDP 与实际 JoinRoom 验收。
公网只开放 Headscale/门户所需的 HTTPS；不得把原始游戏端口直接暴露到互联网。

## 客户端的一键流程

1. 在“设置”选游戏目录、玩家数据目录并安装运行环境；“在线无缝联机”页输入独立联机 ID、密码。
   首次申请还需 QQ 号和昵称，资料保存在本机，密码与 QQ 号使用当前 Windows 用户的 DPAPI 保护。
2. 启动器用 `/v1/application` 检查已保存的申请。若用户改了联机 ID，旧凭据不再被误用于新 ID，
   而是为新 ID 调用 `/v1/apply`，等待审核（若有）后调用 `/v1/register`。服务端的 ID 唯一性和
   申请频率限制继续生效。相关代码在 [`BbportPortable.cs`](../tools/setup/BbportPortable.cs)。
3. 启动器用已注册账号调用 `/v1/device-key` 获取短时、一次性入网密钥，经标准输入交给内置私网
   进程；密钥不写入配置、命令行或日志。确认拿到私网 IP 后，检查 BBSC shadNet、游戏 API 与中继。
4. 仅此模式选 `127.0.0.3`，设置 `BBSC_LAB_MOD=1` 并调用经哈希核验的官方 BBSC 启动器与 DLL。
   [`scripts/run_game.py`](../scripts/run_game.py) 校验受支持的游戏映像和隔离端点后启动游戏；
   传统在线不加载 BBSC，不改用 `.2`。一键失败时保留页面内分步手动操作。
5. 游戏日志确认 shadNet 认证成功后，v1.8.6 启动器每 30 秒向公共门户发一次经申请凭据验证的
   `/v1/presence` 心跳，退出时发下线；服务端 90 秒未收到心跳会自动下线。公开列表仅列出已注册且
   同意公开的账号，并按联机 ID 对传统、无缝两种模式去重。实现见
   [`portal/server.py`](../deploy/shadnet/portal/server.py) 与
   [`nginx.conf`](../deploy/online_remote/nginx.conf)。这是客户端认证成功后的状态上报，
   **不是** BBSC 房间发现或可加入世界列表。

## BBSC 存档与铃铛

BBSC 发放专用铃铛前要先备份存档。游戏运行中直接复制实时 `userdata` 文件可能失败。
启动器原有的启动前、退出后 ZIP 自动备份仍保留；v1.8.6 另外在启动游戏前把当前
`savedata/1/<INSTALL_DIR_SAVEDATA>/SPRJ0005` 复制到临时目录，令 `BBSC_SAVE_DIR` 指向该副本。
游戏仍从原玩家数据目录读取和写入，临时副本在进程退出后清理。首次创建角色、启动前尚无存档时，
无法预制副本；请完成一次存档，退出后重新进入在线无缝模式，再检查专用铃铛和 BBSC 日志。
这项修复针对已知的备份失败途径，不能保证所有延迟发放都由同一原因造成。

## 服务端组成与部署规则

- 传统项目参考：[`deploy/online_remote/compose.yaml`](../deploy/online_remote/compose.yaml)，
  云端历史路径 `/opt/bbport-online/deploy/online_remote`。其 shadNet、Headscale、portal、proxy、
  数据卷和 `.1` 身份继续运行。
- BBSC 隔离项目参考：[`deploy/seamless_lab/compose.yaml`](../deploy/seamless_lab/compose.yaml)、
  [`compose.overlay.yaml`](../deploy/seamless_lab/compose.overlay.yaml)，云端现有部署历史路径
  `/opt/bbport-seamless/deploy`，含独立 shadNet、BBSC game-api/relay、私网桥及各自数据卷。
  云端实际 Compose 曾做适配，仓库文件只是参考，不是可直接覆盖的生产配置。
- 云端现有账号同步 watcher 负责将传统账号供 BBSC 节点使用；本仓库没有它的源文件或服务定义。
  **这是从零重建服务端的缺口**。在取得并审查该实现前，不宣称仅靠本分支可以完整重建现有云端。
- 若只启用 v1.8.6 的公共无缝玩家列表，需人工比对、合并门户 `server.py` 和 Nginx
  `/v1/presence` 放行配置；先备份现有文件，再只重建 `portal`、执行 `nginx -t` 并热重载代理。
  不重建传统 shadNet，也不更换 `.1`/`.2` 私网身份。此门户更新可延后，不影响游戏联机。
- 任何服务端维护前检查实际容器、卷、端口、Headscale 节点、watcher 状态；绝不运行
  `docker compose down -v`，不把密钥、token、数据库、玩家数据或 Docker 卷提交到 Git。

## 构建、验证与回滚

- Windows 运行环境的完整构建以 [`BuildPortable.ps1`](../tools/setup/BuildPortable.ps1) 为准，
  需要本机编译的 Windows 组件、内置私网程序以及官方 BBSC 测试包。远端安装包由
  [`BuildRemoteLauncher.ps1`](../tools/setup/BuildRemoteLauncher.ps1) 配置 `overlay_server=100.64.0.1`
  和 `seamless_overlay_server=100.64.0.2`；不得把两者设为同一节点，也不得把个人设备身份打包。
  `dist/` 安装包是本地产物，不在此分支中。
- 自动检查：`python -m unittest tests.test_run_game_online tests.test_seamless_lab_server`；
  `python -m unittest discover -s deploy/shadnet/portal -p test_server.py`；
  在 `tools/embedded_net` 执行 `go test ./...`。这些验证代码、门户策略与桥，不替代双机游戏测试。
- 双机验收：两端使用不同联机 ID 和不同私网身份，分别记录 `100.64.0.x`；先验证传统在线，
  再验证 BBSC 登录、铃铛、实际 JoinRoom、传送、死亡、Boss、重连、长期游玩及存档完整性。
  对齐两端 `logs/run-*.log`、`logs/net-traffic-*.log`、`runtime/bbsc/logs/bbsc-mod-*.log`。
- 回滚客户端时关闭游戏，保留旧运行目录与存档备份；回滚云端仅针对明确变更的 Compose 项目和
  已备份的文件/镜像，不删除 Headscale Node 2、账号卷或玩家存档。门户状态补丁可单独回滚，
  与已验证的游戏联机路径分离。

第三方 BBSC 许可、版本与校验信息见 [`packaging/BBSC_THIRD_PARTY.txt`](../packaging/BBSC_THIRD_PARTY.txt)。
