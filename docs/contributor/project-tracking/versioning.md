---
tags:
  - contributor
  - project-tracking
  - release
  - versioning
---

# Versioning Policy

Crimson uses [Semantic Versioning](https://semver.org/) for repository releases
and PEP 440-compatible spelling for the Python package. The package version in
`pyproject.toml` is the single source of truth for a tagged source release. The
release tag must be exactly `v<version>`.

The project is currently pre-1.0. A `0.y.0` release can therefore contain an
intentional compatibility break, but pre-1.0 is not permission to break users
casually. Document migrations and prefer a deprecation path whenever practical.

## Choosing the increment

Choose the highest increment required by any included change:

| Increment | Use when | Examples |
|---|---|---|
| Patch: `0.11.0` to `0.11.1` | The release is backward-compatible and contains fixes, security hardening, performance improvements, tests, documentation, or packaging corrections | Fix rollback recovery; repair a wheel; clarify asset import |
| Minor: `0.11.1` to `0.12.0` | The release adds a user-visible capability, command, supported platform, network mode, or data surface; or intentionally breaks a pre-1.0 public interface | Add multiplayer; add a CLI command; change a supported replay contract |
| Major: `0.x` to `1.0.0`, then `1.x` to `2.0.0` | The maintainers are declaring the documented user-facing interfaces stable, or a post-1.0 release makes an incompatible change | Promise the 1.0 CLI/config compatibility contract; later remove it incompatibly |

Pure internal refactors do not require a release. If they are included in a
release, they inherit the increment selected by the release's externally
observable changes. Do not use commit count, elapsed time, or diff size to choose
a version.

For the current pre-1.0 line, these are treated as public interfaces when they
are shipped or documented: CLI commands and exit behavior, configuration and
save files, replay/capture formats, Python import surfaces advertised for use,
network compatibility, supported platforms, and artifact/install behavior.

## Prereleases and development versions

Use PEP 440 prereleases only when publishing an artifact for broader testing:

- alpha: `0.12.0a1` for incomplete or exploratory release candidates;
- beta: `0.12.0b1` for feature-complete candidates still under validation;
- release candidate: `0.12.0rc1` when no further product changes are expected;
- development: `0.12.0.dev1` for non-release automation or private snapshots.

Increment the numeric suffix for each artifact. A stable release drops the
suffix; it does not increment past the target (`0.12.0rc2` becomes `0.12.0`).
Do not publish local-version identifiers such as `+windows` to the normal PyPI
release channel. Platform and personal-build identity belongs in the artifact
manifest, not in the public package version.

## Versions that remain independent

The repository version is not a substitute for compatibility versions embedded
in data or protocols. Replay, capture, trace, ABI, save, and network protocol
versions must be bumped according to their own compatibility rules in the same
change that alters their wire or file contract. Mention those bumps in release
notes. They do not need to numerically match the package version.

CrimsonVR personal builds that are not public repository releases use the source
commit, workflow run, platform, and artifact hash as their identity. Creating a
private Quest or PCVR build does not by itself consume a package version.

## Release mechanics

1. Determine the highest required increment from the frozen release scope and
   record the rationale in Release Preparation.
2. Run `uv version --bump patch`, `minor`, or `major`; for a prerelease, set the
   exact PEP 440 version supported by the selected release workflow.
3. Run `uv lock` and verify `pyproject.toml` and the root package entry in
   `uv.lock` agree.
4. Build the wheel and sdist and inspect their filenames and metadata.
5. Commit the bump as `chore(release): bump version to <version>`.
6. Only after every go/no-go gate passes, create annotated tag `v<version>` from
   that exact clean commit and push it once.

Never reuse, move, or overwrite a published version or release tag. If a release
artifact is wrong, correct it and publish the next patch version.
