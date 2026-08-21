# Quest playtester handoff

This is the operated path for a small private CrimsonVR playtest when public
binary redistribution has not been approved. It keeps original Crimsonland
assets off GitHub and makes each participant responsible for their own private
build and locally owned game data.

## Boundary first

- Do not use GitHub's **Fork** button: a fork of a public repository is public.
- Do not upload or share `crimson-assets.pack`, PAQ/PAK files, extracted art, or
  audio.
- A private workflow is not itself a licence grant. The upstream repository has
  no detected licence file, so the organiser must confirm that the selected
  source-copy and personal-build arrangement is acceptable. If APK distribution
  is not approved, each tester must build their own APK using this guide.
- Do not add testers to a maintainer artifact repository solely to bypass an
  unresolved binary-distribution decision.

## What each tester needs

- A GitHub account and a private standalone copy containing the approved
  CrimsonVR revision, with `.github/workflows/quest.yml` on its default branch.
- A Quest in Developer Mode, a Windows PC with `adb` available, and an approved
  USB debugging connection.
- Their own GOG **Crimsonland Classic** installation from Extras. The 2014 HD
  remake is not compatible.
- `uv` for local asset preparation (`winget install --id=astral-sh.uv -e`). Godot,
  Zig, .NET, Java, and the Android SDK are supplied by GitHub Actions.

## Create the private standalone copy

The organiser supplies the approved public repository URL and branch name. The
tester runs the following with GitHub CLI, substituting their account and the
approved branch. This creates a new private repository; it is not a GitHub fork:

```powershell
git clone --single-branch --branch APPROVED_BRANCH PUBLIC_REPOSITORY_URL crimsonvr-playtest
Set-Location crimsonvr-playtest
gh repo create OWNER/crimsonvr-playtest --private --source=. --remote=playtest --push
gh repo edit OWNER/crimsonvr-playtest --default-branch APPROVED_BRANCH
```

Before continuing, open the private repository in a browser and confirm:

- its visibility says **Private**;
- `APPROVED_BRANCH` is the default branch;
- `.github/workflows/quest.yml` exists on that branch; and
- the Actions tab shows **Quest personal build**.

GitHub Importer can also copy a publicly accessible Git repository and its
history, but the default-branch checks above are still required. The current
maintainer public fork's `master` branch does not contain the Quest workflow, so
importing it and running the stale default branch is not sufficient.

## Build

1. In the private standalone repository, enable GitHub Actions if prompted.
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
   private repository:

   ```powershell
   $bytes = [IO.File]::ReadAllBytes('.\crimsonvr-ci.keystore')
   [Convert]::ToBase64String($bytes) | gh secret set QUEST_KEYSTORE_BASE64 --repo OWNER/REPOSITORY
   ```

Never post the keystore or secret. Losing it does not expose game assets, but it
forces an uninstall before the next differently signed APK and therefore erases
CrimsonVR app data.

## Install and supply assets

From a clean checkout of the same approved revision:

```powershell
.\crimson-vr\tools\prepare_assets.ps1 -Quest -Apk '.\CrimsonVR.quest.apk'
```

The helper validates the APK, finds Crimsonland Classic (or accepts `-GameDir`),
installs the asset-free APK, creates the pack locally, copies it only to the
connected Quest's app-owned inbox, and leaves the app stopped. If more than one
Android device is connected, add `-Device SERIAL`.

Launch CrimsonVR manually from the headset library. A correct clean setup first
imports the local pack and then shows the first-run guide. Do not use an ADB
launch command: the Quest controllers/permissions interstitial can make it look
as though first launch was skipped.

## Test and report

Enable **Options → VR Settings → Display → Debug overlays** to open the current
in-headset checklist. Poking a row cycles untested → PASS → FAIL. Testers should
return:

- headset model and system software version;
- controller or optical-hand input;
- APK SHA-256 and the workflow run URL from the manifest;
- checklist PASS/FAIL results and reproduction steps for each failure;
- screenshots or video for spatial/layout issues, with no personal information
  or copyrighted asset pack attached.

Before installing a build signed with a different key, export any results that
matter: uninstalling `xyz.crimsonvr.app` erases settings, scores, imported
assets, checklist state, and replays.
