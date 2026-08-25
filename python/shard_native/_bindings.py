"""Loads the SHARD.Native shared library and declares its C ABI for ctypes.

The library is a Native-AOT-published .NET assembly (see SHARD.Native/SHARD.Native.csproj) —
there is no managed runtime dependency at all on the Python side, just a plain shared library.
It is *not* built as part of this Python package; you need to publish it separately (once, per
platform you want to run on):

    dotnet publish SHARD.Native -r <rid> -c Release

where <rid> is e.g. linux-x64, win-x64, osx-x64, osx-arm64. Point this loader at the result via
one of, in priority order:
  1. the SHARD_NATIVE_LIB environment variable (full path to the file)
  2. the SHARD_NATIVE_LIB_DIR environment variable (a directory containing it)
  3. a "native/" subdirectory next to this file
  4. this file's own directory
"""
from __future__ import annotations

import ctypes
import os
import platform
from pathlib import Path


def _candidate_names() -> list[str]:
    system = platform.system()
    if system == "Windows":
        return ["shard_native.dll"]
    if system == "Darwin":
        return ["libshard_native.dylib", "shard_native.dylib"]
    return ["libshard_native.so", "shard_native.so"]


def _candidate_dirs() -> list[Path]:
    here = Path(__file__).resolve().parent
    dirs = [here / "native", here]
    env_dir = os.environ.get("SHARD_NATIVE_LIB_DIR")
    if env_dir:
        dirs.insert(0, Path(env_dir))
    return dirs


def _find_library() -> Path:
    override = os.environ.get("SHARD_NATIVE_LIB")
    if override:
        path = Path(override)
        if path.exists():
            return path
        raise OSError(f"SHARD_NATIVE_LIB={override} does not exist")

    for directory in _candidate_dirs():
        for name in _candidate_names():
            candidate = directory / name
            if candidate.exists():
                return candidate

    raise OSError(
        "Could not find the shard_native shared library.\n"
        "Build it first with:\n"
        "  dotnet publish SHARD.Native -r <your-rid> -c Release\n"
        "then either set SHARD_NATIVE_LIB=/full/path/to/shard_native.(so|dll|dylib),\n"
        "or copy the published file into this package's 'native/' directory."
    )


def bind() -> ctypes.CDLL:
    """Loads the library and declares argtypes/restype for every shard_* export.

    Every data-returning export returns an opaque owned pointer (a heap-allocated,
    null-terminated UTF-8 JSON buffer) — callers must pass it to shard_free_string
    exactly once. c_void_p is used (not c_char_p) as the restype specifically so ctypes
    doesn't auto-copy-and-decode-then-leak the buffer before we get a chance to free it.
    """
    lib = ctypes.CDLL(str(_find_library()))

    lib.shard_open.argtypes = [ctypes.c_char_p]
    lib.shard_open.restype = ctypes.c_void_p

    lib.shard_close.argtypes = [ctypes.c_int64]
    lib.shard_close.restype = None

    lib.shard_get_header.argtypes = [ctypes.c_int64]
    lib.shard_get_header.restype = ctypes.c_void_p

    lib.shard_get_schema.argtypes = [ctypes.c_int64]
    lib.shard_get_schema.restype = ctypes.c_void_p

    lib.shard_get_pages.argtypes = [ctypes.c_int64]
    lib.shard_get_pages.restype = ctypes.c_void_p

    lib.shard_get_rows.argtypes = [ctypes.c_int64, ctypes.c_char_p]
    lib.shard_get_rows.restype = ctypes.c_void_p

    lib.shard_get_deleted_rows.argtypes = [ctypes.c_int64, ctypes.c_char_p]
    lib.shard_get_deleted_rows.restype = ctypes.c_void_p

    lib.shard_carve.argtypes = [ctypes.c_int64, ctypes.c_char_p, ctypes.c_char_p]
    lib.shard_carve.restype = ctypes.c_void_p

    lib.shard_recover_to_file.argtypes = [ctypes.c_int64, ctypes.c_char_p, ctypes.c_char_p]
    lib.shard_recover_to_file.restype = ctypes.c_void_p

    lib.shard_free_string.argtypes = [ctypes.c_void_p]
    lib.shard_free_string.restype = None

    return lib
