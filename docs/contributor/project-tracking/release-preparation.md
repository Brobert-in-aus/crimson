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

!!! warning "Current decision: release candidate preparation"

    The 2026-08-13 engineering remediation closed the rollback recovery and
    Windows networking defects found by the initial audit. Version `0.11.0` has
    been selected and applied. The project owner has approved the source +
    user-owned fork/CI + user-supplied-assets distribution model described
    below, closing the previous permission-scope blocker for that release
    surface. On 2026-08-22 the owner accepted the current Quest, PCVR, and
    PCVR-to-Quest multiplayer state for this scope and approved the prepared
    release commit. Private exact-SHA hosted CI and the final GitHub review
    remain; do not create or push a release tag yet.

## Release surfaces

Treat these as separate deliverables with separate publication decisions:

| Surface | Publication path | Current status |
|---|---|---|
| Python desktop package | Tag-triggered PyPI and GitHub release | Automated gates pass; version selected as `0.11.0`; final clean-commit rerun pending |
| Zig runtime/build targets | Built and tested as part of the source release | Automated and repeated networking gates pass on Windows |
| CrimsonVR Quest and PCVR | Source plus asset-free user-owned personal builds from manual workflows | Owner accepted current Quest, Windows PCVR, and PCVR-to-Quest scope on 2026-08-22; broader matrices are post-release coverage |

The tag-triggered `.github/workflows/release.yml` publishes only the Python
wheel and source distribution. It does not publish Quest APKs or PCVR packages.
Those binaries remain subject to the personal-fork rules in the
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
| Public CrimsonVR scope | Owner-approved source/fork workflow; no game assets or maintainer-distributed VR binaries | **Pass for this scope** |

Windows tests that create temporary files on a different drive from the checkout
can fail before exercising product code because `os.path.relpath` cannot cross
drive letters. The audit reran those cases with the temporary directory on the
workspace drive and they passed. Record such environment-only exclusions
explicitly; do not use them to waive a networking or simulation failure.

## 2026-08-13 remediation evidence

The networking gates now drive UDP handshakes and rollback correction to a
bounded protocol state instead of assuming that one nonblocking receive cycle is
enough on Windows. The rollback smoke applies the same bounded catch-up rule to
normal, delayed, reordered, and dropped input delivery. No retry is hidden: each
bounded loop returns a specific error if the required state is not reached.

| Gate | Remediation evidence | Result |
|---|---|---|
| Python test suite | 2,574 passed, 12 skipped with pinned Zig on `PATH` and same-drive external temp storage | Pass |
| Zig tests | Eight consecutive passes during remediation, then three consecutive final-gate passes; 962 passed and 1 intentional skip per run | Pass |
| Lockstep smoke | 25 consecutive ReleaseFast passes during remediation plus 10 final-gate passes | Pass |
| Rollback smoke | Normal, delay, reorder, and drop each passed 20 consecutive runs; all 16 impairment/reconnect/resync modes passed the final matrix | Pass |
| VR managed tests | 97 passed in Release configuration | Pass |
| Ruff, import boundaries, types, docs | Passed; documentation site built successfully | Pass |
| ast-grep | Main scan completed with advisory findings only; all 36 configured rule tests passed | Pass |
| Zig ReleaseFast and Wasm | Both built successfully with Zig 0.16.0 | Pass |
| Python wheel and sdist | PEP 517 build and fresh-environment `crimson`/`crimsonland` entry-point smokes passed | Pass build, **fail release version** |

The PEP 517 verification artifacts are intentionally outside the checkout and
are not publication candidates because they still carry version `0.10.0`:

| Artifact | SHA-256 |
|---|---|
| `crimsonland-0.10.0-py3-none-any.whl` | `2f421749b6f8716270a40ae68c8a9960a7776f944805dc39f4c706db2a2005b0` |
| `crimsonland-0.10.0.tar.gz` | `6d04415c89126543ceb0aa6bc51fc9f65fc670fba20ab1a7c7a2a816028dca5d` |

The exact `just check` wrapper could not be invoked in this desktop environment
because `just` is not installed and its `uv` console trampolines are invalid.
The commands that compose the recipe were run directly. The optional local Zig
ast-grep configuration was not counted as a release gate because its committed
custom-language path targets a developer-specific macOS `.dylib`; the canonical
`just check` recipe runs the portable main configuration. The eventual versioned
release commit must still run in CI or a clean checkout using the canonical
wrapper.

## Blocking issue register

All blockers must be closed with evidence, not merely marked understood.

