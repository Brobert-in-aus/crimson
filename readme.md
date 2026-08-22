# CrimsonVR

CrimsonVR is an independent OpenXR VR port built on the
[banteg/crimson](https://github.com/banteg/crimson) reimplementation of
Crimsonland Classic. This fork is maintained separately and is not an official
part of, affiliated with, or endorsed by that upstream project, its
maintainers, 10tons, or the Crimsonland rights holders.

It brings the deterministic Crimson simulation to standalone Quest and PCVR,
with Tabletop and Cabinet layouts, controller and optical-hand input, spatial
menus, VR onboarding, and PCVR-to-Quest multiplayer.

## Before you start

CrimsonVR does not publish game assets or ready-made public binaries. Each
player builds an asset-free personal package in their own GitHub fork and
supplies assets locally from their own GOG copy of **Crimsonland Classic
1.9.93**. The 2014 HD remake is not compatible.

You need:

- a GitHub account and your own fork of this repository;
- a Windows PC with your GOG Crimsonland Classic installation;
- [uv](https://docs.astral.sh/uv/getting-started/installation/) for the local
  asset-preparation helper; and
- either a Quest in Developer Mode or a working PCVR OpenXR runtime.

Install `uv` once from Command Prompt or Windows Terminal:

```bat
winget install --id=astral-sh.uv -e
```

Fork this repository on GitHub, then clone your fork or download and extract
its source ZIP. Run the setup files below from that source folder. No game
assets are uploaded by these helpers.

## Quest installation

### 1. Build your personal Quest APK

In your fork on GitHub:

1. Open **Actions**.
2. Select **Quest personal build**.
3. Choose **Run workflow**.
4. Set `publish_artifact` to `true`, then run it.
5. When the run finishes, download the `CrimsonVR-Quest-*` artifact within one
   day and extract all three files:
   - `CrimsonVR.quest.apk`
   - `crimsonvr-ci.keystore`
   - `QUEST-PERSONAL-BUILD.txt`

Keep the keystore private. Android needs the same signing key for in-place
updates. Losing it does not expose game assets, but the next differently signed
APK will require an uninstall, which erases CrimsonVR app data.

### 2. Connect the Quest

Enable Developer Mode, connect the headset to the PC by USB, and approve the
USB debugging prompt inside the headset. Ensure Android `adb` is available on
`PATH`; Meta Quest Developer Hub or Android platform-tools can provide it.

### 3. Install the APK and your assets

From the repository root, run:

```bat
crimson-vr\setup_quest.bat "C:\path\to\CrimsonVR.quest.apk"
```

The helper finds the usual GOG Classic installation, validates it, builds a
local asset pack, installs the asset-free APK, transfers the pack to the
headset, and leaves the app stopped for a normal first launch.

For a nonstandard Classic location, add it as the second argument:

```bat
crimson-vr\setup_quest.bat "C:\path\to\CrimsonVR.quest.apk" "D:\Games\Crimsonland"
```

If more than one Android device is connected, add the intended ADB serial as
the third argument:

```bat
crimson-vr\setup_quest.bat "C:\path\to\CrimsonVR.quest.apk" "D:\Games\Crimsonland" "SERIAL"
```

After the helper reports success, put on the headset and launch CrimsonVR from
the app library. Do not use an ADB launch command for the first run: Quest's
controller and permission interstitials can make the onboarding appear to have
been skipped.

For updates, keep using the same fork and signing key. Imported assets survive
an in-place APK update; an uninstall removes settings, scores, replays, and
imported assets.

## PCVR installation

### 1. Build your personal PCVR package

In your fork on GitHub:

1. Open **Actions**.
2. Select **PCVR personal build**.
3. Choose **Run workflow**.
4. Set `publish_artifact` to `true`, then run it.
5. Download the `CrimsonVR-PCVR-*` artifact within one day.
6. Choose `CrimsonVR-PCVR-Windows.zip` or the Linux `.tar.gz` package and
   extract the entire archive.

Do not move the executable away from its PCK, `data_*`, `native`, or OpenXR
library files.

### 2. Prepare your assets on Windows

From the repository root, run:

```bat
crimson-vr\setup_pcvr.bat
```

For a nonstandard Classic location:

```bat
crimson-vr\setup_pcvr.bat "D:\Games\Crimsonland"
```

The helper writes the local pack to CrimsonVR's per-user first-run inbox. It
does not upload the pack or copy assets into the GitHub checkout.

Linux users can run the equivalent helper directly:

```bash
uv run python crimson-vr/tools/prepare_assets.py --pcvr
```

### 3. Launch PCVR

Start SteamVR, VDXR, or another OpenXR runtime, then run `CrimsonVR.exe` from
the fully extracted Windows package. On Linux, launch the executable from the
extracted tar archive. CrimsonVR imports the prepared pack before starting XR.

Package updates reuse the installed per-user assets. Keep the generated
`crimson-assets.pack` somewhere safe if you want quick recovery after removing
application data.

## Troubleshooting

### Classic installation is not found

Install **Crimsonland Classic** from the Extras section in GOG Galaxy, or pass
its directory explicitly to the appropriate `.bat` file. A directory containing
only the HD remake will be rejected.

### `uv` is not found

Close and reopen Command Prompt after installing `uv`, then run the setup file
again. The helpers also check `%USERPROFILE%\.local\bin\uv.exe` and an existing
repository `.venv`.

### `adb` is not found or the Quest is unavailable

Install Android platform-tools, reconnect the Quest by USB, approve debugging
inside the headset, and check `adb devices`. If several devices are listed,
pass the desired serial as the third `setup_quest.bat` argument.

### PCVR opens only on the monitor

Confirm that the intended OpenXR runtime is active before launching CrimsonVR.
For Virtual Desktop, connect the headset first and select VDXR where applicable.

### Asset preparation fails partway through

Correct the reported path or dependency problem and run the same setup file
again. Asset installation is integrity-checked and atomic, so a failed
replacement does not overwrite the last working imported asset set.

## Feedback and support

- Use [GitHub Discussions](https://github.com/Brobert-in-aus/CrimsonVR/discussions)
  for questions, playtest feedback, ideas, and help from the community.
- [Report a reproducible bug](https://github.com/Brobert-in-aus/CrimsonVR/issues/new?template=bug_report.yml)
  with your platform, build version, device/runtime details, and reproduction
  steps.
- [Request a feature](https://github.com/Brobert-in-aus/CrimsonVR/issues/new?template=feature_request.yml)
  when you have a concrete improvement to propose.

Before posting logs, screenshots, or recordings, remove personal information.
Never upload Crimsonland assets, PAQ/PAK files, generated asset packs, personal
APK/PCVR packages, or signing keys.

## More documentation

- [Quest personal-build details](crimson-vr/notes/quest-ci.md)
- [Quest playtester handoff](crimson-vr/notes/quest-playtest.md)
- [PCVR personal-build details](crimson-vr/notes/pcvr-ci.md)
- [Asset import and recovery](crimson-vr/notes/asset-import.md)
- [Quest release checklist](crimson-vr/notes/quest-release-checklist.md)
- [PCVR release checklist](crimson-vr/notes/pcvr-release-checklist.md)
- [Godot Quest controller models](crimson-vr/notes/godot-quest-controller-models.md)
- [Release evidence](crimson-vr/notes/release-evidence-0.11.0.md)

## Distribution and privacy

The supported publication surface is source plus asset-free, user-owned CI
builds. Do not upload or share generated asset packs, PAQ/PAK files, extracted
art or audio, personal bundled-assets APKs, or signing keys. The local setup
helpers process files on the player's PC and transfer Quest data only to the
connected headset.

## Legal

CrimsonVR is an independent port and is not affiliated with or endorsed by the
upstream Crimson project, its maintainers, 10tons, or the Crimsonland rights
holders. The upstream project documents its own asset permissions; this fork
does not claim those permissions transfer to CrimsonVR distribution. Each
player must supply their own legally obtained Crimsonland Classic assets.
