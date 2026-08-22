# Multiplayer implementation

_Started 2026-08-10. This is the execution companion to
[`multiplayer-plan.md`](multiplayer-plan.md)._

## Target

Ship cross-play between Quest/PCVR and the native Zig flatscreen build first.
Both frontends must use the same Zig protocol-v6 live-session runtime. Python
flatscreen compatibility remains a gated target until mixed-runtime matches
pass the determinism matrix below.

The normal user path is relay rollback with a four-character room code. Direct
UDP lockstep remains an Advanced/LAN path. The 0.11.0 candidate supports
Survival and Rush for two to four players. Casual Quests remain slice 7 and are
not advertised for this release. Tutorial, Typo'Shooter and Hardcore are also
out of scope.

## Non-negotiable boundaries

- The C# frontend samples VR input and presents snapshots; it does not implement
  reliability, rollback, lobby state, resync or simulation.
- Network input is `PackedPlayerInput` (move X/Y, aim X/Y, action flags). Never
  reinterpret `CrimsonHostInput.flags`, whose bit layout is different.
- The native and XR frontends consume `net/network_live_runtime.zig` rather than
  maintaining separate lockstep/rollback selection logic.
- Network catch-up can advance multiple simulation frames. Audio and terrain FX
  must be queued and drained exactly once.
- A local pause overlay sends neutral input while continuing to pump the
  session. It cannot suspend the shared match.
- Perk choices are ordered network commands. Multiplayer is not deterministic
  enough to ship until those commands are transported and replayed.

## Slice status

| Slice | Deliverable | Status | Exit gate |
|---|---|---:|---|
| 1 | Reusable native live runtime and initial cross-wire fixtures | Implemented | Desktop window builds; shared runtime has config tests; Python and Zig decode the same resync/tick-command packets |
| 2 | Append-only network host ABI and headless loopback | Implemented | Two ABI handles expose lifecycle/status/presentation; twin handles and real UDP loopback match for 10,000 ticks on Windows and Linux |
| 3 | VR direct-LAN lobby and native PC/Quest Survival | Implemented; two-player PC/Quest join and 60 Hz pacing validated | Either side can host; Quest can occupy every non-zero slot |
| 4 | Slot-aware HUD, results and player FX; Rush | Implemented; headset cross-play validation pending | Two- to four-player visual/game-result matrix passes |
| 5 | Perk commands and network replay capture | Implemented; multi-device validation pending | Perk selection cannot diverge; replay includes every slot and command |
| 6 | Relay room flow, DNS and reconnect/resync | Implemented; operated endpoint and headset validation pending | Impairment and headset suspend/resume tests pass |
| 7 | Casual Quests and host-owned progression | Not started | Quest completion/unlocks change only the host profile |
| 8 | Python/native proof matrix | Not started | All cross-runtime gates in the design plan pass |

## Slice 1 implementation

`crimson-zig/src/net/network_live_runtime.zig` now owns the presentation-free
union of lockstep host, lockstep client and rollback sessions. Its public API is:

```text
LaunchConfig
NetworkLiveRuntime.init / initWithStatus
open / start / update / deinit
submitLocalInput / submitLocalFrameInput
runnerForLocalInput / runConfigForResults
localInputSlot / boundPort / rollbackRoomCode
```

`window_main.zig` adapts its menu-owned `NetworkLaunchRequest` to `LaunchConfig`
and uses the shared runtime. Lobby labels and endpoint formatting deliberately
remain in the raylib window because they are presentation concerns. The module
is exported as `crimson_zig.net.network_live_runtime`, which is the dependency
the host ABI will use in slice 2.

Initial cross-wire coverage is deliberately fixture based and process-free:

- Zig decodes Python-msgspec golden protocol packets in
  `net/lockstep_protocol.zig`.
- `tests/net/test_cross_wire_golden.py` decodes and byte-re-encodes those same
  resync and tick-frame/perk-command packets.
- Shared runtime tests cover equal host config propagation, consistent
  invalid-address handling across lockstep and rollback, and two independently
  initialized hosts advancing an identical canonical-input script to matching
  ticks, player state and creature counts.

This is an initial guard, not completion of the protocol proof gate. Every
protocol-v6 message, saved status and rollback snapshot still needs a golden
fixture before Python cross-play can be advertised.

## Slice 2 implementation

ABI v25 adds a separate opaque network handle table without changing
`CrimsonHostSession` behavior. `crimson_host.h` and `host_abi/exports.zig`
expose:

```text
crimson_host_net_create(config_json, config_len, out_handle)
crimson_host_net_update(handle, now_ms, local_input, out_update)
crimson_host_net_command(handle, command_type, player_index, value)
crimson_host_net_status(handle, buffer, inout_len)
crimson_host_net_snapshot(handle, buffer, inout_len)
crimson_host_net_audio_events(handle, buffer, inout_len)
crimson_host_net_terrain_fx(handle, buffer, inout_len)
crimson_host_net_destroy(handle)
```

