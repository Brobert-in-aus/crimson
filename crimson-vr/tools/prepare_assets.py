r"""Validate, extract, bake, pack, and optionally transfer owned game assets.

Development-checkout implementation of the future standalone helper:

    python crimson-vr/tools/prepare_assets.py "C:\Games\Crimsonland Classic"
    python crimson-vr/tools/prepare_assets.py GAME_DIR --quest
    python crimson-vr/tools/prepare_assets.py GAME_DIR --stage-project crimson-vr/godot/assets
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import tempfile
from collections.abc import Iterable
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO_ROOT / "src"))
sys.path.insert(0, str(REPO_ROOT))

from pack_assets import build_pack


def validate_classic(game_dir: Path) -> None:
    if not game_dir.is_dir():
        raise SystemExit(f"game directory does not exist: {game_dir}")
    required = [game_dir / "crimson.paq", game_dir / "sfx.paq"]
    if all(path.is_file() for path in required):
        return
    if (game_dir / "data.pak").is_file():
        raise SystemExit(
            "This is the 2014 HD remake, not Crimsonland Classic. In GOG Galaxy, "
            "open Crimsonland -> Extras and install 'Crimsonland Classic'.",
        )
    raise SystemExit(
        "This folder is not Crimsonland Classic 1.9.93; crimson.paq and sfx.paq are required.",
    )


def default_classic_candidates() -> list[Path]:
    candidates: list[Path] = []
    if sys.platform == "win32":
        roots = [
            os.environ.get("ProgramFiles"),
            os.environ.get("ProgramFiles(x86)"),
            "C:/GOG Games",
        ]
        for value in roots:
            if not value:
                continue
            root = Path(value)
            candidates.extend((root / "Crimsonland Classic", root / "GOG Galaxy" / "Games" / "Crimsonland Classic"))
    else:
        candidates.extend(
            (
                Path.home() / "GOG Games" / "Crimsonland Classic",
                Path.home() / "Games" / "Crimsonland Classic",
            ),
        )
    return candidates


def discover_classic(candidates: Iterable[Path] | None = None) -> Path:
    found: list[Path] = []
    seen: set[Path] = set()
    for candidate in candidates if candidates is not None else default_classic_candidates():
        resolved = candidate.resolve()
        if resolved in seen:
            continue
        seen.add(resolved)
        if (resolved / "crimson.paq").is_file() and (resolved / "sfx.paq").is_file():
            found.append(resolved)
    if len(found) == 1:
        print(f"found Crimsonland Classic: {found[0]}")
        return found[0]
    if len(found) > 1:
        choices = "\n  ".join(str(path) for path in found)
        raise SystemExit(f"Multiple Classic installations found; pass the desired GAME_DIR:\n  {choices}")
    raise SystemExit(
        "Crimsonland Classic was not found in the usual GOG locations. Pass GAME_DIR explicitly. "
        "If only the HD remake is installed, use GOG Galaxy -> Crimsonland -> Extras -> "
        "Crimsonland Classic.",
    )


def bake(game_dir: Path, baked: Path) -> None:
    # Lazy so `--help` and Classic-vs-HD diagnostics work even before the
    # checkout's Python project has been installed (`uv sync`).
    try:
        from crimson.cli.root import cmd_extract
    except ImportError as error:
        raise SystemExit(
            "The Crimson extractor is not installed. Run this command through "
            "the project environment (`uv run python ...`) or use the future "
            "standalone helper.",
        ) from error

    validate_classic(game_dir)
    extracted = baked.parent / "extracted"
    cmd_extract(game_dir, extracted)
    source_music = game_dir / "music"
    if source_music.is_dir():
        shutil.copytree(source_music, extracted / "music")

    subprocess.run(
        [sys.executable, str(Path(__file__).with_name("bake_assets.py")),
         str(extracted), str(baked / "sprites")],
        cwd=REPO_ROOT,
        check=True,
    )


def validate_baked_assets(baked: Path) -> None:
    required = (
        baked / "sprites" / "sprite_manifest.json",
        baked / "audio" / "audio_manifest.json",
    )
    missing = [str(path) for path in required if not path.is_file()]
    if missing:
        raise RuntimeError("baked asset set is incomplete; missing: " + ", ".join(missing))


def prepare(game_dir: Path, output: Path) -> None:
    with tempfile.TemporaryDirectory(prefix="crimsonvr-assets-") as temporary:
        root = Path(temporary)
        baked = root / "baked"
        bake(game_dir, baked)
        validate_baked_assets(baked)
        build_pack(baked, output)


def stage_project_assets(game_dir: Path, destination: Path) -> None:
    """Atomically replace the ignored res://assets development fallback."""
    destination = destination.resolve()
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".crimsonvr-assets-", dir=destination.parent) as temporary:
        root = Path(temporary)
        baked = root / "baked"
        bake(game_dir, baked)
        validate_baked_assets(baked)

        backup = root / "previous-assets"
        had_previous = destination.exists()
        if had_previous:
            destination.replace(backup)
        try:
            baked.replace(destination)
        except BaseException:
            if had_previous and backup.exists() and not destination.exists():
                backup.replace(destination)
            raise
    print(f"staged personal project assets: {destination}")


