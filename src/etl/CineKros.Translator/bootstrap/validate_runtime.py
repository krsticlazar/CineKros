#!/usr/bin/env python3
"""Validate the exact locked venv and emit a path-free runtime identity."""

from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import re
import sys
from pathlib import Path


def normalize_name(name: str) -> str:
    return re.sub(r"[-_.]+", "-", name).lower()


def lock_packages(path: Path) -> dict[str, str]:
    packages: dict[str, str] = {}
    for line in path.read_text(encoding="utf-8").splitlines():
        match = re.match(r"^([A-Za-z0-9_.-]+)==([^\\\s]+)", line)
        if not match:
            continue
        name, version = normalize_name(match.group(1)), match.group(2)
        if name in packages:
            raise ValueError("duplicate package in requirements lock")
        packages[name] = version
    if not packages:
        raise ValueError("requirements lock contains no pins")
    return packages


def installed_packages() -> dict[str, str]:
    packages: dict[str, str] = {}
    for distribution in importlib.metadata.distributions():
        name = distribution.metadata.get("Name")
        if not name:
            continue
        normalized = normalize_name(name)
        if normalized in packages:
            raise ValueError("duplicate installed distribution")
        packages[normalized] = distribution.version
    return dict(sorted(packages.items()))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--lock", required=True)
    parser.add_argument("--expected-python", required=True)
    args = parser.parse_args()
    try:
        lock_path = Path(args.lock)
        actual_version = ".".join(str(value) for value in sys.version_info[:3])
        if actual_version != args.expected_python or actual_version != "3.12.14":
            raise ValueError("Python version mismatch")
        expected = lock_packages(lock_path)
        actual = installed_packages()
        runtime_tools = {"pip", "setuptools"}
        if set(actual) - runtime_tools != set(expected):
            raise ValueError("installed package set differs from lock")
        for name, version in expected.items():
            if actual.get(name) != version:
                raise ValueError("installed package version differs from lock")
        if actual.get("torch") != "2.9.0+cpu":
            raise ValueError("CPU PyTorch baseline mismatch")

        import torch
        import transformers
        import tokenizers
        import sentencepiece
        import sacremoses

        direct = {
            "torch": torch.__version__,
            "transformers": transformers.__version__,
            "tokenizers": tokenizers.__version__,
            "sentencepiece": sentencepiece.__version__,
            "sacremoses": importlib.metadata.version("sacremoses"),
        }
        if direct != {
            "torch": "2.9.0+cpu",
            "transformers": "4.57.1",
            "tokenizers": "0.22.1",
            "sentencepiece": "0.2.1",
            "sacremoses": "0.1.1",
        } or torch.version.cuda is not None:
            raise ValueError("direct runtime package versions/device mismatch")

        lock_hash = hashlib.sha256(lock_path.read_bytes()).hexdigest()
        identity = {
            "schemaVersion": "translator-python-runtime-v1",
            "pythonVersion": actual_version,
            "runtimeLockSha256": lock_hash,
            "packages": actual,
            "torchDevice": "cpu",
        }
        canonical = json.dumps(identity, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
        manifest = dict(identity)
        manifest["runtimeFingerprint"] = hashlib.sha256(canonical).hexdigest()
        sys.stdout.write(json.dumps(manifest, ensure_ascii=False, separators=(",", ":")) + "\n")
        return 0
    except Exception:
        print("translator runtime validation failed", file=sys.stderr)
        return 3


if __name__ == "__main__":
    raise SystemExit(main())