Creation parses and owns all config strings, creates the shared runtime, opens
its transport and returns actionable validation errors. `update` pumps without
waiting for a peer, reports frames/ticks advanced and connection phase, and
submits VR input through `lockstep_input_adapter` only after a local runner is
ready. Status is size-then-fill UTF-8 JSON containing lobby counts, slots,
names/readiness, room/session identifiers, local slot, bound port and failures.

Snapshot, terrain-info, audio and terrain-FX encoding now accept a `LiveRunner`
or event batch, so single-player and network handles emit the identical ABI
payload. Catch-up stepping aggregates every frame's bounded audio/terrain
events; network drains clear only after a successful fill call. ABI v27 now
implements the ordered command entry points described under slice 5 below.

The headless gates verify invalid config and clean destruction while waiting,
drive two independent ABI network handles through 10,000 identical canonical
ticks and compare final snapshot bytes, and run a real host/client UDP loopback
with per-slot input. Zig's Windows threaded-I/O backend does not implement
timed datagram receives, so the transports use a bounded blocking receive
worker plus timed futex wakeups on Windows. Protocol decoding and simulation
remain on the caller thread, while Linux and Quest retain the direct timed-I/O
path. The same real-socket gate now runs on Windows and Linux CI.

## Slice 3 implementation

The Play Game menu now routes to a dedicated poke-only Multiplayer submenu,
keeping LAN diagnostics out of the normal mode list. It offers Host LAN and
Join LAN, a 2-4 player selector, a physical IPv4 keypad, fixed protocol-v6 port
31993, live connected/ready slot rows, an explicit Ready/Unready button and
Cancel. A match starts only after every connected slot, including the host,
has readied; joining no longer drops either player straight into gameplay.
VR exposes this as a poke button, while the native desktop lobby uses Enter to
toggle Ready/Unready. Host lobbies display a
best-effort local IPv4 address to share. The Quest export explicitly requests
Android's INTERNET permission.

`NetworkSimSession` is the managed adapter over the network ABI. It pumps with monotonic
milliseconds, decodes native lobby JSON, and uses the same snapshot, audio,
terrain-info and terrain-FX views as `SimSession`; C# contains no reliability
or lockstep logic. The lobby continues pumping while it owns the VR menu. Once
the native phase becomes `running`, Main hands the existing Survival diorama to
the network adapter. A local multiplayer pause continues pumping neutral input
instead of suspending the shared match.

The first slot-aware seam is included here so a joining Quest is controllable
in slots 1-3: movement origin, HUD, haptics, positional audio and border
proximity select the negotiated local player. Full per-player effects/results
remain slice 4. Perk presentation and replay recording were deliberately
suppressed through slice 4; slice 5 removes both gates canonically.

Managed contract tests pin the update layout and native snake-case
lobby JSON. Native Windows tests cover raw lockstep/relay UDP, the desktop live
runtime and a real 10,000-tick ABI host/client loopback. A physical Windows-PC
host/Quest-client match has validated joining, gameplay, clean peer-loss
reporting and 59.6 Hz progression over a 30-second sample. The remaining
slice-3 hardware gate is Quest in slots 2-3, Quest hosting, and repeated
cold-start coverage for the reported intermittent audio-only startup exit.

## Slice 4 implementation

The direct-LAN configure page now selects Survival or Rush for both host and
join. The negotiated mode is returned in lobby status and in each ABI update,
so the frontend uses the host-started mode rather than trusting stale local UI
state. A focused native network test boots Rush through the same host ABI used
by Quest, advances live input and verifies the Rush snapshot mode.
The host config also carries its quest-unlock and weapon-usage status, keeping
the shared drop pool host-owned as required by the protocol; joiners do not
substitute their local profile.

ABI v26 appends local-slot shots fired, shots hit, most-used weapon, shared
match kills and negotiated mode to `CrimsonHostNetUpdate`. These counters are
read directly from the canonical runner after the local slot is assigned.
`NetworkSimSession` now produces the same result fields as an offline session,
so Rush elapsed-time scoring, accuracy, weapon and Survival XP are no longer
zeroed or borrowed from player zero. Local scores are stored and ranked under
the negotiated 2-4 player count, and the result card identifies the local slot.

The snapshot renderer now applies Sharpshooter lasers, Radioactive auras and
shield rings to every player. Doctor targeting selects the negotiated local
player. Debug-forced effects remain local-only so a headset visualization
toggle cannot paint fake perks on remote players. Movement, HUD, border cues,
audio and haptics retain the slot selection introduced in slice 3.

Leaving or finishing a network room now destroys the network handle without
constructing an unrelated offline run behind the menu. Play Again returns to
the multiplayer chooser; Main Menu returns to the main menu, and an offline
session is created only when an offline mode is actually started.

Managed layout/JSON tests, focused native network ABI tests and a headless Rush
run cover the code gates. The remaining slice-4 gate is the physical 2-4 player
visual/results matrix on native PC and Quest, including Quest in slots 1-3.

## Slice 5 implementation

