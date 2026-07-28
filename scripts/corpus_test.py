#!/usr/bin/env python3
"""
corpus_test.py — Run SQLite Forensic Corpus v2.0 tests via shard-cli.

Usage:
  python corpus_test.py <corpus-root> [options]

Options:
  --cli <path>          Path to shard-cli executable (default: shard-cli on PATH)
  --sections <list>     Comma-separated section IDs to test (default: all 01-0E)
  --no-deleted          Skip deleted-row counts (faster)
  --format json|text    Output format for summary (default: text)
  --output <file>       Write summary to file instead of stdout
  --log-dir <dir>       Write one detailed .log file per database here
  --fail-fast           Stop after the first failure

Exit codes:
  0  All tests passed (skipped tests do not count as failures)
  1  One or more tests failed
  2  Usage or environment error

Examples:
  python corpus_test.py /path/to/corpus --log-dir logs/
  python corpus_test.py /path/to/corpus --cli ./shard-cli --format json
  python corpus_test.py /path/to/corpus --sections 0A --log-dir logs/
"""

import argparse
import json
import os
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional


# ── XML parsing ───────────────────────────────────────────────────────────────

_CTRL_CHAR_RE = re.compile(r'[\x00-\x08\x0b\x0c\x0e-\x1f]')

def _sanitise_xml_bytes(path: Path) -> str:
    raw = path.read_bytes()
    text = raw.decode('utf-8', errors='replace')
    return _CTRL_CHAR_RE.sub('', text)


@dataclass
class TableExpectation:
    name: str
    rows_alive: int
    rows_deleted: int
    is_dropped_table: bool   # True when the whole table was deleted from the DB


def parse_xml(xml_path: Path) -> Optional[tuple[str, list[TableExpectation]]]:
    """Return (description, expectations) or None on parse error."""
    try:
        text = _sanitise_xml_bytes(xml_path)
        root = ET.fromstring(text)
    except ET.ParseError:
        return None

    desc_parts = []
    desc_el = root.find('description')
    if desc_el is not None:
        for child in desc_el:
            if child.text and child.text.strip():
                tag = child.tag.split('}')[-1]  # strip namespace
                desc_parts.append(f'{tag}: {child.text.strip()}')
    description = '\n'.join(desc_parts)

    tables = []
    for el in root.findall('element'):
        meta = el.find('meta')
        if meta is None:
            continue
        type_el = meta.find('type')
        if type_el is None or type_el.text.lower() != 'table':
            continue

        name_el = meta.find('name')
        if name_el is None:
            continue

        deleted_el   = meta.find('deleted')
        is_dropped   = deleted_el is not None and deleted_el.text.lower() == 'true'
        alive_el     = meta.find('rowsAlive')
        del_rows_el  = meta.find('rowsDeleted')
        tables.append(TableExpectation(
            name             = name_el.text or '',
            rows_alive       = int(alive_el.text)    if alive_el    is not None and alive_el.text    else 0,
            rows_deleted     = int(del_rows_el.text) if del_rows_el is not None and del_rows_el.text else 0,
            is_dropped_table = is_dropped,
        ))
    return description, tables


# ── CLI invocation ────────────────────────────────────────────────────────────

def run_cli(cli: str, *args) -> tuple[int, dict | list | None, str]:
    cmd = [cli] + list(args)
    result = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8')
    parsed = None
    if result.stdout.strip():
        try:
            parsed = json.loads(result.stdout)
        except json.JSONDecodeError:
            pass
    return result.returncode, parsed, result.stderr.strip()


def get_schema(cli: str, db_path: Path) -> tuple[list[dict], str | None]:
    code, data, stderr = run_cli(cli, 'schema', str(db_path))
    if code != 0 or data is None:
        return [], stderr or 'schema command failed'
    return data.get('entries', []), None


def get_rows(cli: str, db_path: Path, table: str) -> tuple[int, list[dict], str | None]:
    code, data, stderr = run_cli(cli, 'rows', str(db_path), table)
    if code != 0 or data is None:
        return 0, [], stderr or 'rows command failed'
    return data.get('count', 0), data.get('rows', []), None


def get_deleted(cli: str, db_path: Path, table: str) -> tuple[int, list[dict], str | None]:
    code, data, stderr = run_cli(cli, 'deleted', str(db_path), table)
    if code != 0 or data is None:
        return 0, [], stderr or 'deleted command failed'
    return data.get('count', 0), data.get('rows', []), None


