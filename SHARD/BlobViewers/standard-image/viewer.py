#!/usr/bin/env python3
"""Example SHARD blob-viewer plugin: image formats SHARD's own renderer already decodes
natively (PNG/JPEG/GIF/BMP), so `decode` is a pure passthrough. See viewer.json for the
manifest, and BlobViewerManifest/BlobViewerPlugin in SHARD.Core for the protocol this
implements: `check <sample on stdin>` exits 0/1; `decode <full blob on stdin>` writes a
one-line JSON header to stdout, then the raw result bytes.
"""
import sys

MAGIC_PREFIXES = [
    b"\x89PNG\r\n\x1a\n",  # PNG
    b"\xff\xd8\xff",        # JPEG
    b"GIF87a",              # GIF
    b"GIF89a",              # GIF
    b"BM",                  # BMP
]


def check() -> None:
    sample = sys.stdin.buffer.read()
    sys.exit(0 if any(sample.startswith(p) for p in MAGIC_PREFIXES) else 1)


def decode() -> None:
    data = sys.stdin.buffer.read()
    sys.stdout.buffer.write(b'{"kind":"image"}\n')
    sys.stdout.buffer.write(data)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        sys.exit(1)
    if sys.argv[1] == "check":
        check()
    elif sys.argv[1] == "decode":
        decode()
    else:
        sys.exit(1)
