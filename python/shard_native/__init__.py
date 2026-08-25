"""Python bindings for SHARD's forensic SQLite recovery engine.

    from shard_native import ShardDatabase

    with ShardDatabase("evidence.db") as db:
        print(db.header)
        for row in db.rows("users"):
            print(row)
        result = db.recover_to_file("recovered.db", carve_mode="tight")
        # "recovered.db" is a normal SQLite file — open it with the stdlib sqlite3 module.

Every call crosses into the native library once and returns a JSON envelope
(``{"ok": true, "data": ...}`` or ``{"ok": false, "error": "..."}``); this module unwraps that
into a plain Python value (dict/list/str/int/...) or raises :class:`ShardError`.
"""
from __future__ import annotations

import ctypes
import json
from typing import Any

from ._bindings import bind

_lib = bind()


class ShardError(Exception):
    """Raised for any failure reported by the native library (bad path, unknown table, etc.)."""


def _read_and_free(ptr: int | None) -> dict[str, Any]:
    if not ptr:
        raise ShardError("shard_native returned a null pointer")
    try:
        raw = ctypes.cast(ptr, ctypes.c_char_p).value
        return json.loads(raw.decode("utf-8")) if raw is not None else {}
    finally:
        _lib.shard_free_string(ptr)


def _call(fn, *args) -> Any:
    envelope = _read_and_free(fn(*args))
    if not envelope.get("ok"):
        raise ShardError(envelope.get("error") or "unknown shard_native error")
    return envelope.get("data")


class ShardDatabase:
    """An open forensic session against one SQLite evidence file.

    Use as a context manager, or call close() explicitly when done — the underlying handle
    is just bookkeeping on the native side (each call re-opens/re-parses the file), so there's
    no expensive resource held open between calls, but closing still frees that bookkeeping.
    """

    def __init__(self, path: str):
        self._handle: int | None = _call(_lib.shard_open, path.encode("utf-8"))["handle"]

    def close(self) -> None:
        if self._handle is not None:
            _lib.shard_close(ctypes.c_int64(self._handle))
            self._handle = None

    def __enter__(self) -> "ShardDatabase":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.close()

    def __del__(self) -> None:
        try:
            self.close()
        except Exception:
            pass

    def _require_handle(self) -> int:
        if self._handle is None:
            raise ShardError("this ShardDatabase is closed")
        return self._handle

    @property
    def header(self) -> dict[str, Any]:
        """Database header fields (page size, encoding, freelist info, ...)."""
        return _call(_lib.shard_get_header, ctypes.c_int64(self._require_handle()))

    @property
    def schema(self) -> list[dict[str, Any]]:
        """Every sqlite_master entry (tables, indexes, triggers, views)."""
        return _call(_lib.shard_get_schema, ctypes.c_int64(self._require_handle()))

    @property
    def pages(self) -> list[dict[str, Any]]:
        """Every page in the file with its type and (if known) owning table."""
        return _call(_lib.shard_get_pages, ctypes.c_int64(self._require_handle()))

    def rows(self, table_name: str) -> list[dict[str, Any]]:
        """Live rows currently readable from `table_name`."""
        return _call(_lib.shard_get_rows, ctypes.c_int64(self._require_handle()), table_name.encode("utf-8"))

    def deleted_rows(self, table_name: str) -> list[dict[str, Any]]:
        """Deleted/freeblock-recovered rows still reachable within `table_name`'s own tree."""
        return _call(_lib.shard_get_deleted_rows, ctypes.c_int64(self._require_handle()), table_name.encode("utf-8"))

    def carve(self, mode: str = "loose", tables: list[str] | None = None) -> list[dict[str, Any]]:
        """
        Read-only scan of pages with no known owner, trying every candidate table's schema
        ("loose": declared column types only; "tight": narrowed to each table's own observed
        data). Optionally restrict candidates to `tables`. Does not write anything — use
        recover_to_file's carve_mode to persist results.
        """
        filter_json = json.dumps(tables).encode("utf-8") if tables is not None else None
        return _call(_lib.shard_carve, ctypes.c_int64(self._require_handle()), mode.encode("utf-8"), filter_json)

    def recover_to_file(
        self,
        output_path: str,
        process_wal: bool = True,
        carve_mode: str | None = None,
        carve_table_filter: list[str] | None = None,
    ) -> dict[str, Any]:
        """
        Builds a fully recovered SQLite database at `output_path` (live rows, in-tree
        deleted/freeblock-recovered rows, and — per the arguments — WAL-recovered and/or
        orphan-page-carved rows). Returns a summary dict; `output_path` is a normal SQLite
        file afterwards, openable with the stdlib sqlite3 module.
        """
        options = {"processWal": process_wal, "carveMode": carve_mode, "carveTableFilter": carve_table_filter}
        return _call(
            _lib.shard_recover_to_file,
            ctypes.c_int64(self._require_handle()),
            output_path.encode("utf-8"),
            json.dumps(options).encode("utf-8"),
        )


__all__ = ["ShardDatabase", "ShardError"]
