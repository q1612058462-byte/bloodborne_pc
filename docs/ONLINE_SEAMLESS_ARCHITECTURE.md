# Online seamless co-op: integration boundary (2026-10-16)

## Current truth

The production server at `82.157.168.20` serves **traditional** co-op with
`SHADNET_SEAMLESS=false`. Its accounts, portal, Headscale identity, bridge and
named volumes must remain available throughout seamless development. The
native client currently has an experimental `SHADPS4_BLOODBORNE_SEAMLESS_COOP`
switch, but source inspection found only the summon HTTP route/placement
header. It has no game-side death, travel, remote-character re-insertion or
progress-sync hooks. The server's `BloodborneSeamlessCoop` setting alone cannot
provide a persistent session. Do not relabel it as complete seamless play.

## Reference projects and licensing

- visy's native Windows Party implementation uses its **own serverless party
  protocol** and separate party saves. It is architecturally different from
  our shadNet service, and its current repository is GPL-3.0-or-later. Treat
  it as a behavioral/design reference; do not paste its code into this
  GPL-2.0 codebase without a deliberate license migration.
- BBSC uses the shadp2p/shadNet protocol family and adds Bloodborne-specific
  hooks plus a game-server relay. Its source repository declares
  GPL-2.0-or-later, but the Nexus download page separately restricts asset
  redistribution and modification. Refer to the public source and obtain
  clear distribution terms before bundling any BBSC assets or binaries.
- Both references are version-sensitive. Target only the verified
  CUSA03173 v1.09 executable; reject unknown code bytes before patching.

## Safe milestones

1. **Isolation:** keep traditional production on 31313/31314/31315 and its
   current volumes. Use `deploy/seamless_lab` with a separate shadNet process,
   database and loopback 32313/32314/32315 ports. The launcher now derives
   WebAPI as base port + 2 and keeps test saves under `seamless-lab/` with
   automatic backups. This milestone is only relaxed matchmaking.
2. **Game-side prototype:** gated, byte-validated hooks for summon eligibility,
   persistent room/peer signaling, death and stage-transition classification.
   Genuine disconnect/kick paths must still terminate the session.
3. **World re-entry:** on lamp/death/area change, coordinate destination,
   reload both worlds and reinsert remote characters; verify host and guest
   both see each other and can fight. No save mutation yet.
4. **Save-safe progression:** separate test saves and automatic backups before
   changing boss, lamp, shortcut or item state. Add idempotent sync and
   rollback/recovery tests, then real two-PC tests over Headscale.
5. **Online beta:** only after local and two-PC tests, give the seamless service
   its own tailnet node, enrollment path and client mode. Production remains
   traditional and its public 80/443 portal stays unchanged.

## Release gates

- Traditional bell pairing and status still pass after every client change.
- Seamless clients cannot accidentally connect to the traditional service.
- A version/hash mismatch never patches or writes a save.
- Two real PCs confirm summon, boss, death, lamp travel, Hunter's Dream,
  reconnect and return to single-player; both logs and save backups reviewed.

References: https://github.com/visy/bloodborne_pc_windows_port,
https://github.com/Classmg22/BBSC,
https://www.nexusmods.com/bloodborne/mods/599 .