| ID | Status | Blocker | Closure evidence |
|---|---|---|---|
| RP-01 | Closed locally | Reordered rollback input smoke returned `RollbackHostInputMismatch` | State-driven catch-up implemented; 20/20 reorder smokes and full Python suite pass |
| RP-02 | Closed locally | Windows native networking was nondeterministic across lockstep handshake and rollback relay tests | Repeated Windows suites and smokes pass without retry-dependent acceptance |
| RP-03 | Closed locally | `0.10.0` was already tagged/published | Minor bump selected for the substantial new VR, multiplayer, replay, and tooling capabilities; package and lockfile now use `0.11.0`; intended tag is `v0.11.0` |
| RP-04 | Release commit approved; private exact-SHA CI pending | The original release candidate existed only as a large dirty worktree | Candidate consolidated, fully rerun, staged without generated payloads, and approved for the 0.11.0 release commit |
| RP-05 | Closed by release-scope decision | Public CrimsonVR derived-content and upstream-code scope was unresolved | Publish source/history and asset-free user-owned build workflows only; GitHub forking is the supported source-copy mechanism, users supply Classic assets locally, and maintainer-distributed/bundled VR binaries remain out of scope |
| RP-06 | Closed by owner acceptance | Quest hosted create/build/download/re-sign passed twice; broader PCVR, relay-operation, and headset matrices exceed the accepted 0.11.0 scope | Quest runs 32534616770 and 32535378646, local package evidence, clean Quest/PCVR first-run tests, PCVR-to-Quest multiplayer test, and owner acceptance on 2026-08-22 |

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

Use the release-candidate evidence forms at
`crimson-vr/notes/quest-release-checklist.md` and
`crimson-vr/notes/pcvr-release-checklist.md`. Casual network Quests and Python
mixed-runtime rooms are not 0.11.0 claims and therefore use explicit
out-of-scope dispositions rather than implied passes.

See the
[multiplayer implementation](https://github.com/banteg/crimson/blob/master/crimson-vr/notes/multiplayer-implementation.md)
for the detailed matrix and
[port status](https://github.com/banteg/crimson/blob/master/crimson-vr/notes/port-status.md)
for deliberately deferred or substituted features.

## Distribution scope decision

The project owner has selected the following release model:

1. Publish the source, history, documentation, and asset-free personal-build
   workflows. GitHub's supported fork mechanism is the source-copy path.
2. Do not commit, upload, or distribute Crimsonland art, audio, PAQ/PAK files,
   generated asset packs, manifests derived from a user's install, or the local
   bundled-assets APK.
3. Each user runs the build workflow for their own personal binary and supplies
   their own GOG Crimsonland Classic assets locally after the build.
4. CI source and output gates must prove that downloadable workflow packages are
   asset-free. The separate local bundled build has an inverse payload gate and
   remains a private convenience output for the builder's own headset.

This is the recorded product-owner release scope, not a general licence grant or
legal opinion. Any future maintainer-hosted VR binary or bundled asset package is
a separate release surface and requires a new disposition.

## Versioning and release procedure

After every blocker above is closed:

1. Freeze the release scope and update this audit baseline with the candidate
   commit, intended version, platform matrix, and evidence links.
2. Choose the new version using the [Versioning Policy](versioning.md). The
   package version must not already exist on PyPI or as a repository tag.
3. Bump `pyproject.toml` and refresh `uv.lock`.
4. Re-run every automated, packaging, and applicable VR gate from the clean
   release commit.
5. Review generated release notes, known limitations, asset-import guidance,
   and legal wording.
6. Create the conventional release commit and annotated `v<version>` tag.
7. Push the commit and tag, then monitor the release workflow through PyPI and
   GitHub release creation.
8. Verify the published wheel in a new environment and record its hashes.
9. Keep maintainer releases source-only; users request asset-free personal-build
   artifacts from their own forks.

## Definition of release-ready

A candidate is release-ready only when all of the following are true:

- the exact release commit is clean, committed, reviewable, and reproducible;
- all required checks pass without retries or unexplained skips;
- package version, tag, artifacts, manifests, and release notes agree;
- supported platform packages pass clean-machine and payload inspection;
- every advertised network scenario passes its deterministic and physical gate;
- known limitations are accurate and do not contradict implementation notes;
- public distribution stays within the recorded source/fork/user-supplied-assets scope; and
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
| Quest workflow/build manifest | Commit `7e804816885d3bfb2419c268fd0c7c323af08dd3`; private runs [32534616770](https://github.com/Brobert-in-aus/cvr-e2e-20260822-084949/actions/runs/32534616770) and [32535378646](https://github.com/Brobert-in-aus/cvr-e2e-20260822-084949/actions/runs/32535378646); generated and restored signing-key paths passed |

The current 0.11.0 candidate's local test, package, hash, hosted-CI, owner
acceptance, and deferred-coverage record is maintained in
`crimson-vr/notes/release-evidence-0.11.0.md`.
| PCVR workflow/build manifests | |
| Physical-device matrix | |
| Relay operations approval | |
| Derived-content audit | |
| Code/binary distribution decision | |
| Known limitations reviewed | |
| Final decision | Go / No-go |
