"""Offline worker smoke test. Run from the repository root with the project venv."""
from __future__ import annotations

import base64
import json
import struct
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
PYTHON = ROOT / ".venv" / "Scripts" / "python.exe"
RESOURCES = ROOT / "src" / "OfflineSubtitleTranslator.App" / "Resources"
MODEL = ROOT / "models" / "asr" / "whisper-base"
TRANSLATOR = ROOT / "models" / "translators" / "argos"


def request(worker: Path, args: list[str], messages: list[dict]) -> list[dict]:
    process = subprocess.Popen(
        [str(PYTHON), "-u", str(worker), *args],
        cwd=ROOT,
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    assert process.stdin and process.stdout
    process.stdin.write(("\n".join(json.dumps(item, ensure_ascii=False) for item in messages) + "\n").encode("utf-8"))
    process.stdin.flush()
    lines = []
    expected = 1 + sum(item.get("type") != "shutdown" for item in messages)
    for _ in range(expected):
        raw = process.stdout.readline()
        if not raw:
            process.wait(timeout=60)
            error = process.stderr.read().decode("utf-8", errors="replace")
            raise RuntimeError(f"worker ended before response (exit={process.returncode}): {error}")
        lines.append(json.loads(raw.decode("ascii")))
    process.wait(timeout=60)
    if process.returncode != 0:
        raise RuntimeError(process.stderr.read().decode("utf-8", errors="replace"))
    return lines


def main() -> int:
    if not PYTHON.exists():
        raise FileNotFoundError(PYTHON)
    translation = request(
        RESOURCES / "translate_worker.py",
        ["--engine", "argos", "--model", str(TRANSLATOR), "--languages", "ja,en"],
        [
            {"type": "translate", "source": "en", "target": "zh", "text": "Hello, how are you?"},
            {"type": "translate", "source": "ja", "target": "zh", "text": "これはテストです。"},
            {"type": "shutdown"},
        ],
    )
    assert translation[0].get("ready") is True
    assert translation[1].get("text")
    assert translation[2].get("text")

    silence = struct.pack("<16000f", *([0.0] * 16000))
    asr = request(
        RESOURCES / "asr_worker.py",
        ["--model", str(MODEL), "--device", "cpu"],
        [
            {"type": "transcribe", "sample_rate": 16000, "audio_base64": base64.b64encode(silence).decode(), "language": "auto"},
            {"type": "shutdown"},
        ],
    )
    assert asr[0].get("ready") is True
    assert "text" in asr[1]
    print("worker smoke test passed")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"worker smoke test failed: {exc}", file=sys.stderr)
        raise
