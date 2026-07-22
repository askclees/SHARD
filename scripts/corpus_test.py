#!/usr/bin/env python3
"""
corpus_test.py — Run SQLite Forensic Corpus v2.0 tests via shard-cli.

Usage:
  python corpus_test.py <corpus-root> [options]

Options:
  --cli <path>          Path to shard-cli executable (default: shard-cli on PATH)
  --sections <list>     Comma-separated section IDs to test (default: all 01-0E)
  --no-deleted          Skip deleted-row counts (faster)
  --format json|text    Output format (default: text)
  --output <file>       Write report to file instead of stdout
  --fail-fast           Stop after the first failure

Exit codes:
  0  All tests passed
  1  One or more tests failed
  2  Usage or environment error

Examples:
  python corpus_test.py /path/to/corpus
  python corpus_test.py /path/to/corpus --cli ./shard-cli --format json
  python corpus_test.py /path/to/corpus --sections 01,02,03
"""

import argparse
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field, asdict
from pathlib import Path
from typing import Optional


# ── XML parsing ───────────────────────────────────────────────────────────────

_CTRL_CHAR_RE = re.compile(r'[\x00-\x08\x0b\x0c\x0e-\x1f]')

def _sanitise_xml_bytes(path: Path) -> str:
    """Read XML file and replace invalid XML control characters before parsing."""
    raw = path.read_bytes()
    text = raw.decode('utf-8', errors='replace')
    return _CTRL_CHAR_RE.sub('', text)


@dataclass
class TableExpectation:
    name: str
    rows_alive: int
    rows_deleted: int


def parse_xml(xml_path: Path) -> Optional[list[TableExpectation]]:
    """Return list of table expectations, or None if XML is malformed."""
    try:
        text = _sanitise_xml_bytes(xml_path)
        root = ET.fromstring(text)
    except ET.ParseError as e:
        return None

    tables = []
    for el in root.findall('element'):
        meta = el.find('meta')
        if meta is None:
            continue
        type_el = meta.find('type')
        if type_el is None or type_el.text.lower() != 'table':
            continue
        deleted_el = meta.find('deleted')
        if deleted_el is not None and deleted_el.text.lower() == 'true':
            continue  # skip shadow "deleted table" entries

        name_el = meta.find('name')
        if name_el is None:
            continue

        alive_el   = meta.find('rowsAlive')
        deleted_el = meta.find('rowsDeleted')
        tables.append(TableExpectation(
            name         = name_el.text or '',
            rows_alive   = int(alive_el.text)   if alive_el   is not None and alive_el.text   else 0,
            rows_deleted = int(deleted_el.text) if deleted_el is not None and deleted_el.text else 0,
        ))
    return tables


# ── CLI invocation ────────────────────────────────────────────────────────────

def run_cli(cli: str, *args) -> tuple[int, dict | list | None, str]:
    """Run shard-cli and return (exit_code, parsed_json_or_none, stderr)."""
    cmd = [cli] + list(args)
    result = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8')
    parsed = None
    if result.stdout.strip():
        try:
            parsed = json.loads(result.stdout)
        except json.JSONDecodeError:
            pass
    return result.returncode, parsed, result.stderr.strip()


def get_tables(cli: str, db_path: Path) -> tuple[list[str], str | None]:
    """Return (table_names, error). error is None on success."""
    code, data, stderr = run_cli(cli, 'schema', str(db_path))
    if code != 0 or data is None:
        return [], stderr or 'schema command failed'
    names = [e['name'] for e in data.get('entries', []) if e.get('type') == 'table']
    return names, None


def count_rows(cli: str, db_path: Path, table: str) -> tuple[int, str | None]:
    code, data, stderr = run_cli(cli, 'rows', str(db_path), table)
    if code != 0 or data is None:
        return 0, stderr or 'rows command failed'
    return data.get('count', 0), None


def count_deleted(cli: str, db_path: Path, table: str) -> tuple[int, str | None]:
    code, data, stderr = run_cli(cli, 'deleted', str(db_path), table)
    if code != 0 or data is None:
        return 0, stderr or 'deleted command failed'
    return data.get('count', 0), None


# ── Result types ──────────────────────────────────────────────────────────────

@dataclass
class TableResult:
    table: str
    expected_live: int
    actual_live: int
    expected_deleted: int
    actual_deleted: int
    error: Optional[str] = None

    @property
    def live_pass(self) -> bool:
        return self.error is None and self.actual_live == self.expected_live

    @property
    def deleted_pass(self) -> bool:
        if self.error is not None:
            return False
        if self.expected_deleted == 0:
            return True
        return self.actual_deleted == self.expected_deleted

    @property
    def passed(self) -> bool:
        return self.live_pass and self.deleted_pass


@dataclass
class DatabaseResult:
    section: str
    filename: str
    tables: list[TableResult] = field(default_factory=list)
    parse_error: Optional[str] = None

    @property
    def skipped(self) -> bool:
        return self.parse_error is not None

    @property
    def passed(self) -> bool:
        return not self.skipped and all(t.passed for t in self.tables)

    @property
    def failed(self) -> bool:
        return not self.skipped and not self.passed


# ── Core analysis ─────────────────────────────────────────────────────────────

DEFAULT_SECTIONS = ['01', '02', '03', '04', '05', '06', '07', '08', '09', '0A', '0B', '0C', '0D', '0E']


