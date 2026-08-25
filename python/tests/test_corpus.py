"""Confirms the SHARD.Native/Python path reproduces the same results as SHARD.Core.Tests'
CorpusTests.cs (see ../../SHARD.Core.Tests/CorpusTests.cs) against the SQLite Forensic Corpus
v2.0. Both suites call into the exact same recovery engine — this one just proves the C ABI +
ctypes bindings don't lose or distort anything on the way, by re-running the corpus's live-row
and deleted-row expectations through ShardDatabase instead of SqliteForensicDatabase directly.

Uses only the stdlib (unittest, xml.etree, ctypes via shard_native) — no pytest dependency,
consistent with the rest of this Python package.

Requires:
  - The corpus checked out at $SQLITE_CORPUS_PATH, or <repo root>/TestData/Corpus (same
    fallback CorpusTests.cs uses). Tests are skipped automatically when not found.
  - The native library discoverable the same way shard_native normally finds it (SHARD_NATIVE_LIB
    / SHARD_NATIVE_LIB_DIR / shard_native/native/ — see _bindings.py). Tests are skipped
    automatically when it can't be loaded.

Run with:
    python3 -m unittest discover -s python/tests -v
"""
from __future__ import annotations

import os
import sys
import unittest
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

REPO_ROOT = Path(__file__).resolve().parents[2]
CORPUS_ROOT = Path(os.environ.get("SQLITE_CORPUS_PATH") or (REPO_ROOT / "TestData" / "Corpus"))

# Standard corpus sections only (skip anti-forensic 11+ for now) — matches CorpusTests.cs.
STANDARD_SECTIONS = ["01", "02", "03", "04", "05", "06", "07", "08", "09", "0A", "0B", "0C", "0D", "0E"]


@dataclass(frozen=True)
class TableExpectation:
    name: str
    is_deleted: bool
    rows_alive: int
    rows_deleted: int


@dataclass(frozen=True)
class CorpusEntry:
    section: str
    file_name: str
    db_path: Path
    xml_path: Path
    tables: list[TableExpectation] = field(default_factory=list)


def _parse_xml(xml_path: Path) -> list[TableExpectation] | None:
    try:
        root = ET.parse(xml_path).getroot()
    except ET.ParseError:
        return None  # malformed XML — skip, same as CorpusTests.cs's TryParseXml

    tables: list[TableExpectation] = []
    for el in root.findall("element"):
        meta = el.find("meta")
        if meta is None:
            continue
        type_text = (meta.findtext("type") or "")
        if type_text.strip().lower() != "table":
            continue

        name = meta.findtext("name") or ""
        is_deleted = (meta.findtext("deleted") or "").strip().lower() == "true"
        try:
            rows_alive = int(meta.findtext("rowsAlive") or 0)
        except ValueError:
            rows_alive = 0
        try:
            rows_deleted = int(meta.findtext("rowsDeleted") or 0)
        except ValueError:
            rows_deleted = 0

        tables.append(TableExpectation(name, is_deleted, rows_alive, rows_deleted))
    return tables


def _discover_corpus() -> list[CorpusEntry]:
    if not CORPUS_ROOT.is_dir():
        return []
    entries: list[CorpusEntry] = []
    for section in STANDARD_SECTIONS:
        section_dir = CORPUS_ROOT / section
        if not section_dir.is_dir():
            continue
        for db_path in sorted(section_dir.glob("*.db")):
            xml_path = db_path.with_suffix(".xml")
            if not xml_path.exists():
                continue
            tables = _parse_xml(xml_path)
            if tables is None:
                continue
            entries.append(CorpusEntry(section, db_path.name, db_path, xml_path, tables))
    return entries


def _native_lib_error() -> str | None:
    """Returns None if shard_native loads cleanly, else a message explaining why not."""
    try:
        import shard_native  # noqa: F401
    except OSError as exc:
        return str(exc)
    return None


_CORPUS_ENTRIES = _discover_corpus()
_NATIVE_LIB_ERROR = _native_lib_error()


@unittest.skipUnless(_CORPUS_ENTRIES, f"corpus not found under {CORPUS_ROOT} (set SQLITE_CORPUS_PATH)")
@unittest.skipIf(_NATIVE_LIB_ERROR, f"shard_native library unavailable: {_NATIVE_LIB_ERROR}")
class CorpusReplicationTests(unittest.TestCase):
    """Python-side counterparts to CorpusTests.LiveRecords_MatchExpected /
    DeletedRecords_MatchExpected. Each corpus table gets its own subTest, so one mismatch
    doesn't hide the rest — mirroring xUnit's per-[Theory]-case reporting."""

    @classmethod
    def setUpClass(cls) -> None:
        from shard_native import ShardDatabase
        cls.ShardDatabase = ShardDatabase

    def test_live_records_match_expected(self) -> None:
        for entry in _CORPUS_ENTRIES:
            with self.ShardDatabase(str(entry.db_path)) as db:
                live_tables = {
                    e["name"]: e
                    for e in db.schema
                    if e["type"] == "table" and e.get("rootPage")
                }
                for expected in entry.tables:
                    if expected.is_deleted:
                        continue
                    if expected.name not in live_tables:
                        continue

                    with self.subTest(section=entry.section, file=entry.file_name, table=expected.name):
                        try:
                            live_count = len(db.rows(expected.name))
                        except Exception as exc:  # ShardError or ctypes failure
                            self.fail(
                                f"{entry.section}/{entry.file_name} table '{expected.name}': "
                                f"shard_native raised {type(exc).__name__} reading "
                                f"{expected.rows_alive} expected rows — {exc}"
                            )
                            continue

                        self.assertEqual(
                            live_count, expected.rows_alive,
                            f"{entry.section}/{entry.file_name} table '{expected.name}': "
                            f"expected {expected.rows_alive} live rows, shard_native found {live_count}",
                        )

    def test_deleted_records_match_expected(self) -> None:
        for entry in _CORPUS_ENTRIES:
            if not any(not t.is_deleted and t.rows_deleted > 0 for t in entry.tables):
                continue  # same DatabasesWithDeletedRecords filter as CorpusTests.cs

            with self.ShardDatabase(str(entry.db_path)) as db:
                live_tables = {
                    e["name"]: e
                    for e in db.schema
                    if e["type"] == "table" and e.get("rootPage")
                }
                for expected in entry.tables:
                    if expected.is_deleted or expected.rows_deleted == 0:
                        continue
                    if expected.name not in live_tables:
                        continue

                    with self.subTest(section=entry.section, file=entry.file_name, table=expected.name):
                        try:
                            deleted_count = len(db.deleted_rows(expected.name))
                        except Exception as exc:
                            self.fail(
                                f"{entry.section}/{entry.file_name} table '{expected.name}': "
                                f"shard_native raised {type(exc).__name__} reading "
                                f"{expected.rows_deleted} expected deleted rows — {exc}"
                            )
                            continue

                        self.assertEqual(
                            deleted_count, expected.rows_deleted,
                            f"{entry.section}/{entry.file_name} table '{expected.name}': "
                            f"expected {expected.rows_deleted} deleted rows, "
                            f"shard_native recovered {deleted_count}",
                        )


if __name__ == "__main__":
    unittest.main()
