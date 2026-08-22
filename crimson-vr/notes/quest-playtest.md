# Quest playtester handoff

This is the operated path for a CrimsonVR playtest. It keeps original
Crimsonland assets off GitHub and makes each participant responsible for their
own fork-built APK and locally owned game data.

## Boundary first

- Use GitHub's **Fork** button so the source history and ownership of the
  personal CI run remain explicit.
- Do not upload or share `crimson-assets.pack`, PAQ/PAK files, extracted art, or
  audio.
- The approved release surface is source plus user-owned CI builds. Each tester
  creates their own personal APK and supplies their own Classic assets; the
  organiser does not distribute a maintainer-built or bundled-assets APK.

## What each tester needs

- A GitHub account and a fork containing the approved
  CrimsonVR revision, with `.github/workflows/quest.yml` on its default branch.
- A Quest in Developer Mode, a Windows PC with `adb` available, and an approved
  USB debugging connection.
- Their own GOG **Crimsonland Classic** installation from Extras. The 2014 HD
  remake is not compatible.
- `uv` for local asset preparation (`winget install --id=astral-sh.uv -e`). Godot,
  Zig, .NET, Java, and the Android SDK are supplied by GitHub Actions.

## Create the fork

The organiser supplies the approved public repository URL and revision. The
tester creates a GitHub fork, clones it, and checks out the approved revision:

```powershell
gh repo fork PUBLIC_REPOSITORY
gh repo clone OWNER/REPOSITORY crimsonvr-playtest
Set-Location crimsonvr-playtest
git checkout APPROVED_REVISION
```

Before continuing, open the fork in a browser and confirm:

- the fork belongs to the tester;
- `.github/workflows/quest.yml` exists at the approved revision; and
- the Actions tab shows **Quest personal build**.

If the approved revision is not the fork's default branch, select that branch in
the workflow dispatcher. Running a stale default branch is not sufficient.

## Build

This playtester path intentionally uses the asset-free CI contract. The local
`build_quest.ps1` default is instead a non-shareable bundled-assets APK for the
builder's own headsets; do not substitute or upload that artifact.

1. In the fork, enable GitHub Actions if prompted.
2. Confirm **Settings → Actions → General → Workflow permissions** allows read
   repository contents. The workflow requests no write permission.
3. Open **Actions → Quest personal build → Run workflow** on the default branch.
4. Set `publish_artifact` to `true` and start the run.
5. Download `CrimsonVR-Quest-*` within one day and extract all three files:
   `CrimsonVR.quest.apk`, `crimsonvr-ci.keystore`, and
   `QUEST-PERSONAL-BUILD.txt`.
6. Compare the APK SHA-256 with `apk_sha256` in the manifest:

   ```powershell
   (Get-FileHash .\CrimsonVR.quest.apk -Algorithm SHA256).Hash.ToLowerInvariant()
   ```

7. Preserve the keystore. For later in-place updates, configure it in that same
   fork:

   ```powershell
   $bytes = [IO.File]::ReadAllBytes('.\crimsonvr-ci.keystore')
   [Convert]::ToBase64String($bytes) | gh secret set QUEST_KEYSTORE_BASE64 --repo OWNER/REPOSITORY
   ```

Never post the keystore or secret. Losing it does not expose game assets, but it
forces an uninstall before the next differently signed APK and therefore erases
CrimsonVR app data.

## Install and supply assets

From a clean checkout of the same approved revision, the recommended Windows
path is:

```bat
crimson-vr\setup_quest.bat ".\CrimsonVR.quest.apk"
```

The helper validates the APK, finds Crimsonland Classic (or accepts its
directory as the second argument),
installs the asset-free APK, creates the pack locally, copies it only to the
connected Quest's app-owned inbox, and leaves the app stopped. If more than one
Android device is connected, pass the Classic directory as the second argument
and the ADB serial as the third. The underlying `prepare_assets.ps1` and Python
entry point remain available for advanced use.

Launch CrimsonVR manually from the headset library. A correct clean setup first
imports the local pack and then shows the first-run guide. Do not use an ADB
launch command: the Quest controllers/permissions interstitial can make it look
as though first launch was skipped.

## Test and report

Release exports hide the developer **Debug overlays** control. Use the external
release checklist below; developer builds retain the in-headset checklist and FX
tools when deeper diagnostics are required. Testers should return:

- headset model and system software version;
- controller or optical-hand input;
- APK SHA-256 and the workflow run URL from the manifest;
- checklist PASS/FAIL results and reproduction steps for each failure;
- screenshots or video for spatial/layout issues, with no personal information
  or copyrighted asset pack attached.

For release-candidate sign-off, complete the expanded
[Quest headset checklist](quest-release-checklist.md); the in-headset list is a
portable summary, not the complete evidence record.

Before installing a build signed with a different key, export any results that
matter: uninstalling `xyz.crimsonvr.app` erases settings, scores, imported
assets, checklist state, and replays.
