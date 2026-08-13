"""M2 verify gate: record a scripted VR-style survival input sequence to a .crd
that passes `crimson-zig replay verify`.

The VR input path splits into two halves, each independently covered:

  reticle poses -> CrimsonHostInput   (crimson-vr/godot/src/VrInput.cs)
      covered by the C# unit tests in crimson-vr/tests/CrimsonVR.Tests

  CrimsonHostInput -> verifiable .crd  (this harness)
      a fixed synthetic CrimsonHostInput sequence, recorded and re-simulated by
      the deterministic runtime we embed as libcrimson.

The sequence emitted here is exactly the CrimsonHostInput schema the frontend
produces: `move` is the NORMALIZED move-hand direction (host ABI move_x/move_y is
a direction vector, not a target point -- see VrInput.cs), `aim` is the aim-hand
reticle point, and the trigger drives the fire bits, with move_mode
MOUSE_POINT_CLICK + aim_scheme MOUSE. Because move is a direction, the script is
open-loop (no live player position needed).

The claimed stats are filled by walking the replay through the Python verify
playback driver; `crimson-zig replay verify` then re-simulates on the Zig runtime
and confirms it matches -- proving the frontend's input schema yields replays the
embedded runtime accepts.

Usage:
    uv run crimson-vr/tools/gen_vr_verify_replay.py [out.crd] [ticks]
"""

from __future__ import annotations

import math
import sys
from pathlib import Path

import msgspec

from crimson.aim_schemes import AimScheme
from crimson.game_modes import GameMode
from crimson.movement_controls import MovementControlType
from crimson.replay import ReplayHeader, ReplayRecorder, dump_replay
from crimson.replay.driver.playback_driver import build_verify_playback_driver
from crimson.replay.types import ReplayClaimedStatsSnapshot
from crimson.sim.input import PlayerInput
from grim.geom import Vec2

WORLD_CENTER = 512.0
AIM_ORBIT_RADIUS = 300.0


def vr_input_for_tick(tick: int) -> PlayerInput:
    """Build the CrimsonHostInput-equivalent the VR frontend would emit this
    tick, as a PlayerInput (1:1 field map). Move-hand wanders (unit direction
    that slowly rotates); aim-hand orbits the arena center and fires."""
    move_angle = float(tick) * 0.03
    # Unit move direction -- what VrInput.Build emits for move_x/move_y.
    move = Vec2(math.cos(move_angle), math.sin(move_angle))

    aim_angle = float(tick) * 0.05
    aim = Vec2(
        WORLD_CENTER + math.cos(aim_angle) * AIM_ORBIT_RADIUS,
        WORLD_CENTER + math.sin(aim_angle) * AIM_ORBIT_RADIUS,
    )

    return PlayerInput(
        move=move,
        aim=aim,
        move_mode=MovementControlType.MOUSE_POINT_CLICK,
        aim_scheme=AimScheme.MOUSE,
        fire_down=True,
        fire_pressed=tick % 30 == 0,
        move_to_cursor_pressed=True,
    )


def main() -> None:
    out_path = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("artifacts/tests/vr_survival.crd")
    ticks = int(sys.argv[2]) if len(sys.argv) > 2 else 3000

    header = ReplayHeader(
        game_mode_id=GameMode.SURVIVAL,
        seed=0x00C0FFEE,
        tick_rate=60,
        player_count=1,
    )
    recorder = ReplayRecorder(header)
    for tick in range(ticks):
        recorder.record_tick([vr_input_for_tick(tick)])
    replay = recorder.finish()

    # Re-simulate to fill claimed stats so `replay verify` has something to check.
    driver = build_verify_playback_driver(replay, warn_on_version_mismatch=False)
    driver.walk_ticks(start_tick=0, stop_tick=int(driver.tick_limit))
    result = driver.build_run_result(ticks=int(driver.tick_limit))
    replay = msgspec.structs.replace(
        replay,
        header=msgspec.structs.replace(
            replay.header,
            claimed_stats=ReplayClaimedStatsSnapshot(
                complete=True,
                ticks=result.ticks,
                elapsed_ms=result.elapsed_ms,
                score_xp=result.score_xp,
                kills=result.creature_kill_count,
                most_used_weapon_id=result.most_used_weapon_id,
                shots_fired=result.shots_fired,
                shots_hit=result.shots_hit,
            ),
        ),
    )

    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_bytes(dump_replay(replay))
    print(
        f"wrote {out_path} ticks={result.ticks} kills={result.creature_kill_count} "
        f"score={result.score_xp} shots={result.shots_fired}/{result.shots_hit}",
    )


if __name__ == "__main__":
    main()
