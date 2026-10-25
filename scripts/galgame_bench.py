"""Offline asset-based ASR benchmark: prepare, cases, run, compare. See docs/galgame-benchmark.md."""
from __future__ import annotations

import argparse
import base64
from collections import Counter, defaultdict
import csv
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import platform
import queue
import random
import statistics
import subprocess
import sys
import threading
import time
import unicodedata
import wave

ROOT = Path(__file__).resolve().parents[1]
RATE = 16000


def digest(data):
    return hashlib.sha256(data).hexdigest()


def file_hash(path):
    value = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            value.update(chunk)
    return value.hexdigest()


def write_json(path, value):
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def write_manifest(path, rows):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8") as stream:
        for row in rows:
            stream.write(json.dumps(row, ensure_ascii=False) + "\n")


def read_rows(path):
    with Path(path).open(encoding="utf-8-sig", newline="") as stream:
        rows = list(csv.DictReader(stream)) if Path(path).suffix.lower() == ".csv" else [json.loads(line) for line in stream if line.strip()]
    if not rows:
        raise ValueError("Empty dataset")
    return rows


def normalize(text, language):
    text = unicodedata.normalize("NFKC", text)
    if language == "en":
        text = text.casefold()
    # Keep Japanese kanji/kana distinctions. Ignore punctuation, symbols and whitespace for CER.
    text = "".join(" " if unicodedata.category(c)[0] in "PS" else c for c in text)
    return " ".join(text.split())


def distance(a, b):
    previous = list(range(len(b) + 1))
    for i, x in enumerate(a, 1):
        current = [i]
        for j, y in enumerate(b, 1):
            current.append(min(current[-1] + 1, previous[j] + 1, previous[j - 1] + (x != y)))
        previous = current
    return previous[-1]


def score(reference, hypothesis, language):
    ref, hyp = normalize(reference, language), normalize(hypothesis, language)
    chars, predicted = ref.replace(" ", ""), hyp.replace(" ", "")
    result = {"char_edits": distance(chars, predicted), "ref_chars": len(chars),
              "exact": chars == predicted, "false_text": not chars and bool(predicted)}
    if language == "en":
        result.update(word_edits=distance(ref.split(), hyp.split()), ref_words=len(ref.split()))
    return result


def audio(path):
    import av
    import numpy as np
    # Use PyAV directly: some installed versions lack decode_audio's metadata_errors kwarg.
    parts, count = [], 0
    resampler = av.AudioResampler(format="fltp", layout="mono", rate=RATE)
    with av.open(str(path), mode="r") as container:
        if len(container.streams.audio) != 1:
            raise ValueError(f"Expected exactly one audio stream: {path}")
        for frame in container.decode(audio=0):
            frame.pts = None
            for converted in resampler.resample(frame):
                block = converted.to_ndarray().reshape(-1)
                count += len(block)
                if count > RATE * 120:
                    raise ValueError(f"Audio exceeds 120 seconds: {path}")
                parts.append(block)
        for converted in resampler.resample(None):
            parts.append(converted.to_ndarray().reshape(-1))
    samples = np.concatenate(parts) if parts else np.zeros(0, dtype="<f4")
    if not len(samples) or len(samples) > RATE * 120 or not np.isfinite(samples).all():
        raise ValueError(f"Audio must be finite, nonempty and at most 120 seconds: {path}")
    return np.asarray(samples, dtype="<f4")


def pcm_hash(samples):
    return digest(samples.astype("<f4").tobytes())


