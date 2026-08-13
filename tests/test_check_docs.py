from __future__ import annotations

from pathlib import Path

from scripts.check_docs import normalize_repo_path


def test_normalize_repo_path_uses_forward_slashes() -> None:
    assert normalize_repo_path(r"guide\multiplayer\index.md") == "guide/multiplayer/index.md"
    assert normalize_repo_path(Path("guide") / "multiplayer" / "index.md") == "guide/multiplayer/index.md"
