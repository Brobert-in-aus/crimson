# VR menu and interface UX audit — 2026-08-11

## Scope and evidence

This audit covers the Godot/OpenXR menu shell, first-run path, pause/settings,
Arena & Layout, multiplayer lobby, and perk choice. It uses a static heuristic
review and cognitive walkthrough of the current C# implementation plus the
original Crimsonland menu reference. It is evidence level 1: the code builds,
but no headset was connected, so stereo legibility, reach, hand-tracking jitter,
and physical poke feel still require an operated Quest/PCVR pass.

Follow-up 2026-08-11: headset testing found the centred tutorial panel occluded
the independently positioned playfield, especially in Cabinet mode. It now uses
a close, reachable sidecar position whose inner edge clears the Tabletop arena
and the more distant Cabinet playfield, preserving the combat sightline.

The same headset pass found recenter progress attached to the stale menu pose.
Because that pose is precisely what recenter repairs, hold progress and success
feedback are now transient head-relative labels directly in front of the player.

Tutorial follow-up evidence: replay `20260811-170052.crd` ended at the practice
stage with zero creatures and one uncollected bonus. The director correctly
requires both to be empty, but the VR copy only said to clear the wave. Prompts
and persistent bonus hints now state the pickup requirement, and the sidecar
uses the same recenter-stable, world-upright seat-facing yaw as Cabinet power-up
information.

Follow-up 2026-08-13: headset screenshots exposed two transparent-layer ordering
failures. The first-run ClassicPanel rendered after its default-priority labels,
erasing the text over the plate while leaving its overflow visible outside. The
layout editor's real creature previews likewise rendered after the perk detail
label. Classic panel backdrops and UI labels now use an explicit shared render
band (backdrop 58, text 68); the perk detail backing also owns the backdrop band.
First-run copy has a bounded smart-wrap width and shorter controller wording, so
expanded text remains inside the panel. The corrected Release APK is installed;
an in-headset before/after visual re-check remains required.

## UX decision brief

- Job: enter VR, understand the control model, choose a mode, adjust comfort
  settings safely, recover from failures, and return to play.
- User mode: first-time and returning players, seated or standing, using Touch
  controllers or optical hands.
- Frequency/risk: menus are occasional; recenter, run exit, layout reset, and
  perk commitment have comfort or irreversible-session consequences.
- Pattern: diegetic hub-and-spoke navigation with direct-touch controls,
  contextual first-run teaching, and explicit confirmation/recovery for risk.
- Primary action: one concrete action per state (Edit Layout, Tutorial, Resume, Retry,
  Confirm, or Choose).
- Recovery: Back/cancel preserves context; destructive actions confirm or undo;
  multiplayer failures preserve entered room/address data.
- Required states: first-run, normal, focused/selected, confirmation, connecting,
  reconnecting, error, retry, unavailable, success.
- Handoff constraints: retain Crimsonland art, physical depth feedback, shared
  menu plane, release-to-rearm guard, and the 350 ms reveal cooldown.

## Task ergonomics contract

- Core task: operate the game shell without guessing hidden VR gestures.
- Cognitive load: interaction, recenter mapping, consequences, and recovery are
  visible at the moment they matter rather than remembered from documentation.
- Control model: poke remains primary; hold protects recenter; confirm protects
  run exit; undo protects layout reset; select-then-confirm protects perk choice.
- Speed path: returning players skip first-run and retain direct single-poke
  access for routine, reversible actions.
- Error prevention: explicit labels, hold/confirm states, release guards, and
  removal of controls that have no effect.
- Recovery: retry/edit/back for multiplayer; keep playing for run exit; undo for
  layout; preserved room code/address after failure.
- Evidence plan: build/test automation followed by in-headset scenario testing.

## Findings and implementation

### 1. First-run direct touch was undiscoverable — severity 3

The app skipped its first-run prompt, while the Controls reference was behind
Options. The first screen therefore required a gesture it did not teach.

Implemented:

- First launch now shows a short diegetic guide before Main Menu.
- It explains controller-top/fingertip poke and the recenter mapping.
- **Edit Layout** is the left action and **Skip** is the right action; either
  persists `FirstRunDone`, while returning players skip the guide.
