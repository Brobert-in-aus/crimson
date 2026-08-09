from __future__ import annotations

import subprocess
import sys
from pathlib import Path

import pytest

TOOLS = Path(__file__).resolve().parents[1] / "tools"
sys.path.insert(0, str(TOOLS))

import prepare_assets  # noqa: E402


def _adb(stdout: str):
    def run(*_args, **_kwargs):
        return subprocess.CompletedProcess([], 0, stdout=stdout, stderr="")

    return run


def test_choose_device_selects_only_ready_device(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(subprocess, "run", _adb("List of devices attached\nquest:5555\tdevice\n"))
    assert prepare_assets.choose_device("adb", None) == "quest:5555"


def test_choose_device_explains_unauthorized(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(subprocess, "run", _adb("List of devices attached\nABC\tunauthorized\n"))
    with pytest.raises(SystemExit, match="accept USB debugging"):
        prepare_assets.choose_device("adb", None)


def test_choose_device_requires_serial_for_multiple(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(
        subprocess,
        "run",
        _adb("List of devices attached\nquest:5555\tdevice\nphone\tdevice\n"),
    )
    with pytest.raises(SystemExit, match="--device"):
        prepare_assets.choose_device("adb", None)


def test_validate_classic_rejects_hd_remake(tmp_path: Path) -> None:
    (tmp_path / "data.pak").write_bytes(b"")
    with pytest.raises(SystemExit, match="Extras"):
        prepare_assets.validate_classic(tmp_path)


def test_discover_classic_finds_only_valid_candidate(tmp_path: Path) -> None:
    hd = tmp_path / "Crimsonland"
    hd.mkdir()
    (hd / "data.pak").write_bytes(b"")
    classic = tmp_path / "Crimsonland Classic"
    classic.mkdir()
    (classic / "crimson.paq").write_bytes(b"")
    (classic / "sfx.paq").write_bytes(b"")

    assert prepare_assets.discover_classic((hd, classic)) == classic.resolve()


def test_discover_classic_explains_gog_extras_when_missing(tmp_path: Path) -> None:
    with pytest.raises(SystemExit, match="Extras"):
        prepare_assets.discover_classic((tmp_path / "missing",))


def test_send_to_pcvr_uses_godot_user_directory(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    pack = tmp_path / "source.pack"
    pack.write_bytes(b"pack")
    appdata = tmp_path / "AppData" / "Roaming"
    monkeypatch.setattr(sys, "platform", "win32")
    monkeypatch.setenv("APPDATA", str(appdata))

    inbox = prepare_assets.send_to_pcvr(pack)

    assert inbox == appdata / "Godot" / "app_userdata" / "CrimsonVR" / "crimson-assets.pack"
    assert inbox.read_bytes() == b"pack"
