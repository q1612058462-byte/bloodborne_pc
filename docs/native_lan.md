# Native LAN networking

bbport keeps its original disconnected network service behavior by default.
Set `BB_NETWORK_MODE=lan` to opt in to the native transport while it is under
development.

## Implemented transport foundation

- PS4-style IPv4 TCP and UDP sockets backed by the host operating system.
- Bind, connect, listen, accept, send/receive, socket options and DNS lookup.
- `sceNetEpoll*` readiness events (`WSAPoll` on Windows, `select` elsewhere).
- Online `sceNetCtl` state for the opted-in process.
- A configurable local NP identity (`BB_LAN_NPID`, default `Hunter01`) and
  signed-in state, including the state callback.
- Matching2 initialization, context start/stop callbacks and the single LAN
  world needed before room matchmaking begins.
- P2P UDP framing compatible with Wozzardman/shadp2p commit `6e97b404`:
  two network-order virtual ports followed by the guest payload.
- `BB_P2P_PORT=<port>` can pin the host UDP port; otherwise the operating
  system chooses a free port.

`build.sh --test` performs real loopback UDP tests for normal datagrams,
readiness notification, and P2P virtual-port framing. A sandbox that blocks
loopback networking must run that test outside the sandbox.

## Not implemented yet

This layer alone does not make Bloodborne's bells or messages functional.
The game still needs NP Matching2 room discovery/join callbacks and
signaling/peer routing. Those layers will be added on top of this transport
rather than embedding or launching the full shadPS4 emulator.
