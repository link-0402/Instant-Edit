"""Standalone safety regression for the Blender add-on's owned cache."""

import importlib.util
import json
from pathlib import Path
import tempfile
import uuid


def _load_cache():
    path = Path(__file__).resolve().parents[1] / "instant_edit" / "cache.py"
    spec = importlib.util.spec_from_file_location("xiv_instant_edit_cache_regression", path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


def run() -> None:
    cache = _load_cache()
    with tempfile.TemporaryDirectory(prefix="xiv-ie-cache-test-") as temporary:
        base = Path(temporary)
        root = cache.configure_cache(base, True)
        assert root == (base / cache.CACHE_FOLDER).resolve()
        assert json.loads((root / ".instant-edit-cache.json").read_text("utf-8"))["schema"] == cache.CACHE_SCHEMA

        handoff = base / "handoff" / uuid.uuid4().hex
        preview = handoff / "preview"
        preview.mkdir(parents=True)
        model = handoff / "source.mdl"
        model.write_bytes(b"\x06\x00\x00\x01model")
        (preview / "materials.json").write_text('{"materials":[]}', encoding="utf-8")
        (preview / "texture.rgba").write_bytes(b"rgba")

        staged = cache.stage_import({
            "filePath": str(model),
            "previewManifestPath": str(preview / "materials.json"),
        })
        staged_model = Path(staged["filePath"])
        job = Path(staged["cacheJobDirectory"])
        assert staged_model.read_bytes() == model.read_bytes()
        assert Path(staged["previewManifestPath"]).is_file()
        assert job.parent == root / "imports"

        foreign = base / "foreign"
        foreign.mkdir()
        (foreign / "keep.txt").write_text("keep", encoding="utf-8")
        assert not cache.remove_job(foreign)
        assert (foreign / "keep.txt").is_file()

        export_job = cache.create_job("exports")
        (export_job / "result.mdl").write_bytes(b"result")
        cache.configure_cache(base, False)
        assert cache.finish_job(job) and cache.finish_job(export_job)
        removed, removed_bytes = cache.clean_cache()
        assert removed == 2
        assert removed_bytes >= len(model.read_bytes()) + len(b"result")
        assert not job.exists() and not export_job.exists()
        assert (foreign / "keep.txt").is_file()

    print("[PASS] cache staging, ownership boundaries, and cleanup")


if __name__ == "__main__":
    run()
