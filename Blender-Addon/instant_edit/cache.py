"""Owned cache directories and safe import handoff staging."""

from __future__ import annotations

from pathlib import Path
import json
import shutil
import tempfile
import threading
import time
import uuid


CACHE_SCHEMA = "instant-edit.cache"
CACHE_VERSION = 1
CACHE_FOLDER = "XIV-Instant-Edit"
STALE_SECONDS = 24 * 60 * 60
MAX_MODEL_BYTES = 512 * 1024 * 1024
MAX_PREVIEW_BYTES = 1024 * 1024 * 1024
MAX_PREVIEW_FILES = 2048

_lock = threading.RLock()
_base_directory = Path(tempfile.gettempdir())
_automatic_cleanup = True
_active_jobs: set[Path] = set()


def configure_cache(base_directory: str | Path, automatic_cleanup: bool) -> Path:
    """Update the thread-safe cache configuration without accessing Blender APIs."""
    global _base_directory, _automatic_cleanup
    base = Path(base_directory or tempfile.gettempdir()).expanduser().resolve()
    if base.exists() and not base.is_dir():
        raise ValueError("cache base path must be a directory")
    with _lock:
        _base_directory = base
        _automatic_cleanup = bool(automatic_cleanup)
    return ensure_cache_root()


def automatic_cleanup_enabled() -> bool:
    with _lock:
        return _automatic_cleanup


def cache_root() -> Path:
    with _lock:
        return (_base_directory / CACHE_FOLDER).resolve()


def _marker_path(root: Path) -> Path:
    return root / ".instant-edit-cache.json"


def ensure_cache_root() -> Path:
    root = cache_root()
    root.mkdir(parents=True, exist_ok=True)
    marker = _marker_path(root)
    if marker.exists():
        try:
            payload = json.loads(marker.read_text(encoding="utf-8"))
        except (OSError, UnicodeError, json.JSONDecodeError) as error:
            raise ValueError("cache ownership marker is unreadable") from error
        if payload != {"schema": CACHE_SCHEMA, "version": CACHE_VERSION}:
            raise ValueError("cache ownership marker is invalid")
    else:
        if any(root.iterdir()):
            raise ValueError("cache directory is not empty and has no XIV Instant Edit ownership marker")
        marker.write_text(
            json.dumps({"schema": CACHE_SCHEMA, "version": CACHE_VERSION}),
            encoding="utf-8",
        )
    for kind in ("imports", "exports"):
        (root / kind).mkdir(exist_ok=True)
    return root


def _uuid_name(value: str) -> str:
    try:
        parsed = uuid.UUID(value)
    except (ValueError, AttributeError) as error:
        raise ValueError("cache job id must be a UUID") from error
    return parsed.hex


def create_job(kind: str, job_id: str | None = None) -> Path:
    if kind not in {"imports", "exports"}:
        raise ValueError("unsupported cache job type")
    name = _uuid_name(job_id or uuid.uuid4().hex)
    parent = ensure_cache_root() / kind
    job = parent / name
    job.mkdir(parents=False, exist_ok=False)
    with _lock:
        _active_jobs.add(job.resolve())
    return job


def _owned_job(path: str | Path) -> Path | None:
    raw_candidate = Path(path)
    if raw_candidate.is_symlink():
        return None
    candidate = raw_candidate.resolve()
    root = cache_root()
    try:
        relative = candidate.relative_to(root)
    except ValueError:
        return None
    if len(relative.parts) != 2 or relative.parts[0] not in {"imports", "exports"}:
        return None
    try:
        _uuid_name(relative.parts[1])
    except ValueError:
        return None
    if not _marker_path(root).is_file() or (root / relative.parts[0]).is_symlink():
        return None
    return candidate


def remove_job(path: str | Path) -> bool:
    job = _owned_job(path)
    if job is None or not job.is_dir():
        return False
    with _lock:
        _active_jobs.discard(job)
    shutil.rmtree(job)
    return True


def finish_job(path: str | Path) -> bool:
    """Mark a job inactive and remove it immediately when auto-cleanup is enabled."""
    job = _owned_job(path)
    if job is None:
        return False
    with _lock:
        _active_jobs.discard(job)
        automatic = _automatic_cleanup
    return remove_job(job) if automatic else True


def _directory_size(path: Path) -> int:
    total = 0
    for item in path.rglob("*"):
        try:
            if item.is_file() and not item.is_symlink():
                total += item.stat().st_size
        except OSError:
            continue
    return total


def clean_cache(older_than_seconds: float | None = None) -> tuple[int, int]:
    """Remove only marked UUID job directories and return (jobs, bytes)."""
    root = ensure_cache_root()
    cutoff = None if older_than_seconds is None else time.time() - older_than_seconds
    removed = 0
    bytes_removed = 0
    for kind in ("imports", "exports"):
        for candidate in tuple((root / kind).iterdir()):
            job = _owned_job(candidate)
            if job is None or not job.is_dir():
                continue
            with _lock:
                if job in _active_jobs:
                    continue
            try:
                if cutoff is not None and job.stat().st_mtime > cutoff:
                    continue
                bytes_removed += _directory_size(job)
                shutil.rmtree(job)
                removed += 1
            except OSError:
                continue
    return removed, bytes_removed


def stage_import(data: dict) -> dict:
    """Copy a validated v1 handoff into the add-on-owned cache before queueing."""
    source_value = Path(data.get("filePath", ""))
    source_model = source_value.resolve()
    if source_value.is_symlink() or source_model.suffix.casefold() != ".mdl" or not source_model.is_file():
        raise ValueError("import file must be a regular .mdl file")
    model_size = source_model.stat().st_size
    if model_size <= 0 or model_size > MAX_MODEL_BYTES:
        raise ValueError("import model size is outside the supported range")

    job = create_job("imports")
    try:
        target_model = job / source_model.name
        shutil.copyfile(source_model, target_model)
        result = dict(data)
        result["filePath"] = str(target_model)
        result["cacheJobDirectory"] = str(job)

        manifest_value = data.get("previewManifestPath", "")
        if manifest_value:
            raw_manifest = Path(manifest_value)
            manifest = raw_manifest.resolve()
            preview_root = manifest.parent.resolve()
            if (
                manifest.name != "materials.json"
                or preview_root.name != "preview"
                or preview_root.parent.resolve() != source_model.parent.resolve()
                or raw_manifest.is_symlink()
                or not manifest.is_file()
            ):
                raise ValueError("preview manifest is not safely contained beside the model")

            target_preview = job / "preview"
            target_preview.mkdir()
            total_bytes = 0
            file_count = 0
            for source in preview_root.rglob("*"):
                if source.is_dir():
                    continue
                file_count += 1
                if file_count > MAX_PREVIEW_FILES or source.is_symlink():
                    raise ValueError("preview bundle contains too many files or a symbolic link")
                resolved = source.resolve()
                try:
                    relative = resolved.relative_to(preview_root)
                except ValueError as error:
                    raise ValueError("preview bundle escapes its source directory") from error
                if resolved.suffix.casefold() not in {".json", ".rgba"}:
                    raise ValueError("preview bundle contains an unsupported file")
                size = resolved.stat().st_size
                total_bytes += size
                if total_bytes > MAX_PREVIEW_BYTES:
                    raise ValueError("preview bundle exceeds 1 GiB")
                destination = target_preview / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(resolved, destination)
            target_manifest = target_preview / "materials.json"
            if not target_manifest.is_file():
                raise ValueError("preview bundle did not contain materials.json")
            result["previewManifestPath"] = str(target_manifest)
        return result
    except Exception:
        remove_job(job)
        raise
