# Windows 玩家论坛

论坛独立于游戏账号、Headscale 和两套 shadNet。公开阅读；论坛昵称和密码只用于发帖与回复。新账号的第一条内容待审，通过后该账号后续内容直接公开。

- 公开入口：`https://82.157.168.20/windows/forum/`
- API：`/windows/api/`，仅经同源 HTTPS 代理，不向公网映射容器端口。
- 图片：每条最多两张 PNG/JPEG/WebP，原图每张不超过 3 MB；解码后统一编码为 WebP，单个账号最多占用 50 MB 图片空间。
- 存储：Compose 命名卷 `forum-data` 内的 SQLite 数据库。备份时保存整个卷（含 `forum.db`、`forum.db-wal`、`forum.db-shm`），或用 SQLite 在线备份命令；不要只复制正在运行的主数据库文件。

部署前先检查服务器现有 `compose.yaml`、`nginx.conf` 和 `windows-site/index.html` 是否有本地改动，保留备份，再只启动 `forum` 服务。验证健康后将 `/windows/api/` 路由加入代理并执行 `nginx -t`、`nginx -s reload`；不要用无服务参数的 `docker compose up -d` 重建联机服务。

管理员应先在网页上注册自己的论坛账号。确认昵称后，在服务器的 `deploy/online_remote` 目录执行：

```sh
docker compose exec forum python /app/server.py promote '已注册的管理员昵称'
```

这一步只需昵称，不需要、也不应该索取玩家密码。完成后刷新论坛页面即可出现“审核内容”。首帖审核在管理员设立前不会自动放行。
