"""Offline ASR calibration. Emits a recommendation; the app applies it with backup.

Never pass reference text to the worker. Tune on grouped development data, then
validate only the winner on held-out data. No downloads or settings writes here.
"""
from __future__ import annotations

import argparse
import base64
from collections import defaultdict
import hashlib
import json
import math
from pathlib import Path
import random
import re
import statistics
import sys
import time

import galgame_bench as bench

STAMP = r"(\d{2,}):(\d{2}):(\d{2})[,.](\d{3})"
TIMING = re.compile(rf"^{STAMP}\s*-->\s*{STAMP}\s*$")


def emit(message):
    print(json.dumps(message, ensure_ascii=True), flush=True)


def read_srt(path):
    text = Path(path).read_text(encoding="utf-8-sig").replace("\r\n", "\n")
    rows = []
    for block in re.split(r"\n\s*\n", text.strip()):
        lines = block.splitlines()
        if lines and lines[0].strip().isdigit():
            lines.pop(0)
        match = TIMING.fullmatch(lines[0].strip()) if lines else None
        if not match:
            raise ValueError("SRT contains an invalid timestamp; use UTF-8 original-language SRT")
        n = [int(v) for v in match.groups()]
        if any(n[i] >= 60 for i in (1, 2, 5, 6)):
            raise ValueError("Invalid SRT minutes/seconds")
        start = n[0] * 3600 + n[1] * 60 + n[2] + n[3] / 1000
        end = n[4] * 3600 + n[5] * 60 + n[6] + n[7] / 1000
        original = " ".join(lines[1:]).strip()
        if not original or "<" in original or "{" in original:
            raise ValueError("Use plain original text in SRT; remove markup and translated lines first")
        if end <= start or end - start > 120 or (rows and start < rows[-1]["end"]):
            raise ValueError("SRT cues must be ordered, non-overlapping and 0–120 seconds long")
        rows.append(dict(start=start, end=end, text=original))
    if not rows:
        raise ValueError("Empty SRT")
    return rows


def cut_recording(source, cues, folder):
    """Decode a long recording once, retaining only the current cue's samples."""
    import av
    import numpy as np
    ranges = [(round(r["start"] * bench.RATE), round(r["end"] * bench.RATE)) for r in cues]
    resampler = av.AudioResampler(format="fltp", layout="mono", rate=bench.RATE)
    position, index, pieces = 0, 0, []
    paths = []

    def consume(data):
        nonlocal position, index, pieces
        limit = position + len(data)
        while index < len(ranges) and ranges[index][0] < limit:
            start, end = ranges[index]
            a, b = max(start, position), min(end, limit)
            if b > a:
                pieces.append(data[a - position:b - position].copy())
            if end > limit:
                break
            path = folder / f"cue-{index:06d}.wav"
            if not pieces:
                raise ValueError("Empty audio cue")
            bench.save_wave(path, np.concatenate(pieces))
            paths.append(path)
            pieces = []
            index += 1
        position = limit

    with av.open(str(source)) as container:
        if len(container.streams.audio) != 1:
            raise ValueError("Recording must have exactly one audio stream")
        for frame in container.decode(audio=0):
            frame.pts = None
            for converted in resampler.resample(frame):
                consume(converted.to_ndarray().reshape(-1))
            if index == len(cues):
                break
        if index < len(cues):
            for converted in resampler.resample(None):
                consume(converted.to_ndarray().reshape(-1))
    if index != len(cues):
        raise ValueError("SRT extends beyond the recording; no calibration performed")
    return paths


def load_dataset(config, out):
    rows = []
    if config.get("audio"):
        if not config.get("verified"):
            raise ValueError("Original text, voiced status and timestamps must be verified")
        cues = read_srt(config["inputs"][0])
        # Contiguous temporal blocks, never random adjacent cues from a single recording.
        # This is within-recording validation, not evidence of cross-game generalization.
        paths = cut_recording(config["audio"], cues, out)
        block_size = max(4, math.ceil(len(cues) / 6))
        for i, (cue, path) in enumerate(zip(cues, paths)):
            rows.append(dict(id=f"cue-{i}", audio=str(path.resolve()), text=cue["text"], language=config["language"],
                scene=f"recording-block-{i // block_size}", game="user-recording", verified_original=True,
                source=f"{config['inputs'][0]}#{i + 1}", kind="game-mix"))
    else:
        for filename in config["inputs"]:
            path = Path(filename)
            for row in bench.read_rows(path):
                row = dict(row)
                row["audio"] = str((path.parent / row["audio"]).resolve())
                rows.append(row)
    ids = set()
    for row in rows:
        for key in ("id", "audio", "text", "language", "scene", "game"):
            if not isinstance(row.get(key), str) or not row[key].strip():
                raise ValueError(f"Missing required field: {key}")
        if row["id"] in ids:
            raise ValueError("Duplicate sample IDs; do not import multiple exports of the same session")
        ids.add(row["id"])
        if str(row.get("verified_original", "")).lower() not in ("true", "1"):
            raise ValueError("Unverified samples found; confirm pairing and voiced original text first")
        if row["language"] not in ("ja", "en"):
            raise ValueError("Only Japanese or English original text is supported, not Chinese translation")
        samples = bench.audio(row["audio"])
        if len(samples) < bench.RATE // 4 or not bench.normalize(row["text"], row["language"]).replace(" ", ""):
            raise ValueError("Empty/too-short sample; exclude unvoiced cues")
        # Reject truly silent material. This is not a speech classifier: BGM still needs human review.
        if float((samples ** 2).mean()) < 1e-8:
            raise ValueError(f"Silent audio with nonempty reference: {row['id']}")
        row.update(pcm_sha256=bench.pcm_hash(samples), duration_s=len(samples) / bench.RATE)
    if len(rows) < 12:
        raise ValueError("At least 12 verified voiced samples are required; no settings changed")
    return rows


