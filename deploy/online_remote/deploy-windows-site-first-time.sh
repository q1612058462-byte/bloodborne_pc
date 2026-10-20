#!/usr/bin/env bash
# One-time, additive deployment of the independent /windows/ information site.
# Run as root with the path to a staged directory containing compose.yaml,
# nginx.conf and windows-site/{index.html,styles.css,app.js,hero.png}.
set -Eeuo pipefail

root=/opt/bbport-online/deploy/online_remote
stage=${1:-}
proxy=bbport-online-remote-proxy-1
site=bbport-online-remote-windows-site-1

if [[ $(id -u) -ne 0 || ! -d "$stage/windows-site" ]]; then
  echo 'Run as root with a valid staging directory.' >&2
  exit 2
fi

# The server has local adaptations. Refuse to replace a configuration that
# differs from the exact pre-site version we reviewed.
printf '%s  %s\n' \
  635ad77b67f6fe9474010e221f47057147b7ba671c5c4e445513f191697ad0ae "$root/nginx.conf" \
  d106df4b6b597576cb921c004810fd43e9fe30761a1cc21acb29afcd302bf4ae "$root/compose.yaml" \
  | sha256sum --check --status
if [[ -e "$root/windows-site" ]]; then
  # A prior attempt may have copied files before a later proxy check failed.
  for file in index.html styles.css app.js hero.png; do
    cmp -s "$stage/windows-site/$file" "$root/windows-site/$file"
  done
fi
[[ -z $(docker ps -aq --filter "name=^/${site}$") ]]
docker inspect "$proxy" >/dev/null

backup=$(mktemp -d "$root/.windows-site-backup.XXXXXX")
cp -p "$root/nginx.conf" "$backup/nginx.conf"
cp -p "$root/compose.yaml" "$backup/compose.yaml"
started=0
rollback_started=0
rollback() {
  local code=$?
  if [[ $rollback_started -eq 1 ]]; then exit "$code"; fi
  rollback_started=1
  set +eE
  trap - ERR
  cp -p "$backup/nginx.conf" "$root/nginx.conf"
  cp -p "$backup/compose.yaml" "$root/compose.yaml"
  docker exec "$proxy" nginx -t && docker exec "$proxy" nginx -s reload || true
  if [[ $started -eq 1 ]]; then docker rm -f "$site" >/dev/null || true; fi
  echo "Deployment failed; proxy and Compose configs restored from $backup" >&2
  exit "$code"
}
trap rollback ERR

install -d -m 0755 "$root/windows-site"
for file in index.html styles.css app.js hero.png; do
  install -m 0644 "$stage/windows-site/$file" "$root/windows-site/$file"
done

# cp overwrites in place so the existing bind mount keeps pointing at this file.
cp "$stage/compose.yaml" "$root/compose.yaml"
docker compose -f "$root/compose.yaml" config --quiet
docker compose -f "$root/compose.yaml" up -d --no-deps windows-site
started=1

for attempt in {1..20}; do
  health=$(docker inspect --format '{{.State.Health.Status}}' "$site")
  if [[ "$health" == healthy ]]; then break; fi
  if [[ "$health" == unhealthy ]]; then false; fi
  sleep 2
done
[[ "$health" == healthy ]]
docker exec "$proxy" wget -q -O /dev/null http://windows-site/

cp "$stage/nginx.conf" "$root/nginx.conf"
docker exec "$proxy" nginx -t
docker exec "$proxy" nginx -s reload

site_http=000
for attempt in {1..10}; do
  site_http=$(curl -ksS --max-time 10 -o /dev/null -w '%{http_code}' https://127.0.0.1/windows/)
  if [[ "$site_http" == 200 ]]; then break; fi
  sleep 1
done
if [[ "$site_http" != 200 ]]; then
  echo "Site route returned HTTP $site_http after proxy reload" >&2
  docker exec "$proxy" sh -c 'grep -A5 -B2 "location = /windows" /etc/nginx/conf.d/default.conf' >&2 || true
fi
[[ "$site_http" == 200 ]]
curl -kfsS --max-time 10 https://127.0.0.1/v1/status | grep -q '"ok":true'
trap - ERR
echo "Windows site live. Backup retained at $backup"