def analyse(corpus_root: Path, cli: str, sections: list[str], check_deleted: bool,
            fail_fast: bool, progress: bool) -> list[DatabaseResult]:
    results = []

    for section in sections:
        sec_dir = corpus_root / section
        if not sec_dir.is_dir():
            continue

        for db_path in sorted(sec_dir.glob('*.db')):
            xml_path = db_path.with_suffix('.xml')
            if not xml_path.exists():
                continue

            filename = db_path.name
            if progress:
                print(f'  {section}/{filename}', file=sys.stderr, flush=True)

            expectations = parse_xml(xml_path)
            if expectations is None:
                results.append(DatabaseResult(section, filename, parse_error='Malformed XML — skipped'))
                continue

            db_result = DatabaseResult(section, filename)
            db_tables, schema_err = get_tables(cli, db_path)

            for exp in expectations:
                if exp.name not in db_tables:
                    continue

                live, live_err = count_rows(cli, db_path, exp.name)
                if live_err:
                    db_result.tables.append(TableResult(
                        table=exp.name,
                        expected_live=exp.rows_alive, actual_live=0,
                        expected_deleted=exp.rows_deleted, actual_deleted=0,
                        error=live_err,
                    ))
                    continue

                deleted = 0
                deleted_err = None
                if check_deleted and exp.rows_deleted > 0:
                    deleted, deleted_err = count_deleted(cli, db_path, exp.name)

                db_result.tables.append(TableResult(
                    table=exp.name,
                    expected_live=exp.rows_alive,    actual_live=live,
                    expected_deleted=exp.rows_deleted, actual_deleted=deleted,
                    error=deleted_err,
                ))

            results.append(db_result)

            if fail_fast and db_result.failed:
                return results

    return results


# ── Reporting ─────────────────────────────────────────────────────────────────

def render_text(results: list[DatabaseResult]) -> str:
    lines = []
    passed = sum(1 for r in results if r.passed)
    failed = sum(1 for r in results if r.failed)
    skipped = sum(1 for r in results if r.skipped)

    lines.append(f'SQLite Forensic Corpus — {passed} passed, {failed} failed, {skipped} skipped\n')

    for r in results:
        if r.skipped:
            lines.append(f'  SKIP  {r.section}/{r.filename}  ({r.parse_error})')
            continue

        marker = 'PASS' if r.passed else 'FAIL'
        lines.append(f'  {marker}  {r.section}/{r.filename}')

        for t in r.tables:
            if t.passed:
                continue
            if t.error:
                lines.append(f'         ERROR  {t.table}: {t.error}')
            else:
                parts = []
                if not t.live_pass:
                    parts.append(f'live {t.actual_live}/{t.expected_live}')
                if not t.deleted_pass:
                    parts.append(f'deleted {t.actual_deleted}/{t.expected_deleted}')
                lines.append(f'         FAIL   {t.table}: {", ".join(parts)}')

    return '\n'.join(lines) + '\n'


def render_json(results: list[DatabaseResult]) -> str:
    passed  = sum(1 for r in results if r.passed)
    failed  = sum(1 for r in results if r.failed)
    skipped = sum(1 for r in results if r.skipped)

    doc = {
        'summary': {'passed': passed, 'failed': failed, 'skipped': skipped},
        'databases': [
            {
                'section':     r.section,
                'filename':    r.filename,
                'passed':      r.passed,
                'skipped':     r.skipped,
                'parseError':  r.parse_error,
                'tables': [
                    {
                        'table':           t.table,
                        'expectedLive':    t.expected_live,
                        'actualLive':      t.actual_live,
                        'expectedDeleted': t.expected_deleted,
                        'actualDeleted':   t.actual_deleted,
                        'passed':          t.passed,
                        'error':           t.error,
                    }
                    for t in r.tables
                ],
            }
            for r in results
        ],
    }
    return json.dumps(doc, indent=2) + '\n'


# ── Entry point ───────────────────────────────────────────────────────────────

def main() -> int:
    parser = argparse.ArgumentParser(
        description='Run SQLite Forensic Corpus tests via shard-cli.',
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument('corpus_root', type=Path, help='Path to corpus root directory')
    parser.add_argument('--cli',        default='shard-cli', help='shard-cli executable path')
    parser.add_argument('--sections',   default=None,
                        help='Comma-separated section IDs (e.g. 01,02,0A); default: all')
    parser.add_argument('--no-deleted', action='store_true', help='Skip deleted-row counts')
    parser.add_argument('--format',     choices=['text', 'json'], default='text')
    parser.add_argument('--output',     type=Path, default=None, help='Write report to file')
    parser.add_argument('--fail-fast',  action='store_true', help='Stop after first failure')
    parser.add_argument('--quiet',      action='store_true', help='Suppress progress output')
    args = parser.parse_args()

    corpus_root: Path = args.corpus_root
    if not corpus_root.is_dir():
        print(f'error: corpus root not found: {corpus_root}', file=sys.stderr)
        return 2

    # Verify CLI is accessible
    probe_code, _, _ = run_cli(args.cli, '--help')
    if probe_code not in (0, 2):  # shard-cli --help exits 0
        print(f'error: cannot run shard-cli at "{args.cli}"', file=sys.stderr)
        print('Build it with: dotnet publish SHARD.Cli -c Release', file=sys.stderr)
        return 2

    sections = args.sections.split(',') if args.sections else DEFAULT_SECTIONS

    if not args.quiet:
        print(f'Running corpus tests from {corpus_root} ...', file=sys.stderr)

    results = analyse(
        corpus_root   = corpus_root,
        cli           = args.cli,
        sections      = sections,
        check_deleted = not args.no_deleted,
        fail_fast     = args.fail_fast,
        progress      = not args.quiet,
    )

    report = render_json(results) if args.format == 'json' else render_text(results)

    if args.output:
        args.output.write_text(report, encoding='utf-8')
        if not args.quiet:
            print(f'Report written to {args.output}', file=sys.stderr)
    else:
        print(report, end='')

    failed = sum(1 for r in results if r.failed)
    return 1 if failed > 0 else 0


if __name__ == '__main__':
    sys.exit(main())
