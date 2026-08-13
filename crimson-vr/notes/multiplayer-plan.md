# CrimsonVR multiplayer and flatscreen cross-play plan

_Investigated 2026-08-10._

Implementation status and slice-level acceptance criteria are tracked in
[`multiplayer-implementation.md`](multiplayer-implementation.md).

## Outcome

Cross-play is architecturally viable. VR controls already become the same
simulation-neutral input carried by the flatscreen protocol: movement X/Y, aim
X/Y and packed action flags. The wire never carries mouse, controller, hand or
headset concepts. All peers simulate the same 1-4 player world and the existing
VR snapshot renderer already draws every player.

The implementation should expose the existing Zig live-network session through
`crimson_host`; it should **not** reproduce the networking stack in C#. This
keeps VR and the native flatscreen build on protocol v6, the same reliable UDP,
room/lobby state, rollback, reconnect/resync logic and deterministic runtime.

There are two different compatibility promises:

1. **Native flatscreen (`crimson-zig-window`) ↔ VR:** same Zig simulation and
   networking code. This is the first shippable target.
2. **Python flatscreen (`uv run crimson`) ↔ VR:** the Python and Zig schemas are
   intentionally mirrored and a Python client already completes a two-peer room
   flow through the Zig relay, but a complete native-client/Python-client match
   and tick-by-tick cross-runtime determinism have not been proven. Do not claim
   this target until the gates below pass.

## What already exists

- Protocol version 6, MessagePack tagged messages, build compatibility checks.
- Maximum four players, 60Hz input, one tick of input delay by default.
- Direct IPv4 UDP lockstep on port 31993.
- Relay-backed rooms with four-character codes, rollback (eight ticks), ping,
  reconnect tokens, state resync and lockstep fallback payloads.
- Host-owned seed, mode, player count, quest level, native-bug policy and saved
  unlock/weapon-use status. Clients construct the identical live session from
  the start message.
- Network modes exposed by flatscreen: Survival, Rush and Quests.
- A presentation-independent five-field `PackedPlayerInput`. VR's two-hand
  mapping already produces the corresponding native `GameInput` values.
- Zig live-session bridges that own a `LiveRunner` and advance it from canonical
  frames for lockstep host/client and rollback peers.
- The host ABI tick call already accepts multiple player inputs, and snapshots
  already contain all player records. The current C# wrapper is the component
  that artificially narrows it to one.
- Python↔Zig relay handshake, room, ready/start, input-forwarding and resync-wire
  interop tests. Native rollback impairment tests cover delay, reorder, drop,
  jitter, reconnect and resync scenarios.

## Required architecture

### 1. Make the native live-session wrapper reusable

Move the private `NetworkLiveRuntime` union in `window_main.zig` into a library
module, for example `net/network_live_runtime.zig`. It should wrap:

- `lockstep_live_session.HostLiveSession`;
- `lockstep_live_session.ClientLiveSession`;
- `rollback_live_session.LiveSession`.

Both the flatscreen window and host ABI must consume this one wrapper. Do not
fork its role/config, update, local-input or runner-selection logic.

### 2. Add an append-only network host ABI

Add a separate opaque network-session handle instead of changing existing
single-player handle semantics. Suggested ABI surface:

```text
crimson_host_net_create(config_json, ..., *handle)
crimson_host_net_update(handle, now_ms, *local_input, *result)
crimson_host_net_command(handle, command_type, player_index, value)
crimson_host_net_status(handle, buffer, *len)
crimson_host_net_snapshot(handle, buffer, *len)
crimson_host_net_audio_events(handle, buffer, *len)
crimson_host_net_terrain_fx(handle, buffer, *len)
crimson_host_net_destroy(handle)
```

`config_json` carries role, netcode, mode, requested player count, quest key,
relay/direct address, port, room code, peer name, build id and host status. The
status payload must expose at least:

- connecting / lobby / running / reconnecting / failed;
- room code and session id;
- local and host slot indices;
- expected, connected and ready player counts plus slot names;
- negotiated mode, quest and player count;
- ping/last-packet age, rollback/resync counters and actionable error text.

The ABI converts `CrimsonHostInput` to native `GameInput`, then uses
`lockstep_input_adapter.packGameInput`. Host ABI flags and network packed flags
use different bit positions and must never be reinterpreted directly.

Snapshot/audio/terrain encoders should be refactored to accept a `LiveRunner`
so single-player and network handles share the exact presentation contract.
Network catch-up may advance several frames in one update; queue emitted audio
and terrain events so catch-up or rollback does not silently lose or replay FX.

### 3. Add the C# session adapter

Add `NetworkSimSession` with the same capture operations used by `SimSession`.
At each physics frame it:

1. samples the local VR input;
2. calls the non-blocking native network update with monotonic milliseconds;
3. advances/presents only when canonical frames were produced;
4. captures the final snapshot and drains queued audio/terrain events.

The network pump must continue while a local menu is open. Multiplayer Pause is
a local overlay and sends neutral input; it cannot freeze one peer's clock. A
true global pause would need a new synchronized host command.

