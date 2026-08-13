"""Create stable PCVR archives with portable permissions."""

from __future__ import annotations

import argparse
import gzip
import tarfile
import zipfile
from pathlib import Path


def files_under(source: Path) -> list[Path]:
    return sorted((path for path in source.rglob("*") if path.is_file()), key=lambda p: p.as_posix())


def build_windows_zip(source: Path, output: Path) -> None:
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in files_under(source):
            relative = path.relative_to(source).as_posix()
            info = zipfile.ZipInfo(relative, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            archive.writestr(info, path.read_bytes(), compresslevel=9)


def build_linux_tar(source: Path, output: Path) -> None:
    # Windows-created ZIPs lose Unix execute bits. A tar.gz with explicit modes
    # makes the exported Linux binary runnable immediately after extraction.
    with (
        output.open("wb") as raw,
        gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0) as compressed,
        tarfile.open(fileobj=compressed, mode="w", format=tarfile.PAX_FORMAT) as archive,
    ):
        for path in files_under(source):
            relative = path.relative_to(source).as_posix()
            info = archive.gettarinfo(str(path), arcname=relative)
            info.uid = info.gid = 0
            info.uname = info.gname = ""
            info.mtime = 0
            info.mode = 0o755 if relative == "CrimsonVR.x86_64" else 0o644
            with path.open("rb") as payload:
                archive.addfile(info, payload)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--platform", choices=("Windows", "Linux"), required=True)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    source = args.source.resolve()
    output = args.output.resolve()
    if not source.is_dir():
        parser.error(f"source directory not found: {source}")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.unlink(missing_ok=True)
    if args.platform == "Windows":
        build_windows_zip(source, output)
    else:
        build_linux_tar(source, output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
