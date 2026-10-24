# Windows 启动器：文件 Mod 与 Xbox 按钮

启动器的「Mod 管理」页管理玩家数据目录下的 `mods/` 和 `mods.json`。总体开关、每个 Mod 的勾选在下次启动时生效。底层 `scripts/mods.py` 在临时目录建立文件覆盖层，不覆盖游戏本体；同一文件被多个 Mod 修改时，靠后的 Mod 生效。排查联机、存档或画面异常时，先关闭全部 Mod，并让队友使用相同配置。

首个推荐 Mod 是 [Dommo 的 Xbox (and switch) UI for Bloodborne](https://www.nexusmods.com/bloodborne/mods/30?tab=files)。选择一个 **Modern Xbox prompts** 主文件；启动器支持导入 ZIP，或从 7z/RAR 手动解压得到的单个 `common.tpf.dcx`。不要导入包含多个版本的整合包。导入后默认关闭，勾选才启用。作者页面说明该 Mod 替换 `dvdroot_ps4/menu/common.tpf.dcx`，并允许注明作者后转载；服务器、启动器和网站须署名 Dommo、链接原页面，不得售卖 Mod。

服务器下载需要先合法取得并核对单版本 ZIP。当前仓库**不包含**该文件，也没有可验证的 Nexus 直链；Nexus 的普通下载经网页登录，不能把网页 URL 伪装成直链。未提供文件和 SHA-256 时，启动器明确显示「服务器文件待发布」，仍可通过官方页面下载再本地导入。

准备在现有服务器发布时：

1. 从作者页面下载单个 Modern Xbox prompts 主文件，并确认 ZIP 内恰有一个 `menu/common.tpf.dcx`。
2. 在服务器现有 `deploy/online_remote/windows-site/mods/` 内存放为 `xbox-prompts.zip`。该目录已经由 `windows-site` 容器只读挂载，通过 `/windows/mods/xbox-prompts.zip` 公开；**不需要重启传统 shadNet、无缝 shadNet 或 BBSC**。请不要把 ZIP 提交进 Git。
3. 确认 `https://82.157.168.20/windows/mods/xbox-prompts.zip` 返回 ZIP，记录 SHA-256；不要只以 HTTP 200 判定内容正确。
4. 构建新的远端启动器时，向 `tools/setup/BuildRemoteLauncher.ps1` 传 `-XboxModZip <本地相同 ZIP 的路径>`。构建脚本将服务器路径和该 ZIP 的 SHA-256 写入启动器的 `ONLINE_SERVICE.txt`。用户下载时会校验同源 HTTPS 地址、文件上限与 SHA-256，校验失败不安装。

若服务器已发布的 ZIP 与构建时输入的 ZIP 不同，启动器会拒绝安装。更新 Mod 时先更换服务器文件，再用新的校验值构建并发布启动器。旧版本可继续从 Nexus 导入；已有 Mod 不会被覆盖。

联机启动时不再出现仅基于 `CUSA03173` 标记的提示框；底层 eboot 映像完整性校验依然有效。局域网联机的兼容性提示保留。