- Edit Layout enters the existing Options → VR Settings → Edit Layout stack, so
  Back navigation remains consistent.

### 2. Recenter was immediate and undocumented — severity 3

Right A or left Menu moved the entire arena on the press edge, and Controls did
not name the mapping.

Implemented:

- Recenter now requires a 700 ms hold.
- A five-step progress indicator and “Recentered” completion message make status
  visible.
- First-run and Controls both document right A / left Menu.
- Initial automatic placement remains unchanged.

### 3. Pause “Quit” hid its consequence — severity 3

Pause Quit immediately abandoned the run, while Main Menu Quit exited the app.

Implemented:

- The action is labelled “Exit to Main Menu.”
- First poke opens a consequence state: current progress will be lost.
- “Keep Playing” cancels; “Exit Run” commits.

### 4. Multiplayer failure exposed an invalid Ready action — severity 3

The shared lobby state could retain Ready after a connection/session failure and
did not offer input-preserving recovery.

Implemented:

- Failure is an explicit state with Retry, Edit, and Back.
- Retry reuses the selected relay/LAN role, players, mode, room code, and address.
- Edit returns to the applicable preserved input screen.
- Normal lobby state restores Ready/Unready and Cancel.

### 5. “UI Info texts” was inert — severity 2

The checkbox persisted a setting that had no runtime consumer.

Implemented:

- Removed the control from Options and its event wiring.
- Retained the config field for backwards-compatible loading/saving.
- It should only return when bonus/weapon hover information is implemented.

### 6. “Reset layout” had ambiguous scope and no recovery — severity 2

The action reset only current-mode action buttons/control pad, despite appearing
beside arena placement controls, and saved immediately.

Implemented:

- Renamed to “Reset buttons & pad.”
- First poke changes to “Confirm reset”; second performs the reset.
- The same control becomes “Undo reset” and restores the current mode's saved
  transforms until the screen is left.

### 7. Perk information competed with immediate commitment — severity 2

A card chose immediately, while its description required holding a small “?”
control.

Implemented:

- A card poke selects and highlights it, pins its description, and leaves the
  choice reversible by poking another card.
- The per-card “?” row is replaced by one “Confirm Perk Selection” button below
  the cards; it appears only after a selection and is the sole commit action.
- Confirm hides immediately when pressed. New offers clear selection and keep it
  hidden until one of their own cards is selected. Because confirmation is also
  spatially below the card row, the confirming hand cannot begin the next offer
  inside either a commit control or one of its freshly spawned cards.
- The description backing and copy own explicit UI render priorities, preventing
  layout-edit creature previews from drawing through the reading surface.

### 8. Layout preview obscured the editor canvas — severity 3

The maximum seven-card preview was useful for clearance testing but remained a
large permanent blocker while editing the arena. Tabletop also exposed Cabinet
placement dimensions, and held-button pitch moved opposite the controllers.

Implemented 2026-08-13:

- The normal Confirm location becomes Show/Hide Perk Menu Preview during editing;
  the toggle remains reachable when the cards and description are hidden. A
  2026-08-13 follow-up made layout-preview state authoritative during snapshot
  refresh, which had been re-showing the cards immediately after every hide.
- Tabletop hides and enforces flat/reachable tilt and distance, and one button
  cycles Small (0.75x), Medium (1.0x), and Large (1.25x); Cabinet keeps its
  detailed placement range. This enforcement also prevents a persisted Cabinet
  pitch from making Tabletop vertical on startup.
- Cabinet's clean Pause and Level-Up defaults now reuse the current Tabletop
  transforms, while saved per-mode overrides still win until reset.
- Player-facing widget pitch now follows controller forward/back rotation rather
  than applying the controller twist with the opposite visual sign.
- Arena height now reads floor-up instead of exposing the internal downward-drop
  value: 0 m is floor, 1 m is one metre above, and the clean default is 0.5 m.

## Scenario re-check

- First run: guide → Skip → Main Menu, or guide → Edit Layout → one-time editor
  tutorial → normal Back
  stack. Static path passes; physical reach remains headset-dependent.