def connected_devices(adb: str) -> list[tuple[str, str]]:
    result = subprocess.run([adb, "devices"], capture_output=True, text=True, check=True)
    devices: list[tuple[str, str]] = []
    for line in result.stdout.splitlines()[1:]:
        fields = line.strip().split()
        if len(fields) >= 2:
            devices.append((fields[0], fields[1]))
    return devices


def choose_device(adb: str, requested: str | None) -> str:
    devices = connected_devices(adb)
    if requested:
        states = {serial: state for serial, state in devices}
        state = states.get(requested)
        if state != "device":
            raise SystemExit(f"Quest {requested!r} is {state or 'not connected'}.")
        return requested
    ready = [serial for serial, state in devices if state == "device"]
    blocked = [(serial, state) for serial, state in devices if state != "device"]
    if len(ready) == 1:
        return ready[0]
    if not ready and blocked:
        detail = ", ".join(f"{serial} ({state})" for serial, state in blocked)
        raise SystemExit(
            f"No authorized Quest is ready: {detail}. Put on the headset and accept USB debugging.",
        )
    if not ready:
        raise SystemExit("No Quest detected. Connect USB or wireless ADB and ensure developer mode is enabled.")
    raise SystemExit("Multiple Android devices are connected; choose one with --device SERIAL.")


def adb_run(adb: str, serial: str, *args: str, capture: bool = False) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [adb, "-s", serial, *args], capture_output=capture, text=True, check=True,
    )


def send_to_quest(pack: Path, adb: str, device: str | None, apk: Path | None) -> None:
    serial = choose_device(adb, device)
    print(f"using Quest {serial}")
    if apk is not None:
        if not apk.is_file():
            raise SystemExit(f"APK does not exist: {apk}")
        adb_run(adb, serial, "install", "-r", str(apk))

    package = "xyz.crimsonvr.app"
    installed = adb_run(adb, serial, "shell", "pm", "path", package, capture=True).stdout.strip()
    if not installed.startswith("package:"):
        raise SystemExit("CrimsonVR is not installed; pass the downloaded build with --apk APK_PATH.")
    inbox = f"/sdcard/Android/data/{package}/files"
    adb_run(adb, serial, "shell", "mkdir", "-p", inbox)
    adb_run(adb, serial, "push", str(pack), f"{inbox}/crimson-assets.pack")
    adb_run(adb, serial, "shell", "am", "force-stop", package)
    print("transferred to the CrimsonVR Quest inbox (the app was not launched)")


def pcvr_user_data_dir() -> Path:
    if sys.platform == "win32":
        appdata = os.environ.get("APPDATA")
        if not appdata:
            raise SystemExit("APPDATA is unavailable; pass --output and import the pack manually.")
        return Path(appdata) / "Godot" / "app_userdata" / "CrimsonVR"
    if sys.platform == "darwin":
        return Path.home() / "Library" / "Application Support" / "Godot" / "app_userdata" / "CrimsonVR"
    data = Path(os.environ.get("XDG_DATA_HOME", Path.home() / ".local" / "share"))
    return data / "godot" / "app_userdata" / "CrimsonVR"


def send_to_pcvr(pack: Path) -> Path:
    inbox = pcvr_user_data_dir() / "crimson-assets.pack"
    inbox.parent.mkdir(parents=True, exist_ok=True)
    if pack.resolve() != inbox.resolve():
        shutil.copy2(pack, inbox)
    print(f"prepared PCVR inbox: {inbox}")
    print("Launch CrimsonVR normally; it will import the pack before starting XR.")
    return inbox


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("game_dir", type=Path, nargs="?")
    parser.add_argument("--output", type=Path, default=Path("crimson-assets.pack"))
    parser.add_argument("--quest", action="store_true", help="transfer the completed pack with adb")
    parser.add_argument("--pcvr", action="store_true", help="place the pack in the PCVR first-run inbox")
    parser.add_argument("--adb", default="adb", help="adb executable (default: adb on PATH)")
    parser.add_argument("--device", help="ADB serial when more than one Android device is connected")
    parser.add_argument("--apk", type=Path, help="install/update this asset-free APK before transfer")
    parser.add_argument(
        "--stage-project",
        type=Path,
        metavar="ASSETS_DIR",
        help="atomically stage baked assets for a personal bundled project export",
    )
    args = parser.parse_args()
    if args.quest and args.pcvr:
        parser.error("choose either --quest or --pcvr")
    if args.apk and not args.quest:
        parser.error("--apk is only valid with --quest")
    if args.stage_project and (args.quest or args.pcvr or args.apk):
        parser.error("--stage-project cannot be combined with --quest, --pcvr, or --apk")
    game_dir = args.game_dir.resolve() if args.game_dir else discover_classic()
    if args.stage_project:
        stage_project_assets(game_dir, args.stage_project)
        return
    output = args.output.resolve()
    prepare(game_dir, output)
    if args.quest:
        send_to_quest(output, args.adb, args.device, args.apk.resolve() if args.apk else None)
    elif args.pcvr:
        send_to_pcvr(output)


if __name__ == "__main__":
    main()