def partition(rows, seed=20261015):
    parent = {(r["game"], r["scene"]): (r["game"], r["scene"]) for r in rows}

    def find(s):
        while parent[s] != s:
            parent[s] = parent[parent[s]]
            s = parent[s]
        return s

    seen, labels = {}, {}
    for row in rows:
        group = (row["game"], row["scene"])
        ref = (row["language"], bench.normalize(row["text"], row["language"]).replace(" ", ""))
        key = row["pcm_sha256"]
        if key in labels and labels[key] != ref:
            raise ValueError("Identical audio has conflicting references")
        labels[key] = ref
        for key in (("audio", key), ("text", *ref)):
            if key in seen:
                parent[find(group)] = find(seen[key])
            else:
                seen[key] = group
    groups = sorted({find(s) for s in parent})
    if len(groups) < 3:
        raise ValueError("Need >=3 independent scenes/temporal blocks after duplicate grouping; collect more data")
    random.Random(seed).shuffle(groups)
    holdout = set(groups[:max(1, round(len(groups) * .3))])
    for row in rows:
        row["group"] = str(find((row["game"], row["scene"])))
        row["split"] = "holdout" if find((row["game"], row["scene"])) in holdout else "tune"
    for split in ("tune", "holdout"):
        subset = [r for r in rows if r["split"] == split]
        if len(subset) < 4 or sum(len(bench.normalize(r["text"], r["language"]).replace(" ", "")) for r in subset) < 40:
            raise ValueError(f"Too little independent {split} data; add scenes with voiced sentences")
    if {r["language"] for r in rows if r["split"] == "tune"} != {r["language"] for r in rows if r["split"] == "holdout"}:
        raise ValueError("Each language must occur in both tuning and held-out scenes")
    return rows


def evaluate(config, candidate, rows, folder, deadline, repeats=1):
    folder.mkdir()
    results = []
    worker_path = Path(config.get("worker_path", bench.ROOT / "src/ACGTranslator.App/Resources/asr_worker.py"))
    command = [sys.executable, str(worker_path),
        "--model", candidate["model"], "--device", candidate["device"], "--beam-size", str(candidate["beam"])]
    if config.get("turn_model"):
        command += ["--turn-model", config["turn_model"]]
    for path in candidate.get("dll_dirs", []):
        command += ["--dll-dir", path]
    for repeat in range(repeats):
        remaining = deadline - time.monotonic()
        if remaining < 1:
            raise TimeoutError("Calibration time budget exhausted")
        worker = bench.Worker(command, folder / f"worker-{repeat}.log", min(120, remaining))
        try:
            for i, row in enumerate(rows):
                remaining = deadline - time.monotonic()
                if remaining < 1:
                    raise TimeoutError("Calibration time budget exhausted")
                samples = bench.checked_audio(row)
                request = {"type": "transcribe", "sample_rate": bench.RATE,
                    "audio_base64": base64.b64encode(samples.tobytes()).decode("ascii"),
                    "language": candidate["language"], "turn_enhancement": bool(config.get("turn_model"))}
                worker.timeout = min(120, remaining)
                begin = time.perf_counter()
                reply = worker.exchange(request)
                elapsed = time.perf_counter() - begin
                if reply.get("error") or reply.get("warning"):
                    raise RuntimeError(reply.get("error") or reply["warning"])
                separator = " " if row["language"] == "en" else ""
                text = separator.join(s["text"] for s in reply["segments"]) if reply.get("segments") else reply.get("text", "")
                results.append(dict(id=row["id"], repeat=repeat, language=row["language"], group=row["group"],
                    reference=row["text"], hypothesis=text, elapsed_s=elapsed, duration_s=row["duration_s"],
                    **bench.score(row["text"], text, row["language"])))
                emit(dict(event="progress", candidate=candidate["name"], phase=rows[0]["split"],
                          sample=i + 1, total=len(rows), repeat=repeat + 1))
        finally:
            worker.close()
    bench.write_manifest(folder / "results.jsonl", results)
    metrics = bench.aggregate(results)
    chars = sum(r["ref_chars"] for r in results)
    metrics["cer"] = sum(r["char_edits"] for r in results) / chars
    bench.write_json(folder / "metrics.json", metrics)
    return metrics


