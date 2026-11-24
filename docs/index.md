# SHARD User Guide

**SHARD** (SQLite Forensic Analyser) is a desktop tool for examining SQLite database files at the byte level — browsing pages, recovering deleted records, inspecting WAL files, and querying recovered data.

## Contents

| Document | Description |
|---|---|
| [Getting Started](getting-started.md) | Opening a file and navigating the interface |
| [Pages Tab](pages-tab.md) | Browsing pages, filters, hex view, and record decoding |
| [Schema Tab](schema-tab.md) | Viewing the database object list |
| [Search Tab](search-tab.md) | Regex search across all page data |
| [Projects & Query](projects-and-query.md) | Creating a project and running SQL queries |
| [WAL Tab](wal-tab.md) | Inspecting Write-Ahead Log files |
| [Carve Unknown Pages](carve-unknown-pages.md) | Recovering records from pages with no known owner, and reusing tuned carving parameters |

## Quick start

1. Launch SHARD and open a `.db` / `.sqlite` file via **File → Open Database** or drag it onto the window.
2. Use the **Pages** tab to browse the B-tree structure and examine raw bytes.
3. Create a **Project** (File → Create Project) to enable SQL querying and record recovery.
4. Right-click any byte in the hex view and choose **Try to decode record at this location** to attempt recovery of a deleted record.