# ── Result types ──────────────────────────────────────────────────────────────

@dataclass
class TableResult:
    table: str
    is_dropped_table: bool
    expected_live: int
    actual_live: int
    actual_live_rows: list[dict]
    expected_deleted: int
    actual_deleted: int
    actual_deleted_rows: list[dict]
    error: Optional[str] = None
    skip_reason: Optional[str] = None

    @property
    def skipped(self) -> bool:
        return self.skip_reason is not None

    @property
    def live_pass(self) -> bool:
        return not self.skipped and self.error is None and self.actual_live == self.expected_live

    @property
    def deleted_pass(self) -> bool:
        if self.skipped or self.error is not None:
            return False
        if self.expected_deleted == 0:
            return True
        return self.actual_deleted == self.expected_deleted

    @property
    def passed(self) -> bool:
        return not self.skipped and self.error is None and self.live_pass and self.deleted_pass


@dataclass
class DatabaseResult:
    section: str
    filename: str
    description: str
    schema_entries: list[dict]
    tables: list[TableResult] = field(default_factory=list)
    parse_error: Optional[str] = None

    @property
    def xml_skipped(self) -> bool:
        return self.parse_error is not None

    @property
    def passed(self) -> bool:
        if self.xml_skipped:
            return False
        active = [t for t in self.tables if not t.skipped]
        return len(active) > 0 and all(t.passed for t in active)

    @property
    def failed(self) -> bool:
        return not self.xml_skipped and any(not t.skipped and not t.passed for t in self.tables)

    @property
    def all_skipped(self) -> bool:
        return not self.xml_skipped and len(self.tables) > 0 and all(t.skipped for t in self.tables)


# ── Core analysis ─────────────────────────────────────────────────────────────

DEFAULT_SECTIONS = ['01', '02', '03', '04', '05', '06', '07', '08', '09', '0A', '0B', '0C', '0D', '0E']


def analyse(corpus_root: Path, cli: str, sections: list[str], check_deleted: bool,
            fail_fast: bool, progress: bool, log_dir: Optional[Path]) -> list[DatabaseResult]:
    if log_dir:
        log_dir.mkdir(parents=True, exist_ok=True)
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

            parsed = parse_xml(xml_path)
            if parsed is None:
                db_result = DatabaseResult(section, filename, '', [], parse_error='Malformed XML — skipped')
                results.append(db_result)
                if log_dir:
                    _write_log(log_dir, db_result, db_path, xml_path)
                continue

            description, expectations = parsed
            schema_entries, schema_err = get_schema(cli, db_path)
            schema_table_names = {e['name'] for e in schema_entries if e.get('type') == 'table'}

            db_result = DatabaseResult(section, filename, description, schema_entries)

            for exp in expectations:
                if exp.is_dropped_table:
                    # Table was dropped from the DB; it won't be in sqlite_master.
                    # We record this as skipped with an explanation rather than passing silently.
                    db_result.tables.append(TableResult(
                        table               = exp.name,
                        is_dropped_table    = True,
                        expected_live       = exp.rows_alive,
                        actual_live         = 0,
                        actual_live_rows    = [],
                        expected_deleted    = exp.rows_deleted,
                        actual_deleted      = 0,
                        actual_deleted_rows = [],
                        skip_reason         = (
                            f'table was dropped — not in sqlite_master; '
                            f'{exp.rows_alive} rows expected to be recoverable from raw pages '
                            f'(requires page-scan recovery, not yet supported via CLI)'
                        ),
                    ))
                    continue

                if exp.name not in schema_table_names:
                    db_result.tables.append(TableResult(
                        table               = exp.name,
                        is_dropped_table    = False,
                        expected_live       = exp.rows_alive,
                        actual_live         = 0,
                        actual_live_rows    = [],
                        expected_deleted    = exp.rows_deleted,
                        actual_deleted      = 0,
                        actual_deleted_rows = [],
                        error               = 'table not found in schema',
                    ))
                    continue

                live, live_rows, live_err = get_rows(cli, db_path, exp.name)
                if live_err:
                    db_result.tables.append(TableResult(
                        table               = exp.name,
                        is_dropped_table    = False,
                        expected_live       = exp.rows_alive,
                        actual_live         = 0,
                        actual_live_rows    = [],
                        expected_deleted    = exp.rows_deleted,
                        actual_deleted      = 0,
                        actual_deleted_rows = [],
                        error               = live_err,
                    ))
                    continue

                deleted, deleted_rows, deleted_err = 0, [], None
                if check_deleted:
                    deleted, deleted_rows, deleted_err = get_deleted(cli, db_path, exp.name)

                db_result.tables.append(TableResult(
                    table               = exp.name,
                    is_dropped_table    = False,
                    expected_live       = exp.rows_alive,
                    actual_live         = live,
                    actual_live_rows    = live_rows,
                    expected_deleted    = exp.rows_deleted,
                    actual_deleted      = deleted,
                    actual_deleted_rows = deleted_rows,
                    error               = deleted_err,
                ))

            results.append(db_result)
            if log_dir:
                _write_log(log_dir, db_result, db_path, xml_path)

            if fail_fast and db_result.failed:
                return results

    return results