def improved(candidate, baseline, mode, max_rtf):
    """Gate each language separately. Latency claims need at least 10% improvement."""
    if candidate["rtf"] > max_rtf:
        return False
    tolerance = .005 if mode == "speed" else 0.0
    if set(candidate["languages"]) != set(baseline["languages"]):
        return False
    for lang, old in baseline["languages"].items():
        new = candidate["languages"][lang]
        if new["cer"] > old["cer"] + tolerance:
            return False
        if lang == "en" and new["wer"] > old["wer"] + tolerance:
            return False
    quality = candidate["cer"] <= baseline["cer"] - .01
    faster = candidate["p95_s"] <= baseline["p95_s"] * .9
    if mode == "accuracy":
        return quality or (candidate["cer"] <= baseline["cer"] and faster)
    if mode == "speed":
        return faster
    return (quality and candidate["p95_s"] <= baseline["p95_s"] * 1.1) or faster


def calibrate(config, out):
    deadline = time.monotonic() + config.get("budget_seconds", 900)
    rows = partition(load_dataset(config, out))
    bench.write_manifest(out / "dataset.jsonl", rows)
    candidates = config["candidates"]
    if not candidates or len(candidates) > 16:
        raise ValueError("Expected 1–16 configurations, baseline first")
    models = {}
    for c in candidates:
        if c["device"] not in ("cpu", "cuda") or c["beam"] not in (1, 2, 5) or c["language"] not in ("ja", "en", "auto"):
            raise ValueError("Unsupported candidate parameters")
        model = Path(c["model"]).resolve()
        if not (model / "model.bin").is_file():
            raise ValueError("Only installed local models can be calibrated")
        c["model"] = str(model)
        if str(model) not in models:
            models[str(model)] = {p.name: bench.file_hash(p) for p in sorted(model.iterdir()) if p.is_file()}
    bench.write_json(out / "provenance.json", dict(model_files=models, dataset_sha256=bench.file_hash(out / "dataset.jsonl"),
        calibrator_sha256=bench.file_hash(__file__), benchmark_sha256=bench.file_hash(bench.__file__),
        workers={p.name: bench.file_hash(p) for p in Path(config.get("worker_path", bench.ROOT / "src/ACGTranslator.App/Resources/asr_worker.py")).parent.glob("*.py")},
        turn_model_sha256=bench.file_hash(config["turn_model"]) if config.get("turn_model") else None,
        python=sys.version, scope="Worker-only; within-recording blocks for SRT; no game rendering load measurement"))
    tune = [r for r in rows if r["split"] == "tune"]
    held = [r for r in rows if r["split"] == "holdout"]
    trials = []
    mode, max_rtf = config.get("mode", "balanced"), config.get("max_rtf", 1.)
    baseline = evaluate(config, candidates[0], tune, out / "tune-0", deadline)
    trials.append(dict(candidate=candidates[0], metrics=baseline))
    for index, candidate in enumerate(candidates[1:], 1):
        try:
            metrics = evaluate(config, candidate, tune, out / f"tune-{index}", deadline)
            trials.append(dict(candidate=candidate, metrics=metrics))
        except TimeoutError:
            raise
        except Exception as exc:
            trials.append(dict(candidate=candidate, error=str(exc)))
            emit(dict(event="candidate_failed", candidate=candidate["name"], message=str(exc)))
        bench.write_json(out / "trials.json", trials)
    eligible = [r for r in trials[1:] if "metrics" in r and improved(r["metrics"], baseline, mode, max_rtf)]
    report = dict(eligible=False, reason="No configuration improved the development results", trials=trials,
                  tune_samples=len(tune), holdout_samples=len(held), settings_written=False)
    if not eligible:
        return report
    rank = (lambda r: (r["metrics"]["p95_s"], r["metrics"]["cer"])) if mode == "speed" else (lambda r: (r["metrics"]["cer"], r["metrics"]["p95_s"]))
    winner = min(eligible, key=rank)
    # Only one winner touches the held-out set. Do not select another after a failed validation.
    old = evaluate(config, candidates[0], held, out / "holdout-baseline", deadline, repeats=2)
    new = evaluate(config, winner["candidate"], held, out / "holdout-winner", deadline, repeats=2)
    passed = improved(new, old, mode, max_rtf)
    report.update(eligible=passed, recommendation=winner["candidate"], baseline_holdout=old,
        winner_holdout=new, reason="Held-out validation passed" if passed else "Winner did not improve held-out results; keep original settings")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()
    # The app creates the run directory containing config/backup first.
    args.out.mkdir(parents=True, exist_ok=True)
    if (args.out / "report.json").exists() or (args.out / "dataset.jsonl").exists():
        parser.error("Run directory already contains results")
    config = json.loads(args.config.read_text(encoding="utf-8-sig"))
    try:
        report = calibrate(config, args.out)
        bench.write_json(args.out / "report.json", report)
        emit(dict(event="complete", eligible=report["eligible"], reason=report["reason"]))
        return 0
    except Exception as exc:
        report = dict(eligible=False, reason=f"{type(exc).__name__}: {exc}", settings_written=False)
        bench.write_json(args.out / "report.json", report)
        emit(dict(event="failed", message=report["reason"]))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
