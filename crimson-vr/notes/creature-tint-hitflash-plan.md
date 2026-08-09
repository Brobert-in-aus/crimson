# Creature base tint + hit-flash — implementation plan (implemented)

> **IMPLEMENTED 2026-07-09 (ABI v4).** This landed simpler than the plan below
> assumed: `CreatureInit.tint` was **already computed** per spawn template (incl.
> XP-reddening + rare-creature `applyTint`), so no ~40-template port was needed —
> just wire `spawnInit` (copy `init.tint` -> `entry.color`) and `applyPoolResidue`
> (from the residue fields), add `color` + `hit_flash_timer` to `CreatureState`,
> set the flash in the core `applyDamage`, decay it in the per-creature update
> loop (before the freeze gate), and append `r,g,b,a,hit_flash_timer` to the ABI
> `CreatureSnap`. M1 gate stayed green (8 baseline); new golden test
> "abi creature snapshot carries tint and hit flash". Frontend `Diorama.CreatureTint`
> now starts from the base color. The rest of this doc is kept for the record.

Status (original): **not started** (deferred from the 2026-07-09 presentation-ABI round). This
is the runtime-touching half of the "creature tint" gap. The safe half
(energizer-blue + lifecycle fade, computed frontend-side from the global
`energizer_timer` header field) already shipped; see PLAN.md §6 audit.

This lands as a future **ABI v4** (append-only `CreatureSnap` fields). It is fully
verifiable without a headset: the M1 replay-verify gate proves determinism, and
golden tests pin the tint values against the Python reference.

## What's missing today

- Zig `CreatureState` (`runtime/creatures.zig`) stores **neither** a per-creature
  `color`/`tint` **nor** `hit_flash_timer`.
- Per-creature base tint is drawn from spawn RNG then **discarded**
  (`_ = randfTagged(rng, callers.tint, ...)` in `spawnBasicRandomTemplate` and
  the alien/lizard/spider random templates).
- Hit-flash: `hit_flash_timer` is set to `0.2` on damage and decayed by dt in the
  reference (`creatures/damage.py`, `creatures/runtime.py`); the Zig runtime does
  neither. NOTE: the Python *renderer* (`render/world/draw.py`) does **not**
  visibly consume `hit_flash_timer` — so "faithful" hit-flash rendering matches the
  original exe's white-brighten behaviour, not a Python render reference.

## Tint model (reference)

`creature.tint` is an **RGBA multiplier** (default `(1,1,1,1)`), resolved at spawn
by `resolve_tint(init.tint)` (`creatures/spawn.py`). Two sources:

1. **Fixed per-template constants** — the common case. ~40 templates in
   `creatures/spawn_templates.py` set `tint=(r,g,b,a)` literals
   (e.g. `(0.6,0.6,1.0,0.8)`, `(0.9,0.1,0.1,1.0)`, `(None,None,None,1.0)` where
   `None`→1.0). Mechanical to port.
2. **Per-instance random channels** — a few basic-random templates draw 1–3 tint
   channels via RNG (the currently-discarded `randfTagged` draws in
   `creatures.zig`). The draw already happens, so **capturing** it instead of
   discarding does not change RNG order → determinism is preserved.

Render application (`draw.py draw_creatures`, already ported frontend-side for the
energizer/lifecycle part): `tint_rgba = creature.tint` → energizer blend (max_hp<500,
lerp toward `(0.5,0.5,1,1)` by clamp(energizer,0,1)) → lifecycle alpha fade
(lifecycle_stage<0 → `a += stage*0.1`) → `scaled_alpha(entity_alpha)`.

## Key de-risking fact

`replay_codec.zig` `ReplayCreatureSlotResidue` **already has**
`hit_flash_timer` + `tint_r/g/b/a` (lines ~783–787): the `.crd` capture format
already round-trips these, and the codec already parses them. They are simply
**not applied** to `CreatureState` in `creatures.zig applyPoolResidue` (which lists
every other residue field but drops these four + hit_flash). So:
- **No codec / wire-format change.** The parity-sensitive part is already done.
- The replay-through-ABI gate will exercise the values for free once stored.

## Implementation steps

1. **Runtime state** (`runtime/creatures.zig`):
   - Add `color: Color = .{}` (reuse `terrain_fx.Color` or a local RGBA, default
     `(1,1,1,1)`) and `hit_flash_timer: f32 = 0.0` to `CreatureState`.
   - In `applyPoolResidue`, wire the already-parsed residue:
     `entry.color = { slot.tint_r, slot.tint_g, slot.tint_b, slot.tint_a }` and
     `entry.hit_flash_timer = slot.hit_flash_timer`. (Verify residue defaults:
     `tint_* = 0` today, which would spawn black — make residue default to 1.0
     OR only apply when the capture provided them. Check native capture semantics.)
2. **Live spawn tint** (`runtime/spawn.zig` / `creatures.zig` templates):
   - Add `tint: Color` to `SpawnInit`/`CreatureInit` (default `(1,1,1,1)`).
   - Per template, assemble the RGBA to match `spawn_templates.py`: fixed
     constants for the literal templates; for the random ones, **store** the
     `randfTagged` draw into the right channel(s) instead of `_ = ...`. Match each
     template's channel assembly against the Python 1:1.
   - `spawnInit` copies `init.tint → entry.color`.
3. **Hit-flash** (`runtime/creatures.zig` damage + update):
   - In the damage entry point (`creature_apply_damage` equivalent), set
     `creature.hit_flash_timer = 0.2`.
   - In the per-creature update, decay `hit_flash_timer -= dt` (clamp ≥0), mirroring
     `runtime.py` lines ~1021.
4. **ABI** (`host_abi/exports.zig` + `abi/crimson_host.h`): append `r,g,b,a`
   (+ `hit_flash_timer`) to `CreatureSnap`; bump `abi_version` 3→4; update the
   C# `Sim.CreatureSnap` + `ExpectedAbiVersion`; rebuild both native libs.
5. **Frontend** (`Diorama.cs`): in `CreatureTint`, start from `entry.color`
   (not white), keep the energizer/lifecycle math on top; add a white-brighten
   when `hit_flash_timer > 0` (lerp tint toward white by e.g. `hit_flash_timer/0.2`).
6. **Verification (no eyes):**
   - `zig build test` — the M1 replay gate must stay green (8 baseline failures).
     Determinism is preserved (no RNG-order change), so this is the primary check.
   - New golden tests: for a fixed seed, drive spawns through the ABI and assert
     the first N creatures' `color` matches the Python `resolve_tint`/template
     literals (port a handful of representative templates: a fixed-tint one, a
     `None`-channel one, a random-channel one).
   - Add a gate assertion that a hit creature reports `hit_flash_timer > 0` right
     after a damage tick, decaying toward 0.

## Risks / watch-items

- **Residue tint defaults.** `tint_* = 0` default would spawn *black* creatures if
  applied unconditionally to residue-seeded (replay) creatures. Confirm how the
  native capture distinguishes "no tint captured" from "tint = 0"; likely apply
  residue tint only for captures that carry it, else leave `(1,1,1,1)`.
- **Guardrail.** This touches `runtime/` gameplay files — allowed here because it
  records sim-computed *visual* state without changing gameplay/RNG. Keep every
  edit additive; if the gate reddens, the change introduced an order/behaviour
  shift — revert and re-approach.
- **Scope.** The ~40-template tint port is the bulk of the effort; it's mechanical
  but wide. Consider landing fixed-tint templates first (covers most creatures),
  then the random-channel ones, then hit-flash, each behind the same gate.
