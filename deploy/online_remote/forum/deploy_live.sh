#!/bin/sh
# Deploy only the Windows forum. Run as root after reviewing the staged files.
set -eu

stage="${1:?staging directory required}"
live=/opt/bbport-online/deploy/online_remote
backup="$live/forum-backup-$(date +%Y%m%d-%H%M%S)"
test -f "$stage/backend/server.py"
test -f "$stage/compose.yaml"
test -f "$stage/nginx.conf"
test -f "$stage/index.html"
test -d "$stage/frontend"
mkdir -p "$backup" "$live/forum" "$live/windows-site/forum"
cp -p "$live/compose.yaml" "$backup/compose.yaml"
cp -p "$live/nginx.conf" "$backup/nginx.conf"
cp -p "$live/windows-site/index.html" "$backup/index.html"

rollback() {
    if [ "${deployed:-0}" != 1 ]; then
        cp "$backup/compose.yaml" "$live/compose.yaml"
        cp "$backup/nginx.conf" "$live/nginx.conf"
        cp "$backup/index.html" "$live/windows-site/index.html"
        docker exec bbport-online-remote-proxy-1 nginx -t >/dev/null 2>&1 &&
            docker exec bbport-online-remote-proxy-1 nginx -s reload >/dev/null 2>&1 || true
        printf '%s\n' 'Forum routes rolled back; check service logs.' >&2
    fi
}
trap rollback EXIT

cp "$stage/backend/server.py" "$stage/backend/Dockerfile" \
    "$stage/backend/.dockerignore" "$stage/backend/test_server.py" "$live/forum/"
cp "$stage/compose.yaml" "$live/compose.yaml"
cd "$live"
docker compose config --quiet
docker compose up -d --no-deps --no-build forum

ready=0
attempt=0
while [ "$attempt" -lt 20 ]; do
    if [ "$(docker inspect -f '{{.State.Health.Status}}' bbport-online-remote-forum-1)" = healthy ]; then
        ready=1
        break
    fi
    attempt=$((attempt + 1))
    sleep 2
done
test "$ready" = 1

cp "$stage/frontend/index.html" "$stage/frontend/forum.css" \
    "$stage/frontend/forum.js" "$live/windows-site/forum/"
cp "$stage/index.html" "$live/windows-site/index.html"
cp "$stage/nginx.conf" "$live/nginx.conf"
docker exec bbport-online-remote-proxy-1 nginx -t
docker exec bbport-online-remote-proxy-1 nginx -s reload

check_url() {
    label="$1"
    url="$2"
    attempt=0
    while [ "$attempt" -lt 5 ]; do
        if curl --noproxy '*' -fsS --max-time 10 \
            --resolve 82.157.168.20:443:127.0.0.1 "$url" >/dev/null 2>&1; then
            printf '%s: OK\n' "$label"
            return 0
        fi
        attempt=$((attempt + 1))
        sleep 1
    done
    printf '%s: FAILED\n' "$label" >&2
    return 1
}
check_url forum-api https://82.157.168.20/windows/api/healthz
check_url forum-page https://82.157.168.20/windows/forum/
check_url online-status https://82.157.168.20/v1/status
deployed=1
printf 'Forum deployed. Previous configs: %s\n' "$backup"
