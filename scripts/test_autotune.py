import argparse
import contextlib
import io
import json
from pathlib import Path
import tempfile
import time
import unittest
from unittest.mock import patch
import numpy as np

import autotune as tune
import galgame_bench as bench


def metrics(cer=.1, p95=1., rtf=.5):
    return dict(cer=cer, p95_s=p95, rtf=rtf, languages={"en": {"cer": cer, "wer": cer * 2}})


def rows():
    return [dict(id=str(i), text=f"This is the original sentence number {i}", language="en",
                 game="fixture", scene=str(i // 4), pcm_sha256=f"hash-{i}") for i in range(12)]


class CalibrationTests(unittest.TestCase):
    def test_srt_parses_bom_crlf_and_rejects_overlap(self):
        with tempfile.TemporaryDirectory() as directory:
            file = Path(directory) / "original.srt"
            file.write_text("\ufeff1\n00:00:01,250 --> 00:00:02,500\nHello world.\n\n2\n00:00:03,000 --> 00:00:04,000\nGoodbye.\n", encoding="utf-8")
            result = tune.read_srt(file)
            self.assertEqual(result[0]["start"], 1.25)
            self.assertEqual(result[1]["text"], "Goodbye.")
            file.write_text("00:00:00,000 --> 00:00:02,000\na\n\n00:00:01,000 --> 00:00:03,000\nb", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "non-overlapping"):
                tune.read_srt(file)

    def test_streaming_cut_supports_recordings_longer_than_clip_limit(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "long.wav"
            bench.save_wave(source, np.zeros(bench.RATE * 121, dtype=np.float32))
            files = tune.cut_recording(source, [dict(start=119.5, end=120.5, text="test")], root)
            self.assertEqual(len(bench.audio(files[0])), bench.RATE)
            with self.assertRaisesRegex(ValueError, "beyond"):
                tune.cut_recording(source, [dict(start=121, end=122, text="test")], root)

    def test_partition_keeps_scenes_separate(self):
        data = tune.partition(rows())
        scenes = {}
        for row in data:
            scenes.setdefault(row["scene"], set()).add(row["split"])
        self.assertTrue(all(len(v) == 1 for v in scenes.values()))
        self.assertEqual({r["split"] for r in data}, {"holdout", "tune"})

    def test_duplicate_text_groups_cannot_leak(self):
        data = rows()
        data[4]["text"] = data[0]["text"]
        with self.assertRaisesRegex(ValueError, "independent"):
            tune.partition(data)

    def test_conflicting_audio_labels_are_rejected(self):
        data = rows(); data[4]["pcm_sha256"] = data[0]["pcm_sha256"]
        with self.assertRaisesRegex(ValueError, "conflicting"):
            tune.partition(data)

    def test_silent_or_unverified_reference_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); audio = root / "silence.wav"; bench.save_wave(audio, np.zeros(16000))
            row = rows()[0] | {"audio": str(audio), "verified_original": False}
            manifest = root / "input.jsonl"; bench.write_manifest(manifest, [row])
            with self.assertRaisesRegex(ValueError, "Unverified"):
                tune.load_dataset({"inputs": [str(manifest)]}, root)
            manifest.unlink(); row["verified_original"] = True; bench.write_manifest(manifest, [row])
            with self.assertRaisesRegex(ValueError, "Silent"):
                tune.load_dataset({"inputs": [str(manifest)]}, root)

    def test_quality_and_speed_gates_do_not_hide_regressions(self):
        self.assertTrue(tune.improved(metrics(.08, 1), metrics(), "balanced", 1))
        self.assertFalse(tune.improved(metrics(.12, .5), metrics(), "balanced", 1))
        self.assertFalse(tune.improved(metrics(.1, .95), metrics(), "speed", 1))
        self.assertFalse(tune.improved(metrics(.01, .2, 1.5), metrics(), "accuracy", 1))
        other = metrics(.05, .5); other["languages"]["en"]["wer"] = .3
        self.assertFalse(tune.improved(other, metrics(), "balanced", 1))

    def test_only_winner_uses_holdout_and_failure_does_not_select_another(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); model = root / "model"; model.mkdir(); (model / "model.bin").write_bytes(b"fake")
            candidates = [dict(name=name, model=str(model), beam=beam, device="cpu", language="en") for name, beam in [("baseline", 2), ("winner", 1), ("other", 5)]]
            calls = []

            def evaluate(config, candidate, subset, folder, deadline, repeats=1):
                phase = subset[0]["split"]
                calls.append((candidate["name"], phase, repeats))
                if phase == "holdout":
                    return metrics(.1 if candidate["name"] == "baseline" else .2)
                return metrics({"baseline": .1, "winner": .05, "other": .07}[candidate["name"]])

            with patch.object(tune, "load_dataset", return_value=rows()), patch.object(tune, "evaluate", side_effect=evaluate):
                report = tune.calibrate(dict(candidates=candidates), root)
            self.assertFalse(report["eligible"])
            self.assertEqual([c[0] for c in calls if c[1] == "holdout"], ["baseline", "winner"])
            self.assertEqual(calls[-1][2], 2)

    def test_gold_text_and_file_names_never_reach_worker(self):
        class FakeWorker:
            requests = []
            def __init__(self, *args): pass
            def exchange(self, request):
                self.requests.append(request)
                return {"text": "A test sentence."}
            def close(self): pass
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            row = rows()[0] | dict(text="A test sentence.", audio="secret-name.wav", duration_s=1., split="tune", group="scene")
            with patch.object(bench, "Worker", FakeWorker), patch.object(bench, "checked_audio", return_value=np.ones(16000, dtype="<f4")), contextlib.redirect_stdout(io.StringIO()):
                result = tune.evaluate({}, dict(name="test", model="unused", device="cpu", beam=1, language="auto"), [row], root / "run", time.monotonic() + 60)
            self.assertEqual(result["cer"], 0)
            self.assertEqual(set(FakeWorker.requests[0]), {"type", "sample_rate", "audio_base64", "language", "turn_enhancement"})
            self.assertEqual(FakeWorker.requests[0]["language"], "auto")


if __name__ == "__main__":
    unittest.main()