### 4. Make the VR frontend slot-aware

- Replace every local-player `[0]` assumption with the negotiated local slot
  for movement origin, HUD, border proximity, haptics and results.
- Continue rendering every player; that path already loops over snapshots.
- Render per-player auras/laser effects rather than only player zero where the
  base runtime exposes per-player state.
- Record high scores with negotiated player count. The host's progression/status
  determines the match; only the host should advance quest unlock progression.
- Leaving a room destroys the network handle and returns to the lobby without
  restarting an unrelated single-player session.

### 5. Carry deterministic gameplay commands

Perk open/pick events are canonical network commands, not ordinary input bits.
The protocol types exist, but the Zig window live-session path currently emits
empty command lists. Wire host-authoritative perk commands through both native
and Python sessions and include the requesting player index. Until this works,
a multiplayer run can diverge at the first perk choice.

Tutorial and Typo are not initial network modes. Rollback resync snapshots only
support Survival, Rush and Quests, matching the current flatscreen network UI.
Hardcore is also absent from protocol v6; either keep network Quests casual or
add it in a versioned protocol change after the base three modes work.

## VR menu and user flow

Add `Play Game → Multiplayer` as a submenu, not more buttons on the main page.

Recommended default flow:

- **Host Room** → mode → players (2-4) → quest when applicable → Create.
- **Join Room** → poke a four-character code → Join.
- Lobby shows code, player slots/readiness and Cancel. Host starts automatically
  when the requested slots are connected and ready, matching current behavior.

Put **Direct LAN** under an Advanced page. It requires host/join, IPv4 address,
port and lockstep mode; it is useful for development and same-Wi-Fi play but is
not a friendly internet flow because the host may need port forwarding.

Relay rooms are the practical public experience, but they require an operated
relay endpoint. The relay contains no game assets and can be deployed separately
from binary distribution. Native clients now resolve IPv4 DNS names before
transport creation; IPv6 remains unsupported. Before public use, configure an
owned hostname plus capacity monitoring, rate limits, expiry and incident
ownership rather than compiling an unowned endpoint into clients.

The current four-character `[a-z0-9]` room code is a convenience locator, not
authentication, and protocol traffic is not encrypted. Initial public rooms
should be described as friends-only. An operated relay also needs expiry,
rate/size limits, abuse metrics and capacity protection before its address is
compiled into released clients.

Quest must include Android internet/network permission explicitly and be tested
across headset sleep/focus loss, Wi-Fi roaming and app resume. No headset launch
should occur as part of ordinary deployment; use the existing push-without-launch
policy for test builds.

## Cross-play proof gates

These are release gates, not optional polish:

The cross-project go/no-go decision and evidence record live in
[Release Preparation](../../docs/contributor/project-tracking/release-preparation.md).
The 2026-08-13 audit remains a no-go until the reordered-input regression and
native networking flakes are fixed as well as the proof gates below.

1. Golden MessagePack packets encoded in Python decode in Zig and vice versa for
   every protocol-v6 message, including status and rollback resync snapshots.
2. Python host ↔ native client direct-lockstep lobby and live match.
3. Native host ↔ Python client direct-lockstep lobby and live match.
4. Python and native clients in the same relay rollback room, including forced
   packet delay/reorder/drop, reconnect and resync.
5. For each pairing, run at least 10,000 canonical ticks in Survival and Rush
   and a complete Quest. Compare per-tick deterministic state hashes, RNG state,
   player/creature/projectile pools and final statistics—not only protocol
   messages or final score.
6. Native PC flatscreen ↔ Quest VR over LAN and relay, with each side hosting.
7. Two-, three- and four-player slot ordering; a VR client in every non-zero
   slot; death/results, perks and quest completion.
8. Version/build mismatch gives a clear lobby error. Exact hashes must match
   when both builds advertise hashes; public version-only packages need a
   deliberate shared simulation compatibility id.
9. Replay capture of a network match either records all canonical player inputs
   and commands and verifies, or is visibly disabled. Never produce a plausible
   single-player replay from only the local slot.
10. Profile Quest CPU and memory under eight-tick rollback, catch-up and resync.
    The current native rollback live session retains 64 by-value `LiveRunner`
    snapshots; measure it on arm64 and reduce/compact that history if necessary.

## Recommended implementation slices

1. **Implemented:** extract shared native runtime and add initial
   deterministic/cross-wire tests.
2. Add host ABI network handle and a headless native ABI loopback test.
3. Add the VR lobby plus direct LAN **native-PC ↔ Quest** Survival slice.
4. Make HUD/results/player FX slot-aware; add Rush.
5. Wire perk commands and multiplayer replay capture.
6. Add relay room-code flow, DNS, reconnect/resync and operated relay config.
7. Add casual Quests and host-only progression.
8. Pass Python↔native determinism gates, then advertise full flatscreen cross-play.

## Decision

Build relay rollback as the normal user path and retain direct lockstep as an
Advanced/LAN diagnostic path. Ship native-flatscreen cross-play first. Treat
Python-flatscreen compatibility as intended and likely, but unproven until the
cross-runtime gates pass.