def prepare(args):
    rows = read_rows(args.input)
    seen = set()
    for index, row in enumerate(rows):
        for key in ("id", "audio", "text", "language", "game", "scene", "source"):
            if key not in row or (key != "text" and not str(row[key]).strip()):
                raise ValueError(f"Row {index + 1}: required field {key}")
        if row["id"] in seen:
            raise ValueError(f"Duplicate id: {row['id']}")
        seen.add(row["id"])
        if row["language"] not in ("ja", "en"):
            raise ValueError("Reference language must be ja or en, never a Chinese translation")
        if str(row.get("verified_original", "")).lower() not in ("true", "1"):
            raise ValueError(f"{row['id']}: source/audio pairing not verified; do not treat guesses as gold labels")
        path = (args.input.parent / row["audio"]).resolve()
        samples = audio(path)
        row.update(audio=str(path), pcm_sha256=pcm_hash(samples), duration_s=len(samples) / RATE,
                   kind=row.get("kind") or "clean", schema=1)
        if not normalize(row["text"], row["language"]).replace(" ", ""):
            raise ValueError("Import requires voiced original text; use cases to generate explicit negative controls")
        if str(row.get("order", "")).strip():
            row["order"] = int(row["order"])
        else:
            row.pop("order", None)

    # Union scenes connected by duplicate audio OR exact normalized text before splitting.
    # Conservative: repeated phrases may collapse many scenes. Fail instead of leaking test labels.
    scenes = sorted({(r["game"], r["scene"]) for r in rows})
    parent = {s: s for s in scenes}

    def find(s):
        while parent[s] != s:
            parent[s] = parent[parent[s]]
            s = parent[s]
        return s

    duplicates, transcripts = {}, {}
    for row in rows:
        scene = (row["game"], row["scene"])
        text = normalize(row["text"], row["language"]).replace(" ", "")
        key = row["pcm_sha256"]
        if key in transcripts and transcripts[key] != (row["language"], text):
            raise ValueError("Identical decoded audio has conflicting original text/language")
        transcripts[key] = (row["language"], text)
        for item in (("audio", key), ("text", row["language"], text)):
            if item in duplicates:
                parent[find(scene)] = find(duplicates[item])
            else:
                duplicates[item] = scene
    groups = sorted({find(s) for s in scenes})
    random.Random(args.seed).shuffle(groups)
    if not args.smoke_only and len(groups) < 3:
        raise ValueError("Need >=3 independent scene groups after deduplication; --smoke-only cannot produce accuracy claims")
    n = max(1, round(len(groups) * 0.2))
    assigned = {group: ("smoke" if args.smoke_only else "test" if i < n else "dev" if i < 2 * n else "train") for i, group in enumerate(groups)}
    for row in rows:
        group = find((row["game"], row["scene"]))
        row.update(split=assigned[group], group=digest(json.dumps(group).encode())[:16], split_seed=args.seed)
    write_manifest(args.out, rows)
    print(json.dumps({"samples": len(rows), "independent_groups": len(groups), "splits": dict(Counter(r["split"] for r in rows))}))


def save_wave(path, samples):
    import numpy as np
    samples = np.clip(samples, -1, 32767 / 32768)
    with wave.open(str(path), "wb") as stream:
        stream.setparams((1, 2, RATE, 0, "NONE", "not compressed"))
        stream.writeframes((samples * 32768).round().astype("<i2").tobytes())


def checked_audio(row):
    samples = audio(row["audio"])
    if pcm_hash(samples) != row["pcm_sha256"]:
        raise ValueError(f"Audio changed since preparation: {row['id']}")
    return samples


def cases(args):
    import numpy as np
    rows = read_rows(args.manifest)
    args.out.mkdir(parents=True, exist_ok=False)
    generated = []

    def add(samples, originals, kind, text, extra=None):
        identifier = digest(json.dumps([r["id"] for r in originals]).encode() + kind.encode())[:24]
        path = args.out / (identifier + ".wav")
        save_wave(path, samples)
        row = {key: originals[0][key] for key in ("game", "scene", "language", "split", "group")}
        row.update(id=identifier, audio=str(path.resolve()), text=text, kind=kind, source_ids=[r["id"] for r in originals],
                   duration_s=len(samples) / RATE, pcm_sha256=pcm_hash(audio(path)), schema=1)
        row.update(extra or {})
        generated.append(row)

    scenes = defaultdict(list)
    for row in rows:
        if row.get("kind") == "clean" and "order" in row:
            scenes[(row["game"], row["scene"], row["language"], row["split"])].append(row)
    pairs = 0
    for group in scenes.values():
        group.sort(key=lambda row: row["order"])
        if len({r["order"] for r in group}) != len(group):
            raise ValueError("Duplicate order within scene")
        for left, right in zip(group, group[1:]):
            if right["order"] != left["order"] + 1:
                continue  # Gaps may contain unvoiced dialogue; never invent adjacency.
            if pairs >= args.max_pairs:
                break
            a, b = checked_audio(left), checked_audio(right)
            if len(a) + len(b) > 119 * RATE:
                continue
            known = bool(left.get("speaker")) and bool(right.get("speaker"))
            turn = left["speaker"] != right["speaker"] if known else None
            for ms in (0, 80, 150, 300):
                gap = np.zeros(round(ms * RATE / 1000), dtype="<f4")
                text = left["text"] + (" " if left["language"] == "en" else "") + right["text"]
                add(np.concatenate([a, gap, b]), [left, right], f"gap-{ms}ms", text,
                    {"expected_speaker_change": turn, "asset_join_s": len(a) / RATE,
                     "note": "Asset join, not a manually annotated acoustic boundary; internal silence retained."})
            pairs += 1
    # Negative control, and optional fixed BGM stress for the same selected originals.
    for language, split in sorted({(r["language"], r["split"]) for r in rows}):
        first = next(r for r in rows if r["language"] == language and r["split"] == split)
        add(np.zeros(3 * RATE, dtype="<f4"), [first], "silence", "")
    if args.bgm:
        music = audio(args.bgm)
        for row in rows[:args.max_pairs]:
            speech = checked_audio(row)
            noise = np.resize(music, len(speech))
            # Full-clip RMS SNR, not voiced-only SNR. Record the convention in metadata.
            noise_rms, voice_rms = np.sqrt(np.mean(noise ** 2)), np.sqrt(np.mean(speech ** 2))
            if noise_rms < 1e-7 or voice_rms < 1e-7:
                raise ValueError("BGM/speech RMS too low for SNR mixing")
            mixed = speech + noise * voice_rms / (noise_rms * 10 ** (args.snr / 20))
            mixed /= max(1., float(np.max(np.abs(mixed))) / .98)
            add(mixed, [row], f"bgm-{args.snr:g}dB", row["text"],
                {"bgm_sha256": file_hash(args.bgm), "snr_definition": "full-clip RMS"})
        for language, split in sorted({(r["language"], r["split"]) for r in rows}):
            first = next(r for r in rows if r["language"] == language and r["split"] == split)
            add(music, [first], "music-only", "", {"bgm_sha256": file_hash(args.bgm),
                "note": "Negative control valid only when the supplied BGM contains no vocals."})
    write_manifest(args.out / "manifest.jsonl", generated)
    print(json.dumps({"pairs": pairs, "cases": len(generated), "manifest": str(args.out / "manifest.jsonl")}, ensure_ascii=True))


