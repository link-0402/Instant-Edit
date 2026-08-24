"""Model backup discovery and safe local file operations."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
import re
import shutil
import uuid


BACKUP_RE = re.compile(
    r"^(?P<original>.+\.(?:mdl|fbx))\.(?P<stamp>\d{8}T\d{6}\.\d{6}Z)\.bak$",
    re.IGNORECASE,
)
LEGACY_RE = re.compile(r"^(?P<original>.+\.(?:mdl|fbx))\.bak$", re.IGNORECASE)


@dataclass(frozen=True)
class BackupEntry:
    path: Path
    original_name: str
    created: datetime
    timestamped: bool


def target_folder(settings, context=None) -> tuple[Path | None, str]:
    """Return the active Quick Export folder, or the Simple Export folder."""
    if context is not None:
        try:
            from .instant_edit.context import ContextValidationError
            from .instant_edit.ops import export_destination_context

            ref = export_destination_context(context)
            target = Path(ref.target_file_path).expanduser().resolve()
            if target.parent.is_dir():
                return target.parent, "Quick Export target"
        except (ContextValidationError, OSError, ValueError):
            pass

    value = getattr(settings, "export_directory", "")
    if not value:
        return None, "Simple Export folder"
    try:
        folder = Path(value).expanduser().resolve()
    except OSError:
        return None, "Simple Export folder"
    return (folder if folder.is_dir() else None), "Simple Export folder"


def parse_backup(path: Path) -> BackupEntry | None:
    if path.is_symlink() or not path.is_file() or path.name.startswith("."):
        return None
    match = BACKUP_RE.match(path.name)
    if match:
        try:
            created = datetime.strptime(match.group("stamp"), "%Y%m%dT%H%M%S.%fZ").replace(tzinfo=timezone.utc)
        except ValueError:
            return None
        return BackupEntry(path, match.group("original"), created, True)
    match = LEGACY_RE.match(path.name)
    if not match:
        return None
    try:
        created = datetime.fromtimestamp(path.stat().st_mtime, timezone.utc)
    except OSError:
        return None
    return BackupEntry(path, match.group("original"), created, False)


def list_backups(folder: Path | None) -> list[BackupEntry]:
    if folder is None or not folder.is_dir():
        return []
    entries = []
    for path in folder.iterdir():
        entry = parse_backup(path)
        if entry is not None:
            entries.append(entry)
    return sorted(entries, key=lambda item: (item.created, item.path.name), reverse=True)


def _safe_child(folder: Path, name: str) -> Path:
    folder = folder.resolve()
    path = (folder / name).resolve()
    if path.parent != folder:
        raise ValueError("backup path must be directly inside the target folder")
    return path


def backup_name(original_name: str, now: datetime | None = None) -> str:
    stamp = (now or datetime.now(timezone.utc)).astimezone(timezone.utc).strftime("%Y%m%dT%H%M%S.%fZ")
    return f"{original_name}.{stamp}.bak"


def create_backup(folder: Path, original_name: str) -> Path | None:
    """Copy an existing MDL/FBX before replacement; return None if absent."""
    if Path(original_name).name != original_name or Path(original_name).suffix.lower() not in {".mdl", ".fbx"}:
        raise ValueError("unsupported model filename")
    source = _safe_child(folder, original_name)
    if not source.is_file():
        return None
    for _ in range(8):
        destination = _safe_child(folder, backup_name(original_name))
        if not destination.exists():
            shutil.copyfile(source, destination)
            return destination
    raise FileExistsError("could not allocate a unique backup filename")


def restore_local(folder: Path, entry: BackupEntry) -> Path:
    entry_path = _safe_child(folder, entry.path.name)
    target = _safe_child(folder, entry.original_name)
    if not entry_path.is_file():
        raise FileNotFoundError(entry.path.name)
    if target.exists():
        create_backup(folder, entry.original_name)
    temporary = _safe_child(folder, f".xiv-ie-restore-{uuid.uuid4().hex}.tmp")
    try:
        shutil.copyfile(entry_path, temporary)
        temporary.replace(target)
    finally:
        if temporary.exists():
            temporary.unlink()
    return target


def clear_backups(folder: Path | None) -> int:
    removed = 0
    for entry in list_backups(folder):
        try:
            entry.path.unlink()
            removed += 1
        except OSError:
            pass
    return removed
