from __future__ import annotations

import hashlib
import json
import sys
import zipfile
from pathlib import Path

import pytest

TOOLS = Path(__file__).resolve().parents[1] / "tools"
sys.path.insert(0, str(TOOLS))

import pack_assets


def _asset_tree(root: Path) -> None:
    (root / "sprites").mkdir(parents=True)
    (root / "audio").mkdir()
    (root / "sprites" / "sprite_manifest.json").write_text("{}")
    (root / "audio" / "audio_manifest.json").write_text("{}")
    (root / "sprites" / "sample.png").write_bytes(b"not copyrighted test pixels")
    (root / "sprites" / "UPPER.png").write_bytes(b"case-order sentinel")
    (root / "sprites" / "sample.png.import").write_text("machine-local Godot cache")


def test_pack_is_deterministic_and_self_consistent(tmp_path: Path) -> None:
    assets = tmp_path / "assets"
    _asset_tree(assets)
    first = tmp_path / "first.pack"
    second = tmp_path / "second.pack"

    pack_assets.build_pack(assets, first)
    pack_assets.build_pack(assets, second)

    assert first.read_bytes() == second.read_bytes()
    with zipfile.ZipFile(first) as archive:
        marker = json.loads(archive.read("crimson-assets.json"))
        assert marker["schema_version"] == 1
        assert "sprites/sample.png.import" not in archive.namelist()
        for name, expected in marker["files"].items():
            assert hashlib.sha256(archive.read(name)).hexdigest() == expected
        combined = "".join(
            f"{name}\0{digest}\n" for name, digest in sorted(marker["files"].items())
        ).encode()
        assert hashlib.sha256(combined).hexdigest() == marker["content_sha256"]


def test_pack_refuses_incomplete_bake(tmp_path: Path) -> None:
    with pytest.raises(SystemExit, match="audio/audio_manifest.json"):
        pack_assets.build_pack(tmp_path, tmp_path / "bad.pack")
