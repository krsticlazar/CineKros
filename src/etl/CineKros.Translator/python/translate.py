#!/usr/bin/env python3
"""Offline-only JSONL runner for the single pinned Marian model."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
from pathlib import Path

MODEL_ID = "Helsinki-NLP/opus-mt-en-sla"
REVISION = "0bc26914f2f82c3dd5b235e420aa2c711a5ed3d8"
TARGET = ">>srp_Latn<<"
TARGET_ID = 36
MAX_SOURCE_TOKENS = 512
MAX_NEW_TOKENS = 32
NUM_BEAMS = 4
SMOKE_PHRASES = {
    "dark",
    "friendship",
    "camp",
    "feel-good",
    "beautiful animation",
    "psychological mind games",
}


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _git_blob_oid(path: Path) -> str:
    size = path.stat().st_size
    digest = hashlib.sha1()
    digest.update(f"blob {size}\0".encode("ascii"))
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _load_model(model_dir: Path):
    # Import only after validating the local artifact set; no model-code auto-loading.
    import torch
    from transformers import MarianMTModel, MarianTokenizer

    manifest_path = model_dir / "model-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if (manifest.get("schemaVersion") != "opus-mt-pinned-model-v1" or
            manifest.get("modelId") != MODEL_ID or manifest.get("revision") != REVISION or
            manifest.get("targetToken") != TARGET or manifest.get("license") != "Apache-2.0"):
        raise ValueError("model manifest identity mismatch")
    expected_path = Path(__file__).resolve().parent.parent / "bootstrap" / "model-artifacts.expected.json"
    expected_files = json.loads(expected_path.read_text(encoding="utf-8"))["files"]
    if (set(manifest.get("expectedGitOids", {})) != set(expected_files) or
            set(manifest.get("artifactHashes", {})) != set(expected_files)):
        raise ValueError("model manifest artifact contract mismatch")
    for filename, actual_sha256 in manifest["artifactHashes"].items():
        expected_record = expected_files[filename]
        artifact_path = model_dir / filename
        if (manifest["expectedGitOids"].get(filename) != expected_record["gitOid"] or
                artifact_path.stat().st_size != expected_record["size"] or
                _sha256(artifact_path) != actual_sha256):
            raise ValueError("model artifact hash mismatch")
        if expected_record.get("lfsSha256"):
            if actual_sha256 != expected_record["lfsSha256"]:
                raise ValueError("model LFS hash mismatch")
        elif _git_blob_oid(artifact_path) != expected_record["gitOid"]:
            raise ValueError("model Git object mismatch")

    tokenizer = MarianTokenizer.from_pretrained(str(model_dir), local_files_only=True)
    model = MarianMTModel.from_pretrained(str(model_dir), local_files_only=True)
    if tokenizer.get_vocab().get(TARGET) != TARGET_ID:
        raise ValueError("pinned Serbian Latin target token ID mismatch")
    if model.device.type != "cpu":
        model.to("cpu")
    model.eval()
    torch.set_num_threads(1)
    return torch, tokenizer, model


def _validate_job(job: object) -> tuple[str, str, str, str]:
    if not isinstance(job, dict) or set(job) != {"en", "text", "target", "settingsId"}:
        raise ValueError("invalid_job_shape")
    en, text, target, settings_id = (job.get(key) for key in ("en", "text", "target", "settingsId"))
    if not all(isinstance(value, str) and value for value in (en, text, target, settings_id)):
        raise ValueError("invalid_job_fields")
    if en not in SMOKE_PHRASES or text != en:
        raise ValueError("job_outside_frozen_smoke")
    if target != TARGET or settings_id != "marian-do-sample-false-beam4-maxnew32-v1":
        raise ValueError("unsupported_inference_settings")
    return en, text, target, settings_id


def _translate(torch, tokenizer, model, en: str, text: str) -> dict[str, object]:
    if TARGET in text or ">>srp_Cyrl<<" in text:
        return {"en": en, "machine": None, "completed": False, "error": "source_contains_target_token"}
    encoded = tokenizer(TARGET + " " + text, return_tensors="pt", truncation=False)
    if int(encoded["input_ids"].shape[-1]) > MAX_SOURCE_TOKENS:
        return {"en": en, "machine": None, "completed": False, "error": "source_too_long"}
    encoded = {name: tensor.to("cpu") for name, tensor in encoded.items() if name in {"input_ids", "attention_mask"}}
    if set(encoded) != {"input_ids", "attention_mask"}:
        return {"en": en, "machine": None, "completed": False, "error": "unexpected_tokenizer_inputs"}
    try:
        with torch.inference_mode():
            generated = model.generate(
                **encoded,
                do_sample=False,
                num_beams=NUM_BEAMS,
                max_new_tokens=MAX_NEW_TOKENS,
            )
    except Exception:
        return {"en": en, "machine": None, "completed": False, "error": "inference_failed"}
    token_ids = generated[0].tolist()
    eos = model.generation_config.eos_token_id
    eos_ids = set(eos if isinstance(eos, list) else [eos]) if eos is not None else set()
    reached_eos = bool(token_ids) and token_ids[-1] in eos_ids
    generated_count = max(0, len(token_ids) - 1)  # exclude the decoder start token
    machine = tokenizer.decode(token_ids, skip_special_tokens=True).strip()
    if not reached_eos or generated_count >= MAX_NEW_TOKENS:
        return {"en": en, "machine": machine, "completed": False, "error": "incomplete_generation"}
    if not machine:
        return {"en": en, "machine": "", "completed": False, "error": "empty_generation"}
    return {"en": en, "machine": machine, "completed": True, "error": None}


def main() -> int:
    sys.stdin.reconfigure(encoding="utf-8", errors="strict")
    sys.stdout.reconfigure(encoding="utf-8", errors="strict")
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--model-dir", required=True)
    args = parser.parse_args()
    os.environ.setdefault("HF_HUB_DISABLE_PROGRESS_BARS", "1")
    os.environ.setdefault("TRANSFORMERS_OFFLINE", "1")
    try:
        torch, tokenizer, model = _load_model(Path(args.model_dir))
    except Exception:
        print("model startup failed; verify the pinned offline artifacts and locked environment", file=sys.stderr)
        return 3

    seen: set[str] = set()
    count = 0
    for line in sys.stdin:
        try:
            job = json.loads(line)
            en, text, _, _ = _validate_job(job)
            if en in seen or count >= 6:
                raise ValueError("duplicate_or_over_limit_job")
            seen.add(en)
            count += 1
            response = _translate(torch, tokenizer, model, en, text)
        except Exception:
            print("invalid JSONL job received", file=sys.stderr)
            return 4
        sys.stdout.write(json.dumps(response, ensure_ascii=False, separators=(",", ":")) + "\n")
        sys.stdout.flush()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
