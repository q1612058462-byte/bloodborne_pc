# Windows bbport 上游优化对照（2026-10-24）

此文只记录本分支与公开 Windows bbport 分支的差异和移植边界。现有传统在线联机、BBSC 无缝联机、玩家数据目录与存档布局均不改动。上游实现来自 [Supermedo Windows 分支](https://github.com/Supermedo/bloodborne_pc/tree/windows)、[visy Windows 分支](https://github.com/visy/bloodborne_pc_windows_port) 和 [Awakenchan/LanternEngine](https://github.com/Awakenchan/LanternEngine)；不要直接覆盖本分支的 `src/`、`gpu/` 或启动器。2026-10-24 查到 Supermedo 最新发布版是 1.9.4：其 1.9.3 显存修复与 1.9.4 联机、笔记本性能配置改动均需按本分支单独评估。

## 本轮已移植

| 方向 | 本分支变化 | 验证边界 |
| --- | --- | --- |
| 显存预算 | 独显 <=4 GiB 不再额外扣 1 GiB 启动预算；大 NVIDIA 卡只留最多 512 MiB。纹理缓存按驱动实时预算设定回收阈值；<=4.5 GiB 独显使用 70/90/95%，其他设备 70/85/95%。`BB_GC_BUDGET_MB` 只能收紧实时预算。 | 阈值单元测试；仍需 4、6、8、16 GiB 实卡测试。 |
| 纹理回收 | 常态等待 20 秒闲置，压力时等待 5 秒，临界时等待 1 秒；接近驱动预算 98% 时才尝试最近闲置纹理。每轮至多 1 次同步写回，减小连续帧停顿。 | 编译与单元测试；实际场景切换、长时间游玩待测。 |
| 呈现帧分配 | `WITHIN_BUDGET` 拒绝时先尝试常规分配，再尝试主机内存优先；全部失败才报错。 | 编译通过；无法在开发机可靠模拟驱动显存耗尽。 |
| 诊断 | 启动时打印专用显存与预算，运行中每分钟打印使用量、预算与回收阈值。 | 日志格式可检查，数值由实际 Vulkan 驱动提供。 |
| 键盘奔跑 | `W` 到 `W+A` 等方向切换时逐帧转向，并沿方形摇杆门限维持全幅；步行和实体手柄不走此平滑路径。 | 输入回归测试通过，仍需用户在游戏里验证奔跑、冲刺攻击和急转。 |

## 暂缓或需单独项目

- 上游的帧生成、HDR 和新的游戏内 UI 横跨呈现器、着色器、授权资产与启动器，不能作为显存修复直接合并；先保留现有升频与画质设置。
- 上游的完整 `input.ini`、可配置反向键策略仍需独立迁移与实机按键矩阵测试；本轮只合入确定范围的奔跑转向策略。[上游输入说明](https://github.com/Supermedo/bloodborne_pc/blob/windows/docs/INPUT.md)。
- 上游 1.9.3 还宣称释放闲置 staging 内存、按图像大小分配复制缓冲和低显存自动画质配置。这些改变跨 `vk_stream_buffer`、纹理上传和启动器配置，需先分别度量显存峰值与画质，避免未经验证地降低原有画质或破坏游戏资源加载。[发布记录](https://github.com/Supermedo/bloodborne_pc/releases/tag/windows-v1.9.3)。
- 可跨显卡分享的 Shader 源缓存、自动 Shader 构建器与当前缓存布局不兼容；在迁移格式和校验前不能直接导入别的分支缓存。[上游缓存说明](https://github.com/visy/bloodborne_pc_windows_port/blob/master/docs/SHADERS.md)。
- 上游自身也有 4 GiB 显卡在地图切换时显存耗尽的报告；预算策略是降低风险，不是保证不崩溃。[问题记录](https://github.com/deadinside28/bloodborne_pc/issues/163)。

## 建议验证

1. 4 GiB 与 6–8 GiB 显卡各以 1080p/30 FPS、中画质跑捏脸、亚楠地图往返和死亡重生至少 30 分钟；记录每分钟 `GPU memory:` 行及 `Texture cache: memory pressure`。
2. 高显存显卡另测 1440p 和较高画质，确认显存增长趋于稳定，而非持续增加；若出现 OOM，保留完整运行日志及 GPU 型号。
3. 对照现版 1.8.7：平均/最低帧率、同步写回次数、区域切换耗时、首个 STOP/Assertion。无数据前不宣称性能提升百分比。
