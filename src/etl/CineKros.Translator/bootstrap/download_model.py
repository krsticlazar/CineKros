#!/usr/bin/env python3
"""Download and verify only the exact pinned OPUS artifact set."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sys
import tempfile
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path

MODEL_ID = "Helsinki-NLP/opus-mt-en-sla"
REVISION = "0bc26914f2f82c3dd5b235e420aa2c711a5ed3d8"
API = f"https://huggingface.co/api/models/{MODEL_ID}/revision/{REVISION}"


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def git_blob_oid(path: Path) -> str:
    digest = hashlib.sha1()
    digest.update(f"blob {path.stat().st_size}\0".encode("ascii"))
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def fetch_json(url: str) -> object:
    request = urllib.request.Request(url, headers={"User-Agent": "CineKros-P2-translator-bootstrap/1.0"})
    with urllib.request.urlopen(request, timeout=45) as response:
        return json.load(response)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-dir", required=True)
    parser.add_argument("--resume-stage")
    args = parser.parse_args()
    model_dir = Path(args.model_dir).resolve(strict=False)
    expected_path = Path(__file__).with_name("model-artifacts.expected.json")
    expected = json.loads(expected_path.read_text(encoding="utf-8"))
    if expected.get("modelId") != MODEL_ID or expected.get("revision") != REVISION:
        print("frozen model artifact contract has the wrong identity", file=sys.stderr)
        return 3
    try:
        metadata = fetch_json(API)
        if not isinstance(metadata, dict) or metadata.get("sha") != REVISION or metadata.get("modelId") != MODEL_ID:
            raise ValueError("revision API response mismatch")
        siblings = {entry.get("rfilename") for entry in metadata.get("siblings", []) if isinstance(entry, dict)}
        if not set(expected["files"]).issubset(siblings):
            raise ValueError("revision does not contain the frozen artifact set")

        if model_dir.exists():
            manifest_path = model_dir / "model-manifest.json"
            if not model_dir.is_dir() or not manifest_path.is_file():
                raise ValueError("existing model destination is incomplete; refusing to modify it")
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            if (manifest.get("modelId") != MODEL_ID or manifest.get("revision") != REVISION or
                    manifest.get("targetToken") != ">>srp_Latn<<" or
                    manifest.get("expectedGitOids") != {name: entry["gitOid"] for name, entry in sorted(expected["files"].items())}):
                raise ValueError("existing model manifest does not match the frozen pin")
            allowed = set(expected["files"]) | {"model-manifest.json"}
            if {item.name for item in model_dir.iterdir()} != allowed or any(item.is_dir() for item in model_dir.iterdir()):
                raise ValueError("existing model destination has unexpected files")
            for filename, artifact in expected["files"].items():
                path = model_dir / filename
                if path.stat().st_size != artifact["size"]:
                    raise ValueError(f"existing model artifact size mismatch: {filename}")
                observed = sha256(path)
                expected_hash = artifact.get("lfsSha256")
                if expected_hash:
                    if observed != expected_hash:
                        raise ValueError("existing model LFS SHA-256 mismatch")
                elif git_blob_oid(path) != artifact["gitOid"]:
                    raise ValueError(f"existing model Git object mismatch: {filename}")
                if manifest.get("artifactHashes", {}).get(filename) != observed:
                    raise ValueError(f"existing model manifest artifact hash mismatch: {filename}")
            print("existing pinned OPUS artifact set re-verified; no files changed")
            return 0

        model_dir.parent.mkdir(parents=True, exist_ok=True)
        if args.resume_stage:
            stage = Path(args.resume_stage).resolve(strict=True)
            if stage.parent != model_dir.parent or not stage.name.startswith(".opus-mt-stage-") or not stage.is_dir():
                raise ValueError("resume stage must be an owned OPUS staging folder beside the target")
            allowed = set(expected["files"])
            if any(item.is_dir() or item.name not in allowed for item in stage.iterdir()):
                raise ValueError("resume stage contains an unexpected artifact")
        else:
            stage = Path(tempfile.mkdtemp(prefix=".opus-mt-stage-", dir=model_dir.parent))
        actual_sha256: dict[str, str] = {}
        for filename, artifact in expected["files"].items():
            destination = stage / filename
            if destination.exists():
                size = destination.stat().st_size
                observed_hash = sha256(destination)
                if size != artifact["size"]:
                    raise ValueError(f"existing staged artifact size mismatch: {filename}")
                if artifact.get("lfsSha256"):
                    if observed_hash != artifact["lfsSha256"]:
                        raise ValueError("existing staged LFS SHA-256 mismatch")
                elif git_blob_oid(destination) != artifact["gitOid"]:
                    raise ValueError(f"existing staged Git object mismatch: {filename}")
            else:
                url = f"https://huggingface.co/{MODEL_ID}/resolve/{REVISION}/{urllib.parse.quote(filename)}"
                request = urllib.request.Request(url, headers={"User-Agent": "CineKros-P2-translator-bootstrap/1.0"})
                digest = hashlib.sha256()
                size = 0
                with urllib.request.urlopen(request, timeout=90) as response, destination.open("xb") as output:
                    while True:
                        block = response.read(1024 * 1024)
                        if not block:
                            break
                        output.write(block)
                        digest.update(block)
                        size += len(block)
                if size != artifact["size"]:
                    raise ValueError(f"pinned artifact size verification failed: {filename}")
                observed_hash = digest.hexdigest()
                if artifact.get("lfsSha256"):
                    if observed_hash != artifact["lfsSha256"]:
                        raise ValueError("pinned model LFS SHA-256 verification failed")
                elif git_blob_oid(destination) != artifact["gitOid"]:
                    raise ValueError(f"pinned Git object verification failed: {filename}")
            actual_sha256[filename] = observed_hash

        manifest = {
            "schemaVersion": "opus-mt-pinned-model-v1",
            "modelId": MODEL_ID,
            "revision": REVISION,
            "targetToken": ">>srp_Latn<<",
            "license": "Apache-2.0",
            "sourceMetadata": {"revision": REVISION, "apiUrl": API},
            "expectedGitOids": {name: entry["gitOid"] for name, entry in sorted(expected["files"].items())},
            "artifactHashes": dict(sorted(actual_sha256.items())),
        }
        manifest_path = stage / "model-manifest.json"
        manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        model_dir.parent.mkdir(parents=True, exist_ok=True)
        os.rename(stage, model_dir)
        print("pinned OPUS artifact set downloaded and verified")
        return 0
    except (OSError, ValueError, urllib.error.URLError, json.JSONDecodeError):
        print("pinned OPUS bootstrap failed; no model artifact was accepted", file=sys.stderr)
        return 4


if __name__ == "__main__":
    raise SystemExit(main())
