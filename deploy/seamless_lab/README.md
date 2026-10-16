# Isolated seamless-matchmaking laboratory

This is **not yet full seamless co-op**. The existing native client can request
shadNet's experimental relaxed summon rules, but it cannot yet keep a party
together through death, lamp travel or area loading. Those require verified
Bloodborne game-code hooks and save isolation. Do not distribute this as a
finished seamless mode.

Run this Compose project only on a test machine. It is independent of
`deploy/online_remote`: its own named data volume contains a separate account
database and registration secret. It binds only to loopback on 32313/TCP,
32314/UDP and 32315/TCP, leaving the production traditional service's
31313/31314/31315 ports untouched. Never copy the production volume here or
run `down -v` against production.

After Docker Desktop is running:

```powershell
docker compose -f deploy/seamless_lab/compose.yaml config --quiet
docker compose -f deploy/seamless_lab/compose.yaml up -d --build
curl.exe http://127.0.0.1:32315/status
```

In the launcher, use native LAN mode with server `127.0.0.1:32313`, turn off
"this PC is the LAN server", and enable the experimental seamless-matching
checkbox. The launcher derives the WebAPI port as TCP port + 2 and the client
derives the STUN port as TCP port + 1. Register a **new test account** on this
isolated server before attempting a bell test. On your own test machine, read
the generated registration secret with
`docker compose -f deploy/seamless_lab/compose.yaml exec -T shadnet cat /var/lib/shadnet/registration_secret`
and paste it into the launcher's LAN registration field; do not share it in
logs or screenshots. Production credentials are not
copied. The launcher redirects this mode's game data and automatic backups to
`<your player-data directory>/seamless-lab` without copying your existing
character. Start a new test character; never import your only save here.

For two PCs or Internet testing, first add a separate, authenticated tailnet
node and private port bridge for this lab. Do not expose these raw game ports
to the public Internet, and do not reconfigure the production shadNet service.