# ── Per-database log ──────────────────────────────────────────────────────────

def _fmt_rows(rows: list[dict], limit: int = 50) -> str:
    if not rows:
        return '    (none)\n'
    lines = []
    for i, row in enumerate(rows):
        if i >= limit:
            lines.append(f'    ... ({len(rows) - limit} more rows not shown)')
            break
        lines.append('    ' + json.dumps(row, ensure_ascii=False))
    return '\n'.join(lines) + '\n'


def _write_log(log_dir: Path, result: DatabaseResult, db_path: Path, xml_path: Path):
    log_path = log_dir / f'{result.section}-{result.filename}.log'
    lines = []

    lines.append('=' * 72)
    lines.append(f'DATABASE : {result.section}/{result.filename}')
    lines.append(f'PATH     : {db_path}')
    lines.append(f'XML      : {xml_path}')
    lines.append('=' * 72)

    if result.description:
        lines.append('')
        lines.append('DESCRIPTION:')
        for line in result.description.splitlines():
            lines.append(f'  {line}')

    if result.xml_skipped:
        lines.append('')
        lines.append(f'XML PARSE ERROR: {result.parse_error}')
        log_path.write_text('\n'.join(lines) + '\n', encoding='utf-8')
        return

    # Schema
    lines.append('')
    lines.append(f'SCHEMA ({len(result.schema_entries)} entries):')
    if result.schema_entries:
        for e in result.schema_entries:
            lines.append(f'  {e.get("type","?"):<8}  {e.get("name","?")}  root_page={e.get("rootPage","?")}')
    else:
        lines.append('  (empty — no entries in sqlite_master)')

    # Per-table detail
    for t in result.tables:
        lines.append('')
        lines.append('-' * 72)
        marker = 'SKIP' if t.skipped else ('PASS' if t.passed else 'FAIL')
        dropped_note = '  [TABLE WAS DROPPED]' if t.is_dropped_table else ''
        lines.append(f'TABLE: {t.table}{dropped_note}  →  {marker}')
        lines.append('-' * 72)

        if t.skip_reason:
            lines.append(f'  Skip reason: {t.skip_reason}')

        if t.error:
            lines.append(f'  Error: {t.error}')

        lines.append('')
        lines.append(f'  Expected live rows   : {t.expected_live}')
        if not t.skipped:
            match_live = 'OK' if t.live_pass else f'MISMATCH (got {t.actual_live})'
            lines.append(f'  Actual live rows     : {t.actual_live}  {match_live}')
        lines.append(f'  Expected deleted rows: {t.expected_deleted}')
        if not t.skipped:
            match_del = 'OK' if t.deleted_pass else f'MISMATCH (got {t.actual_deleted})'
            lines.append(f'  Actual deleted rows  : {t.actual_deleted}  {match_del}')

        if not t.skipped and not t.error:
            lines.append('')
            lines.append(f'  Live rows ({t.actual_live}):')
            lines.append(_fmt_rows(t.actual_live_rows).rstrip())
            lines.append('')
            lines.append(f'  Recovered deleted rows ({t.actual_deleted}):')
            lines.append(_fmt_rows(t.actual_deleted_rows).rstrip())

    # Overall result
    lines.append('')
    lines.append('=' * 72)
    if result.xml_skipped:
        overall = 'XML-SKIPPED'
    elif result.all_skipped:
        overall = 'ALL-TABLES-SKIPPED'
    elif result.passed:
        overall = 'PASS'
    elif result.failed:
        overall = 'FAIL'
    else:
        overall = 'NO-ACTIVE-TABLES'
    lines.append(f'OVERALL RESULT: {overall}')
    lines.append('=' * 72)

    log_path.write_text('\n'.join(lines) + '\n', encoding='utf-8')