- First Play Game: Tutorial starts the lesson; Skip opens the unchanged mode
  selector. The pure policy tests prove the prompt is suppressed after either it
  has been seen or the tutorial has already been completed.
- Layout distance: controller/hand movement is reduced to recenter-local depth,
  clamped to 0.05-0.45 m beyond the base plane; automated tests prove horizontal
  and vertical deltas do not affect the setting. Physical comfort remains an
  in-headset check.
- Returning run: Main Menu remains the fast path with no repeated onboarding.
- Recenter: tap/release does nothing; held input shows progress and completion.
- Pause exit: accidental first poke cannot abandon the run; cancellation is
  explicit.
- Multiplayer error: entered data is preserved with Retry/Edit/Back; Ready is not
  presented as the error action.
- Layout reset: scope is visible, confirmation is required, and saved overrides
  can be restored.
- Perk choice: information can be read without commitment; selection remains
  reversible until the spatially separate Confirm control is deliberately poked.

## Remaining headset validation

1. Re-capture the installed first-run guide and layout-edit perk preview; confirm
   the bounded onboarding copy and both buttons fit in both eyes, and no creature
   sprite draws over perk text.
2. Verify the 700 ms recenter hold is long enough to prevent accidents but not
   tiring; confirm completion text remains readable after the world moves.
3. Exercise pause confirmation with both controller and optical-hand jitter.
4. Trigger relay unavailable, invalid IPv4, connection failure, and reconnect
   failure; verify all three recovery controls are reachable and unambiguous.
5. Reset and undo customized layouts in both Cabinet and Tabletop modes.
   Verify Tabletop's named sizes, the perk-preview toggle, matching Cabinet button
   defaults, and direct controller-to-widget pitch while doing so.
6. Compare seven perks, switch selection between cards, then confirm; with
   accumulated picks, verify the confirming hand does not contact the next offer.
7. Re-check expanded/localized text and the smallest supported render scale.

## 2026-08-22 general UX follow-up

This pass separates issues that can be resolved from code and flow inspection
from questions that require a worn headset. Returning players retain the direct
Main Menu path; new players are guided toward first value without being forced
through the tutorial.

Implemented without headset interaction:

- The first Play Game action opens a one-time recommendation with **Tutorial**
  and **Skip**. Tutorial starts the lesson directly; Skip opens the normal mode
  selector. Returning players reach the selector immediately.
- The first Edit Layout visit briefly lists Pause, Level Up, the Cabinet control
  pad and shared menu-distance point before enabling the grab handles.
- One centred blue grip point moves the shared menu/perk/results plane toward or
  away from the player. It cannot change height, horizontal position or angle.
- Offline session-creation failures now open a player-facing recovery panel with
  **Retry** and **Main Menu**, rather than leaving the player in a blank or
  partially transitioned state.
- The missing-assets flow uses numbered PC/headset steps and shows immediate
  "Checking" feedback while preventing duplicate imports.
- VR Display uses player-language quality labels (Resolution and Edge
  smoothing), and the movement dead zone is expressed as Off/Low/Medium/High.
- The tutorial's opening prompt points out Pause.
- Validation results are batch-versioned. Batch 3 clears the previous checklist
  and contains only stereo, reach, tracked-input, layout, and network questions
  that cannot be settled headlessly.

Fresh headset checklist (batch 3): first-boot action order; one-time layout
teaching; shared menu-depth-only manipulation; Tutorial/Skip routing; reach and
stereo legibility; quit-confirm separation; tutorial completion;
Pause/Level Up discovery; perk confirmation safety; both VR Settings pages;
Cabinet and Tabletop layout editing; animated controllers; optical-hand joint
matching; whether direct poke needs a ray fallback; and full PC/Quest LAN flow.

Deferred until operated testing:

- A direct-ray alternative, because target acquisition, accidental activation,
  and mode-switch cost must be measured with controllers and hands before
  choosing the interaction rule.
- Further spatial offsets, typography sizes, and environmental grounding, which
  depend on stereo comfort and physical reach at the new shared menu depth.
