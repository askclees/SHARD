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
import os
import sqlite3
import tempfile
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


def _quote_identifier(name: str) -> str:
    """SQLite double-quoted identifier escaping — matches ShadowDatabaseBuilder.QuoteIdentifier
    on the .NET side, so table/column names with embedded quotes (real corpus fixtures have
    these) round-trip correctly instead of producing broken SQL."""
    return '"' + name.replace('"', '""') + '"'


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
        self._recovered_path: str | None = None
        self._recovered_options: tuple[Any, ...] | None = None

    def close(self) -> None:
        self._cleanup_recovered_file()
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

    def query(
        self,
        sql: str,
        params: Any = (),
        *,
        process_wal: bool = True,
        carve_mode: str | None = None,
        carve_table_filter: list[str] | None = None,
    ) -> list[dict[str, Any]]:
        """
        Runs a read/write SQL statement against a fully recovered copy of this database via the
        stdlib sqlite3 module — live rows stay in their original table names; in-tree
        deleted/freeblock-recovered rows land alongside them in `_shard_recovered_<table>`
        tables, so a query can join or UNION live and recovered data directly (e.g.
        ``SELECT * FROM users UNION ALL SELECT * FROM _shard_recovered_users``).

        The recovered copy is built once, in a temp file, the first time query() is called (or
        again if called with different process_wal/carve_mode/carve_table_filter than last
        time), and reused across calls with matching options — cleaned up automatically when
        this ShardDatabase is closed. For anything beyond one-off queries against a stable
        recovery, calling recover_to_file() yourself once and opening the result directly is
        more explicit about when recovery actually happens.
        """
        options_key = (
            process_wal, carve_mode,
            tuple(carve_table_filter) if carve_table_filter else None,
        )
        if self._recovered_path is None or self._recovered_options != options_key:
            self._cleanup_recovered_file()
            fd, path = tempfile.mkstemp(suffix=".db", prefix="shard_query_")
            os.close(fd)
            os.remove(path)  # recover_to_file creates it fresh; it must not already exist
            self.recover_to_file(
                path, process_wal=process_wal,
                carve_mode=carve_mode, carve_table_filter=carve_table_filter,
            )
            self._recovered_path = path
            self._recovered_options = options_key

        connection = sqlite3.connect(self._recovered_path)
        try:
            connection.row_factory = sqlite3.Row
            cursor = connection.execute(sql, params)
            if cursor.description is None:
                connection.commit()
                return []
            return [dict(row) for row in cursor.fetchall()]
        finally:
            connection.close()

    def table_rows(
        self,
        table_name: str,
        *,
        include_deleted: bool = False,
        process_wal: bool = True,
        carve_mode: str | None = None,
        carve_table_filter: list[str] | None = None,
    ) -> list[dict[str, Any]]:
        """
        Returns `table_name`'s rows via a SQL query against a recovered copy — live rows only
        by default; pass include_deleted=True to also include in-tree recoverable deleted/
        freeblock rows from `_shard_recovered_<table_name>` (plus carved orphan-page records
        too, if carve_mode is also passed). Columns are read from `table_name` itself via
        `PRAGMA table_info` (its own declared columns, plus SHARD's own `_page_number`/
        `_cell_offset`/`_overflow_page` provenance columns) — `_shard_recovered_<table_name>`
        additionally has a `_recovery_method` column that isn't part of that set and so isn't
        included here, unlike writing the equivalent query() UNION by hand with `SELECT *`.
        Uses the same cached-recovered-copy behavior as query() (see there for details).
        """
        recover_kwargs = dict(process_wal=process_wal, carve_mode=carve_mode, carve_table_filter=carve_table_filter)
        quoted_table = _quote_identifier(table_name)

        columns = [
            row["name"] for row in
            self.query(f"PRAGMA table_info({quoted_table})", **recover_kwargs)
        ]
        if not columns:
            raise ShardError(f"Table '{table_name}' not found (or has no columns).")
        column_list = ", ".join(_quote_identifier(c) for c in columns)

        sql = f"SELECT {column_list} FROM {quoted_table}"
        if include_deleted:
            recovered_table = _quote_identifier(f"_shard_recovered_{table_name}")
            sql += f" UNION ALL SELECT {column_list} FROM {recovered_table}"

        return self.query(sql, **recover_kwargs)

    def _cleanup_recovered_file(self) -> None:
        if self._recovered_path is None:
            return
        for suffix in ("", "-wal", "-shm"):
            path = self._recovered_path + suffix
            if os.path.exists(path):
                os.remove(path)
        self._recovered_path = None
        self._recovered_options = None


__all__ = ["ShardDatabase", "ShardError"]