class Worker:
    def __init__(self, command, log, timeout):
        self.timeout = timeout
        self.log = log.open("w", encoding="utf-8")
        env = dict(os.environ, HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", PYTHONIOENCODING="utf-8")
        try:
            self.process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                stderr=self.log, text=True, encoding="utf-8", env=env,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
            ready = self.exchange(None)
            if not ready.get("ready"):
                raise RuntimeError(f"Worker did not become ready: {ready}")
        except BaseException:
            self.close()
            raise

    def exchange(self, request):
        result = queue.Queue()

        def communicate():
            try:
                if request is not None:
                    self.process.stdin.write(json.dumps(request) + "\n")
                    self.process.stdin.flush()
                line = self.process.stdout.readline()
                if not line:
                    raise RuntimeError("Worker exited before replying; see worker.stderr.log")
                result.put((True, json.loads(line)))
            except Exception as exc:
                result.put((False, exc))

        threading.Thread(target=communicate, daemon=True).start()
        try:
            ok, value = result.get(timeout=self.timeout)
        except queue.Empty:
            self.process.kill()
            self.process.wait()
            raise TimeoutError(f"Worker exceeded {self.timeout:g}s; run aborted to avoid response misalignment") from None
        if not ok:
            raise value
        return value

    def close(self):
        process = getattr(self, "process", None)
        if process is not None:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
            for pipe in (process.stdin, process.stdout):
                if pipe:
                    pipe.close()
        self.log.close()


def aggregate(rows):
    metrics = {"samples": len(rows), "errors": sum(bool(r.get("error")) for r in rows)}
    durations = [r["elapsed_s"] for r in rows]
    if not rows:
        return metrics
    metrics.update(p50_s=statistics.median(durations), p95_s=sorted(durations)[max(0, (95 * len(rows) + 99) // 100 - 1)],
                   rtf=sum(durations) / sum(r["duration_s"] for r in rows),
                   turn_status=dict(Counter(r.get("turn_status", "off") for r in rows)))
    turns = [r for r in rows if isinstance(r.get("expected_speaker_change"), bool)]
    if turns:
        tp = sum(r["expected_speaker_change"] and r.get("turn_status") == "split" for r in turns)
        fp = sum(not r["expected_speaker_change"] and r.get("turn_status") == "split" for r in turns)
        fn = sum(r["expected_speaker_change"] and r.get("turn_status") != "split" for r in turns)
        metrics["synthetic_turn_detection"] = {
            "labelled_samples": len(turns), "true_positive": tp, "false_positive": fp, "false_negative": fn,
            "true_negative": len(turns) - tp - fp - fn,
            "precision": tp / (tp + fp) if tp + fp else None,
            "recall": tp / (tp + fn) if tp + fn else None,
            "scope": "Script speaker labels on concatenated assets; not real-world diarization or boundary accuracy."}
    metrics["languages"] = {}
    for language in sorted({r["language"] for r in rows}):
        items = [r for r in rows if r["language"] == language]
        chars = sum(r["ref_chars"] for r in items)
        words = sum(r.get("ref_words", 0) for r in items)
        negative = [r for r in items if not r["ref_chars"]]
        metrics["languages"][language] = {
            "cer": sum(r["char_edits"] for r in items if r["ref_chars"]) / chars if chars else None,
            "wer": sum(r.get("word_edits", 0) for r in items if r.get("ref_words")) / words if words else None,
            "negative_samples": len(negative), "false_text_samples": sum(r["false_text"] for r in negative)}
    return metrics


def run(args):
    rows = [r for r in read_rows(args.manifest) if r["split"] == args.split]
    available = len(rows)
    rows = rows[:args.limit] if args.limit else rows
    if not rows or len({r["id"] for r in rows}) != len(rows):
        raise ValueError("Empty split or duplicate IDs")
    if not args.model.is_dir() or not (args.model / "model.bin").is_file():
        raise ValueError("--model must be a local faster-whisper model directory")
    if args.turns and (not args.turn_model or not args.turn_model.is_file()):
        raise ValueError("--turns requires an existing --turn-model ONNX file")
    args.out.mkdir(parents=True, exist_ok=False)
    worker_path = ROOT / "src/ACGTranslator.App/Resources/asr_worker.py"
    command = [sys.executable, str(worker_path), "--model", str(args.model.resolve()),
               "--device", args.device, "--beam-size", str(args.beam)]
    if args.turns:
        command += ["--turn-model", str(args.turn_model.resolve())]
    for path in args.dll_dir:
        command += ["--dll-dir", str(path)]
    metadata = {"manifest_sha256": file_hash(args.manifest), "split": args.split,
        "benchmark_sha256": file_hash(__file__),
        "selected": len(rows), "available": available, "partial_selection": len(rows) < available,
        "command": command, "python": sys.version, "platform": platform.platform(),
        "model_files": {str(p.relative_to(args.model)): file_hash(p) for p in sorted(args.model.rglob("*")) if p.is_file()},
        "worker_sources": {p.name: file_hash(p) for p in sorted(worker_path.parent.glob("*.py"))},
        "turn_model_sha256": file_hash(args.turn_model) if args.turns else None,
        "scope": "Sequential worker latency including IPC; excludes audio decode, WASAPI, C# segmentation, translation and display.",
        "reference_sent_to_worker": False, "started_utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    metadata["dependencies"] = {}
    for package in ("faster-whisper", "ctranslate2", "av", "numpy", "onnxruntime"):
        try:
            metadata["dependencies"][package] = importlib.metadata.version(package)
        except importlib.metadata.PackageNotFoundError:
            metadata["dependencies"][package] = None
    write_json(args.out / "config.json", metadata)
    results, fatal, worker = [], None, None
    started = time.perf_counter()
    try:
        worker = Worker(command, args.out / "worker.stderr.log", args.timeout)
        metadata["startup_with_worker_warmup_s"] = time.perf_counter() - started
        with (args.out / "results.jsonl").open("x", encoding="utf-8") as stream:
            for index, row in enumerate(rows):
                samples = checked_audio(row)
                # Gold text, filename, speaker and scene never cross this boundary.
                request = {"type": "transcribe", "sample_rate": RATE, "audio_base64": base64.b64encode(samples.tobytes()).decode("ascii"),
                           "language": row["language"], "turn_enhancement": args.turns}
                start = time.perf_counter()
                response = worker.exchange(request)
                elapsed = time.perf_counter() - start
                # Production displays split segments when present, not the original unsplit result.
                separator = " " if row["language"] == "en" else ""
                hypothesis = separator.join(s["text"] for s in response["segments"]) if response.get("segments") else response.get("text", "")
                item = {"id": row["id"], "language": row["language"], "kind": row.get("kind", "clean"),
                        "group": row["group"], "reference": row["text"], "hypothesis": hypothesis,
                        "pcm_sha256": row["pcm_sha256"], "duration_s": len(samples) / RATE,
                        "elapsed_s": elapsed, "response": response, **score(row["text"], hypothesis, row["language"])}
                for key in ("error", "warning", "turn_status"):
                    if key in response:
                        item[key] = response[key]
                if "expected_speaker_change" in row:
                    item["expected_speaker_change"] = row["expected_speaker_change"]
                results.append(item)
                stream.write(json.dumps(item, ensure_ascii=False) + "\n")
                stream.flush()
                print(f"{index + 1}/{len(rows)} {elapsed:.3f}s", flush=True)
    except Exception as exc:
        fatal = f"{type(exc).__name__}: {exc}"
    finally:
        if worker is not None:
            worker.close()
    report = {"complete": fatal is None and len(results) == len(rows), "fatal_error": fatal,
              "unprocessed": len(rows) - len(results), "overall": aggregate(results),
              "by_kind": {kind: aggregate([r for r in results if r["kind"] == kind]) for kind in sorted({r["kind"] for r in results})}}
    write_json(args.out / "config.json", metadata)
    write_json(args.out / "summary.json", report)
    print(json.dumps(report, ensure_ascii=True))
    return int(not report["complete"] or report["overall"]["errors"] > 0)


def compare(args):
    configs = [json.loads((p / "config.json").read_text(encoding="utf-8")) for p in (args.baseline, args.candidate)]
    summaries = [json.loads((p / "summary.json").read_text(encoding="utf-8")) for p in (args.baseline, args.candidate)]
    if any(not s["complete"] or s["overall"]["errors"] for s in summaries):
        raise ValueError("Cannot compare incomplete or failed runs")
    if any(configs[0][key] != configs[1][key] for key in ("manifest_sha256", "benchmark_sha256", "split", "selected")):
        raise ValueError("Runs must use the same frozen manifest, benchmark version, split and selection")
    left, right = [read_rows(p / "results.jsonl") for p in (args.baseline, args.candidate)]
    signature = lambda rows: [(r["id"], r["pcm_sha256"], r["reference"]) for r in rows]
    if signature(left) != signature(right):
        raise ValueError("Sample identities/order/reference differ")
    report = {"baseline": aggregate(left), "candidate": aggregate(right),
        "mean_paired_latency_delta_s": statistics.mean(b["elapsed_s"] - a["elapsed_s"] for a, b in zip(left, right)),
        "char_edit_delta": sum(r["char_edits"] for r in right) - sum(r["char_edits"] for r in left),
        "note": "Negative deltas improve. Same-host repeated runs and scene-level uncertainty analysis required before release; no automatic accuracy claim."}
    if args.out.exists():
        raise ValueError("Output already exists")
    args.out.parent.mkdir(parents=True, exist_ok=True)
    write_json(args.out, report)
    print(json.dumps(report, ensure_ascii=True))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("prepare", help="Validate original-text CSV/JSONL and freeze grouped splits")
    p.add_argument("--input", type=Path, required=True)
    p.add_argument("--out", type=Path, required=True)
    p.add_argument("--seed", type=int, default=20261014)
    p.add_argument("--smoke-only", action="store_true")
    p.set_defaults(func=prepare)
    p = sub.add_parser("cases", help="Make adjacent-turn, silence and optional instrumental-BGM cases")
    p.add_argument("--manifest", type=Path, required=True)
    p.add_argument("--out", type=Path, required=True)
    p.add_argument("--max-pairs", type=int, default=100)
    p.add_argument("--bgm", type=Path)
    p.add_argument("--snr", type=float, default=10.)
    p.set_defaults(func=cases)
    p = sub.add_parser("run", help="Evaluate the unchanged production worker, completely offline")
    p.add_argument("--manifest", type=Path, required=True)
    p.add_argument("--out", type=Path, required=True)
    p.add_argument("--model", type=Path, required=True)
    p.add_argument("--split", choices=["test", "dev", "train", "smoke"], default="test")
    p.add_argument("--device", choices=["cpu", "cuda"], default="cpu")
    p.add_argument("--beam", type=int, default=2)
    p.add_argument("--turns", action="store_true")
    p.add_argument("--turn-model", type=Path)
    p.add_argument("--dll-dir", type=Path, action="append", default=[])
    p.add_argument("--timeout", type=float, default=120.)
    p.add_argument("--limit", type=int, default=0)
    p.set_defaults(func=run)
    p = sub.add_parser("compare")
    p.add_argument("--baseline", type=Path, required=True)
    p.add_argument("--candidate", type=Path, required=True)
    p.add_argument("--out", type=Path, required=True)
    p.set_defaults(func=compare)
    args = parser.parse_args()
    if any(getattr(args, k, 0) < 0 for k in ("limit", "max_pairs")):
        parser.error("Limits cannot be negative")
    if getattr(args, "timeout", 1) <= 0 or getattr(args, "beam", 1) <= 0:
        parser.error("Timeout and beam must be positive")
    try:
        return args.func(args) or 0
    except (ValueError, OSError) as exc:
        parser.exit(1, f"Error: {exc}\n")


if __name__ == "__main__":
    raise SystemExit(main())