# ── Summary rendering ─────────────────────────────────────────────────────────

def render_text(results: list[DatabaseResult]) -> str:
    passed      = sum(1 for r in results if r.passed)
    failed      = sum(1 for r in results if r.failed)
    all_skipped = sum(1 for r in results if r.all_skipped)
    xml_skipped = sum(1 for r in results if r.xml_skipped)

    lines = [
        f'SQLite Forensic Corpus — {passed} passed, {failed} failed, '
        f'{all_skipped} all-tables-skipped, {xml_skipped} xml-skipped',
        '',
    ]

    for r in results:
        if r.xml_skipped:
            lines.append(f'  XML-SKIP  {r.section}/{r.filename}  ({r.parse_error})')
            continue

        if r.all_skipped:
            skip_tables = ', '.join(t.table for t in r.tables)
            lines.append(f'  SKIP      {r.section}/{r.filename}  (all tables skipped: {skip_tables})')
            continue

        marker = 'PASS' if r.passed else 'FAIL'
        lines.append(f'  {marker}      {r.section}/{r.filename}')

        for t in r.tables:
            if t.skipped:
                lines.append(f'             SKIP   {t.table}: {t.skip_reason}')
            elif t.error:
                lines.append(f'             ERROR  {t.table}: {t.error}')
            elif not t.passed:
                parts = []
                if not t.live_pass:
                    parts.append(f'live {t.actual_live}/{t.expected_live}')
                if not t.deleted_pass:
                    parts.append(f'deleted {t.actual_deleted}/{t.expected_deleted}')
                lines.append(f'             FAIL   {t.table}: {", ".join(parts)}')

    return '\n'.join(lines) + '\n'


def render_json(results: list[DatabaseResult]) -> str:
    passed      = sum(1 for r in results if r.passed)
    failed      = sum(1 for r in results if r.failed)
    all_skipped = sum(1 for r in results if r.all_skipped)
    xml_skipped = sum(1 for r in results if r.xml_skipped)

    doc = {
        'summary': {
            'passed':         passed,
            'failed':         failed,
            'tablesSkipped':  all_skipped,
            'xmlSkipped':     xml_skipped,
        },
        'databases': [
            {
                'section':    r.section,
                'filename':   r.filename,
                'passed':     r.passed,
                'allSkipped': r.all_skipped,
                'xmlSkipped': r.xml_skipped,
                'parseError': r.parse_error,
                'tables': [
                    {
                        'table':           t.table,
                        'isDroppedTable':  t.is_dropped_table,
                        'expectedLive':    t.expected_live,
                        'actualLive':      t.actual_live,
                        'expectedDeleted': t.expected_deleted,
                        'actualDeleted':   t.actual_deleted,
                        'passed':          t.passed,
                        'skipped':         t.skipped,
                        'skipReason':      t.skip_reason,
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
    parser.add_argument('--output',     type=Path, default=None, help='Write summary to file')
    parser.add_argument('--log-dir',    type=Path, default=None,
                        help='Directory to write one detailed .log file per database')
    parser.add_argument('--fail-fast',  action='store_true', help='Stop after first failure')
    parser.add_argument('--quiet',      action='store_true', help='Suppress progress output')
    args = parser.parse_args()

    corpus_root: Path = args.corpus_root
    if not corpus_root.is_dir():
        print(f'error: corpus root not found: {corpus_root}', file=sys.stderr)
        return 2

    probe_code, _, _ = run_cli(args.cli, '--help')
    if probe_code not in (0, 2):
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
        log_dir       = args.log_dir,
    )

    report = render_json(results) if args.format == 'json' else render_text(results)

    if args.output:
        args.output.write_text(report, encoding='utf-8')
        if not args.quiet:
            print(f'Report written to {args.output}', file=sys.stderr)
    else:
        print(report, end='')

    if args.log_dir and not args.quiet:
        print(f'Logs written to {args.log_dir}/', file=sys.stderr)

    failed = sum(1 for r in results if r.failed)
    return 1 if failed > 0 else 0


if __name__ == '__main__':
    sys.exit(main())
