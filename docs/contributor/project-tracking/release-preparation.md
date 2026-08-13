---
tags:
  - contributor
  - project-tracking
  - release
---

# Release Preparation

This is the canonical release-readiness checklist for the Python desktop package,
the native Zig runtime, and CrimsonVR personal builds. It consolidates the gates
that were previously distributed across the VR plan and platform-specific notes.

!!! danger "Current decision: do not release"

    The 2026-08-13 audit found reproducible rollback recovery failure,
    nondeterministic native networking tests, an unchanged already-published
    package version, an uncommitted release candidate, and unresolved CrimsonVR
    operational/legal gates. A successful build alone is not release approval.

## Release surfaces

Treat these as separate deliverables with separate publication decisions:

| Surface | Publication path | Current status |
|---|---|---|
| Python desktop package | Tag-triggered PyPI and GitHub release | Blocked by validation, versioning, and candidate-state gates |
| Zig runtime/build targets | Built and tested as part of the source release | Blocked by native networking failures/flakes |
| CrimsonVR Quest and PCVR | Asset-free personal builds from manual workflows | Build paths exist; operational, physical-device, and legal gates remain |

The tag-triggered `.github/workflows/release.yml` publishes only the Python
wheel and source distribution. It does not publish Quest APKs or PCVR packages.
Those binaries remain subject to the private-copy rules in the
[Quest](https://github.com/banteg/crimson/blob/master/crimson-vr/notes/quest-ci.md)
and
[PCVR](https://github.com/banteg/crimson/blob/master/crimson-vr/notes/pcvr-ci.md)
runbooks.

## 2026-08-13 audit baseline

The release candidate was audited on Windows with Python 3.13, .NET 9, and the
CI-pinned Zig 0.16.0 toolchain.

| Gate | Evidence | Result |
|---|---|---|
| Python test suite | 2,573 passed, 12 skipped, 1 failed | **Fail**: reordered rollback input recovery |
| Zig tests | 961 passed, 1 skipped, 1 failed per failing run | **Fail/flaky**: different networking tests failed across runs |
| Direct lockstep smoke | 1 of 5 consecutive Windows runs failed | **Fail/flaky**: `LockstepHandshakeFailed` |
| VR managed tests | 97 passed | Pass |
| Ruff, import boundaries, types, docs | All passed | Pass |
| Python wheel and sdist | `crimsonland-0.10.0` built | Pass build, **fail release version** |
| Zig ReleaseFast and Wasm | Both built | Pass |
| .NET Release build | Zero warnings or errors | Pass |
| Provenance | Passed with exact CI-pinned, hash-verified binaries | Pass |
| Repository state | 87 modified and 34 untracked files | **Fail**: candidate is not committed or CI-verifiable |
| Public CrimsonVR scope | Derived-content/code-licensing decisions remain open | **Fail** |

Windows tests that create temporary files on a different drive from the checkout
can fail before exercising product code because `os.path.relpath` cannot cross
drive letters. The audit reran those cases with the temporary directory on the
workspace drive and they passed. Record such environment-only exclusions
explicitly; do not use them to waive a networking or simulation failure.

## Blocking issue register

All blockers must be closed with evidence, not merely marked understood.

| ID | Blocker | Closure evidence |
|---|---|---|
| RP-01 | Reordered rollback input smoke returns `RollbackHostInputMismatch` | Regression fixed; focused smoke and full Python suite pass |
| RP-02 | Windows native networking is nondeterministic across lockstep handshake and rollback relay tests | Repeated Windows runs and `zig build test` pass without retry-dependent success |
| RP-03 | `pyproject.toml` is still `0.10.0`, which is already tagged/published | Version and lockfile bumped; intended tag exactly matches the package version |
| RP-04 | Release candidate exists only as a large dirty worktree | Complete candidate committed; clean checkout reproduces every required gate |
| RP-05 | Public CrimsonVR derived-content and upstream-code scope is unresolved | Written licensing decision and completed repository/output audit, or release scope reduced accordingly |
| RP-06 | Private-copy workflows, clean-machine PCVR matrix, relay operation, and broader headset testing remain incomplete | Workflow URLs/artifact manifests and signed physical-validation record attached to the release evidence |

## Required automated gates

Run from a clean checkout of the exact commit that will be tagged. A release is
blocked by any unexpected failure, flaky retry, warning that invalidates the
artifact, or unexplained skip.

```bash
just check
uv build
```

The canonical root check covers Ruff, import boundaries, type checking,
documentation validation, ast-grep rules, the Python suite, Zig tests,
ReleaseFast, and Wasm. Also run the release-facing components directly so their
results are visible in the evidence record:

```bash
cd crimson-zig
zig build test --summary all
zig build -Doptimize=ReleaseFast
zig build wasm
```

```powershell
dotnet test crimson-vr/tests/CrimsonVR.Tests/CrimsonVR.Tests.csproj -c Release
```

For networking changes, add the following stability gate on Windows using Zig
0.16.0:

1. Run `zig build test --summary all` three consecutive times.
2. Run `./zig-out/bin/crimson-zig net smoke-lockstep --json` at least ten
   consecutive times.
3. Run every `smoke-rollback` impairment case, including reordered input,
   loss, jitter, reconnect, and resync.
4. Treat any retry-dependent pass as a failure and preserve the failing seed and
   stderr in the evidence record.

## Packaging and payload gates

- Build the wheel and sdist and confirm their version, filenames, and metadata.
- Install the wheel into a fresh environment and smoke the `crimson` and
  `crimsonland` entry points.
- Validate the pinned original-binary provenance used by the test suite.
- Run the Quest/PCVR source payload guard from an asset-free clean checkout.
  A developer checkout with ignored `crimson-vr/godot/assets/` content is
  expected to fail that guard and is not release evidence.
- Inspect Quest APK and PCVR archives for PAQ/PAK/pack payloads, development
  assets, debug keys not intended for the selected flow, and missing native or
  managed runtime files.
- Preserve the generated personal-build manifest and SHA-256 hashes.

## CrimsonVR operational and physical gates

Before calling a personal build ready:

- dispatch both manual workflows from the eventual default branch;
- reproduce Windows and Linux PCVR packages on a clean machine;
- complete first-launch import, relaunch reuse, and actionable failure recovery
  on a clean Quest install;
- validate direct LAN and the operated relay with two, three, and four players;
- place Quest in every negotiated slot and cover Survival, Rush, perk choices,
  player death, results, disconnect, reconnect, and resync;
- test delay, reorder, packet loss, duplicate delivery, focus loss,
  sleep/resume, Wi-Fi transition, and rollback catch-up load;
- configure owned relay DNS, capacity monitoring, rate limits, expiry, and
  incident ownership before enabling room-code play in a release build;
- finish or explicitly disposition every active headset validation-checklist
  item.

See the
[multiplayer implementation](https://github.com/banteg/crimson/blob/master/crimson-vr/notes/multiplayer-implementation.md)
for the detailed matrix and
[port status](https://github.com/banteg/crimson/blob/master/crimson-vr/notes/port-status.md)
for deliberately deferred or substituted features.

## Legal and distribution gate

CrimsonVR remains asset-free, but that alone does not authorize public binary or
source redistribution. Before public release:

1. Audit committed fixtures, screenshots, generated manifests, documentation
   media, and every build output for original-asset-derived content.
2. Record the permitted scope for upstream-linked code and binaries.
3. If permission is narrower than the repository, publish only the approved
   original-code/patch surface and keep personal binary workflows private.
4. Confirm that release notes and user instructions describe the supported GOG
   Crimsonland Classic asset-import flow without implying redistribution rights.

No public CrimsonVR package may be uploaded until this gate has a written owner
and disposition.

## Versioning and release procedure

After every blocker above is closed:

1. Freeze the release scope and update this audit baseline with the candidate
   commit, intended version, platform matrix, and evidence links.
2. Choose the new version. The package version must not already exist on PyPI or
   as a repository tag.
3. Bump `pyproject.toml` and refresh `uv.lock`.
4. Re-run every automated, packaging, and applicable VR gate from the clean
   release commit.
5. Review generated release notes, known limitations, asset-import guidance,
   and legal wording.
6. Create the conventional release commit and annotated `v<version>` tag.
7. Push the commit and tag, then monitor the release workflow through PyPI and
   GitHub release creation.
8. Verify the published wheel in a new environment and record its hashes.
9. Publish or distribute VR personal-build artifacts only through the separately
   approved private flow.

## Definition of release-ready

A candidate is release-ready only when all of the following are true:

- the exact release commit is clean, committed, reviewable, and reproducible;
- all required checks pass without retries or unexplained skips;
- package version, tag, artifacts, manifests, and release notes agree;
- supported platform packages pass clean-machine and payload inspection;
- every advertised network scenario passes its deterministic and physical gate;
- known limitations are accurate and do not contradict implementation notes;
- public distribution has an explicit legal disposition; and
- the evidence record identifies the responsible reviewer and artifact hashes.

## Evidence record template

Copy this table into the release PR or release issue and link durable logs rather
than pasting only summaries.

| Field | Value |
|---|---|
| Candidate commit | |
| Version/tag | |
| Reviewer and date | |
| `just check` | |
| Repeated Windows networking gate | |
| Wheel/sdist hashes and install smoke | |
| Quest workflow/build manifest | |
| PCVR workflow/build manifests | |
| Physical-device matrix | |
| Relay operations approval | |
| Derived-content audit | |
| Code/binary distribution decision | |
| Known limitations reviewed | |
| Final decision | Go / No-go |
