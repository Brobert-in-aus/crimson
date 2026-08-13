"""Create a deterministic CrimsonVR asset pack from locally baked assets.

The output is a ZIP container with a ``.pack`` extension. It contains only
files derived on the user's machine from their Crimsonland Classic install;
it must never be committed or uploaded as a release artifact.

Usage:
    uv run crimson-vr/tools/pack_assets.py \
        crimson-vr/godot/assets crimson-assets.pack
"""

from __future__ import annotations

import hashlib
import json
import sys
import zipfile
from pathlib import Path

SCHEMA_VERSION = 1
PAYLOAD_SUFFIXES = {".json", ".ogg", ".png"}
REQUIRED = (
    Path("sprites/sprite_manifest.json"),
    Path("audio/audio_manifest.json"),
)


def _files(root: Path) -> list[Path]:
    files = (
        path for path in root.rglob("*")
        if path.is_file()
        and path.name != "crimson-assets.json"
        and path.suffix.lower() in PAYLOAD_SUFFIXES
    )
    # pathlib sorts WindowsPath case-insensitively, which made the aggregate
    # hash host-dependent while the C# consumer uses ordinal POSIX names.
    return sorted(files, key=lambda path: path.relative_to(root).as_posix())


def build_pack(asset_root: Path, output: Path) -> None:
    missing = [path.as_posix() for path in REQUIRED if not (asset_root / path).is_file()]
    if missing:
        raise SystemExit("incomplete baked assets; missing: " + ", ".join(missing))

    files = _files(asset_root)
    hashes = {
        path.relative_to(asset_root).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in files
    }
    content_hash = hashlib.sha256(
        "".join(f"{name}\0{digest}\n" for name, digest in hashes.items()).encode(),
    ).hexdigest()
    marker = json.dumps(
        {
            "schema_version": SCHEMA_VERSION,
            "content_sha256": content_hash,
            "files": hashes,
        },
        indent=2,
        sort_keys=True,
    ).encode()

    output.parent.mkdir(parents=True, exist_ok=True)
    timestamp = (1980, 1, 1, 0, 0, 0)
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as pack:
        marker_info = zipfile.ZipInfo("crimson-assets.json", timestamp)
        marker_info.compress_type = zipfile.ZIP_DEFLATED
        pack.writestr(marker_info, marker)
        for path in files:
            info = zipfile.ZipInfo(path.relative_to(asset_root).as_posix(), timestamp)
            info.compress_type = zipfile.ZIP_DEFLATED
            pack.writestr(info, path.read_bytes())

    print(f"wrote {output} ({len(files)} asset files, {content_hash[:12]})")


def main() -> None:
    if len(sys.argv) not in (2, 3):
        raise SystemExit("usage: pack_assets.py ASSET_ROOT [OUTPUT.pack]")
    root = Path(sys.argv[1])
    output = Path(sys.argv[2]) if len(sys.argv) == 3 else Path("crimson-assets.pack")
    build_pack(root, output)


if __name__ == "__main__":
    main()