Perk menu-open and pick requests now use reliable protocol messages containing
the requesting player index. Direct-lockstep clients send requests to the host;
the host validates the address-to-slot mapping and attaches accepted commands
to a canonical tick frame. Relay rollback uses request/canonical messages:
guests request, the host schedules beyond the rollback window, and the relay
forwards the authoritative tick-stamped command to every peer. Python and Zig
schemas/runtimes implement the same messages.

The live bridge holds the shared perk pause while network traffic continues.
Transport-only frames during that pause do not become fake replay ticks; open
and consecutive pick commands are stamped onto the first simulation tick that
actually advances, matching offline replay semantics.

Network sessions support the normal replay begin/detach/encode path. Capture
uses authoritative frames for every player, replaces rollback-predicted rows
when corrected, records perk commands with their player indices, and emits the
ordinary verifiable `.crd` format. A native ABI gate records and verifies a
network run end-to-end.

## Slice 6 implementation

The VR Multiplayer page defaults to Host Room and Join Room. Joining uses a
poke-only four-character `[a-z0-9]` keypad; hosting selects Survival/Rush and
two-to-four players. Direct LAN remains under Advanced. Lobby status displays
the invite code, slots, readiness, reconnect/resync state and failures. Relay
rooms use the same explicit per-player Ready/Unready gate as direct LAN; room
creation and joining alone never start a match.

Relay address and port are release configuration
(`crimson/network/relay_host` and `relay_port`) with
`CRIMSON_RELAY_HOST`/`CRIMSON_RELAY_PORT` development overrides. No unowned
public hostname is compiled into source: an unconfigured build explains that
online rooms are unavailable and retains LAN. Native clients resolve IPv4 DNS
names before transport creation.

ABI v27 adds canonical commands and `crimson_host_net_resume`. Quest app resume
immediately re-enters token-based relay reconnect instead of waiting for the
silence timeout; native reconnect/resync state is reported as `reconnecting`.
Existing relay capacity, timeout, reconnect-token, rollback/resync and
impairment gates remain the server/runtime foundation.

Remaining slice-6 release work is operational and physical: provision the
friends-only relay, configure DNS/capacity monitoring, and run sleep/resume,
Wi-Fi roaming and impairment tests on real Quest/native-PC pairs.

## Post-release physical and operated matrix

The project owner accepted the current PCVR-to-Quest multiplayer behaviour for
0.11.0 on 2026-08-22. The two automated regressions discovered by the 2026-08-13 audit are closed:
reordered rollback input, repeated Windows Zig suites, lockstep smoke, and the
full impairment/resync matrix now pass without retry-dependent acceptance. The
remaining rows are retained as broader device, topology, operated-service, and
scope-proof hardening coverage rather than 0.11.0 blockers:

| ID | Remaining evidence | Release impact |
|---|---|---|
| MP-01 | Direct LAN with native PC hosting Quest and Quest hosting native PC | Blocks advertised bidirectional LAN |
| MP-02 | Two-, three-, and four-player physical matches with Quest as local slot 0, 1, 2, and 3 | Blocks the advertised 2-4 player/slot claim |
| MP-03 | Survival and Rush: movement/fire, every-player FX/HUD, canonical perk choices, player death, results, and verified all-slot replay | Blocks the advertised gameplay claim |
| MP-04 | Peer leave, disconnect, reconnect-token recovery, state resync, and useful build/protocol mismatch errors | Blocks recovery claims |
| MP-05 | Real-device delay, jitter, reorder, loss, and duplicate delivery while recording final convergence and no duplicate audio/terrain FX | Blocks impairment tolerance claims |
| MP-06 | Quest focus loss, sleep/resume, controller loss, and Wi-Fi transition during LAN and relay matches | Blocks headset lifecycle recovery |
| MP-07 | Quest CPU, memory, frame timing, thermal state, and catch-up load during a 30+ minute rollback match | Blocks performance sign-off |
| MP-08 | Provision the owned friends-only relay and release DNS; record region/provider, capacity, timeout, rate limits, room expiry, monitoring/alerts, logs/privacy, incident owner, and shutdown procedure | Blocks enabling room-code relay in release configuration |
| MP-09 | Two-, three-, and four-player operated-relay matches covering MP-03 through MP-07 | Blocks the normal room-code path |
| MP-10 | Casual Quest completion and host-owned unlock/progression | Not a 0.11.0 blocker because casual network Quests are explicitly out of scope |
| MP-11 | Every protocol-v6 golden packet plus Python-host/native-client, native-host/Python-client, and mixed relay parity | Not a 0.11.0 blocker while Python mixed rooms are explicitly unsupported |

Each run records candidate commit/build hashes, topology, player count, local
slot, mode, devices, impairment settings, final tick/state hash, replay result,
and logs. A single two-player LAN smoke does not close any broader row.

## Definition of done

The current 0.11.0 multiplayer scope is owner-accepted PCVR-to-Quest play. A
future broader multiplayer claim is complete only when native PC/VR cross-play
passes MP-01 through MP-09, network replays verify, the room-code flow is usable
without developer tools, and public relay operation has capacity/rate/expiry
protections. Python cross-play is a separate claim unlocked only by its
cross-runtime proof gates.
