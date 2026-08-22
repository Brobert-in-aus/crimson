# Crimson 0.11.0 release-candidate evidence

Prepared on 2026-08-22 from branch `vr-control-rectangle`, based on commit
`ea0915c8e`. The owner approved the prepared 0.11.0 release commit after final
Quest, PCVR, and multiplayer review. Hosted checks for the exact commit run only
after it is pushed to the private release-staging repository.

## Automated source gates

| Gate | Result |
|---|---|
| `just check` | Pass: Ruff, 3/3 import contracts, ty, documentation validation, ast-grep scan/tests, Python 2576 passed/12 skipped, Zig 963 passed/1 intentional skip, ReleaseFast build, and Wasm build |
| `dotnet test crimson-vr/tests/CrimsonVR.Tests/CrimsonVR.Tests.csproj -c Release` | Pass: 119/119 |
| `zig build test --summary all`, three consecutive runs | Pass on all three: 963 passed, 1 intentional skip per run |
| Native lockstep smoke, ten consecutive runs | Pass: 10/10 without retry |
| Native rollback impairment matrix | Pass: all 16 modes without retry, covering normal, delay, reorder, loss, forced resync, single/double/triple reconnect, reconnect plus resync, jitter, bidirectional jitter, and reconnect plus bidirectional jitter |
| `actionlint` | Pass for all GitHub Actions workflows |
| `git diff --check` | Pass |
| Version availability | Pass: PyPI latest is 0.10.0; no PyPI 0.11.0 release and no local or origin `v0.11.0` tag |

The Windows test run uses `TEMP` and `TMP` on the `D:` drive because Python's
cross-drive `relpath` behaviour cannot represent this checkout relative to the
default `C:` temporary directory. This changes only the temporary root.

## Package gates

| Output | Bytes | SHA-256 | Result |
|---|---:|---|---|
| `crimsonland-0.11.0-py3-none-any.whl` | 972267 | `0a5df0f76188f3ab3d775554fa46725a144d84f2908f9954605dfa03f543ffeb` | Fresh Python 3.13 environment installed; `crimson --help` and `crimsonland --help` passed |
| `crimsonland-0.11.0.tar.gz` | 785776 | `70e861fc5a5aa0601c9c8bdc7b7d1648aadeb291bfcfad47ba403e6f66dfae02` | Built by `uv build` |
| `CrimsonVR.quest.apk` | 114454032 | `25ad5a013692cef1b333f6aa630b891a650f7172b8854aaaa98cbf0d81257e86` | Final Release export hides developer controls and passed required-payload and asset-free APK gates |
| `CrimsonVR-PCVR-Windows.zip` | 80515375 | `14d7d5fcbe029487d832f43fbb204f7a65fa15df3659259e37b702e83db5e36d` | Final Windows export hides developer controls and passed executable, PCK, managed/native runtime, hash, and asset-free gates |
| `CrimsonVR-PCVR-Linux.tar.gz` | 71624899 | `7e5a9fe68b63696e4c42334f80817e059ae7a202f0f2bbc7cd1d1efd6f3c60e3` | Final Linux export hides developer controls and passed executable-bit, PCK, managed/native runtime, hash, and asset-free gates |

The locally generated packages are verification outputs and are ignored by Git.
No game assets, APK, PCVR archive, recording, key, or locally generated asset
pack is part of the release commit.

## Hosted CI evidence

The isolated-repository Quest rehearsal completed the workflow twice from a
fresh source copy, including clean checkout validation, toolchain bootstrap,
asset-free APK verification, artifact download, generated signing key, saved-key
reuse, and signer-certificate equality:

- generated-key run [32534616770](https://github.com/Brobert-in-aus/cvr-e2e-20260822-084949/actions/runs/32534616770);
- saved-key run [32535378646](https://github.com/Brobert-in-aus/cvr-e2e-20260822-084949/actions/runs/32535378646);
- local machine-readable record:
  `D:\Projects\_scratch\cvr-e2e-20260822-084949\evidence.json`.

Those runs validate the build/sign/update mechanics but predate the final
public-fork wording and current candidate tree. After owner approval, push the
release commit and require green CI plus manual Quest and PCVR workflow runs on
that exact SHA before tagging.

## Owner acceptance and deferred coverage

- On 2026-08-22 the project owner accepted the current Quest, PCVR, and
  PCVR-to-Quest multiplayer state for 0.11.0 after final clean first-run tests.
- The expanded [Quest](quest-release-checklist.md),
  [PCVR](pcvr-release-checklist.md), and
  [multiplayer](multiplayer-implementation.md#post-release-physical-and-operated-matrix)
  matrices remain post-release coverage and hardening records. Unchecked rows do
  not expand the configurations advertised for 0.11.0.
- Release exports hide the developer debug-overlay toggle while retaining the
  diagnostic implementation in developer builds.

Casual network Quests (MP-10) and Python/native cross-runtime multiplayer
(MP-11) are explicitly outside the 0.11.0 release scope.
