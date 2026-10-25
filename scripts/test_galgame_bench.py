"""Synthetic fixtures only: no game assets, no network, no model required."""
import argparse
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch
import zlib

import numpy as np

import galgame_bench as bench
import import_galgame as importer
import probe_galgame as probe


class BenchmarkTests(unittest.TestCase):
    def test_metrics_are_real_edit_distances(self):
        self.assertEqual(bench.score("猫がいる", "犬がいる", "ja")["char_edits"], 1)
        self.assertEqual(bench.score("ＡＢＣ！", "ABC", "en")["char_edits"], 0)
        self.assertEqual(bench.score("cat is here", "dog here", "en")["word_edits"], 2)
        self.assertEqual(bench.score("橋", "はし", "ja")["char_edits"], 2)
        self.assertTrue(bench.score("", "こんにちは", "ja")["false_text"])

    def test_zero_reference_metrics_are_not_false_perfect_score(self):
        row = dict(id="silence", language="ja", elapsed_s=.1, duration_s=1,
                   **bench.score("", "", "ja"))
        self.assertIsNone(bench.aggregate([row])["languages"]["ja"]["cer"])

    def test_synthetic_turn_metrics_exclude_unknown_speakers(self):
        rows = [dict(language="ja", elapsed_s=.1, duration_s=1, expected_speaker_change=expected,
                     turn_status=status, **bench.score("テスト", "テスト", "ja"))
                for expected, status in [(True, "split"), (False, "split"), (True, "cooldown"), (None, "split")]]
        metrics = bench.aggregate(rows)["synthetic_turn_detection"]
        self.assertEqual(metrics["labelled_samples"], 3)
        self.assertEqual(metrics["precision"], .5)
        self.assertEqual(metrics["recall"], .5)

    def make_rows(self, root):
        rows = []
        for i in range(6):
            signal = np.sin(np.arange(8000, dtype=np.float32) * (.04 + .01 * i)) * .1
            path = root / f"{i}.wav"
            bench.save_wave(path, signal)
            rows.append(dict(id=str(i), audio=path.name, text=f"word number {i}", language="en",
                             game="fixture", scene=f"scene-{i // 2}", source=f"fixture:{i}",
                             order=i % 2, speaker=str(i % 2), verified_original=True))
        return rows

    def test_prepare_hash_split_and_cases(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            rows = self.make_rows(root)
            bench.write_manifest(root / "input.jsonl", rows)
            args = argparse.Namespace(input=root / "input.jsonl", out=root / "frozen.jsonl", seed=17, smoke_only=False)
            bench.prepare(args)
            frozen = bench.read_rows(args.out)
            self.assertEqual({r["split"] for r in frozen}, {"train", "dev", "test"})
            self.assertEqual(frozen[0]["split"], frozen[1]["split"])
            bench.cases(argparse.Namespace(manifest=args.out, out=root / "cases", max_pairs=3, bgm=None, snr=10))
            cases = bench.read_rows(root / "cases/manifest.jsonl")
            self.assertEqual(len(cases), 15)
            self.assertEqual(sum(r.get("expected_speaker_change") is True for r in cases), 12)
            for case in cases:
                bench.checked_audio(case)
            bench.save_wave(Path(frozen[0]["audio"]), np.zeros(8000))
            with self.assertRaisesRegex(ValueError, "changed"):
                bench.checked_audio(frozen[0])

    def test_duplicate_audio_cannot_leak_across_scenes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            rows = self.make_rows(root)
            rows[2].update(audio=rows[0]["audio"], text=rows[0]["text"])
            bench.write_manifest(root / "input.jsonl", rows)
            args = argparse.Namespace(input=root / "input.jsonl", out=root / "out.jsonl", seed=17, smoke_only=False)
            with self.assertRaisesRegex(ValueError, "independent"):
                bench.prepare(args)
            self.assertFalse(args.out.exists())

    def test_unreviewed_reference_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            rows = self.make_rows(root)
            rows[0]["verified_original"] = False
            bench.write_manifest(root / "input.jsonl", rows)
            with self.assertRaisesRegex(ValueError, "not verified"):
                bench.prepare(argparse.Namespace(input=root / "input.jsonl", out=root / "out.jsonl", seed=0, smoke_only=True))

    def test_importer_rejects_ambiguous_and_dynamic_pairs(self):
        text = '[name name="A"]\n[voice storage="a"]\nHello.[p]\n[voice storage="b"]\nWorld.[p]\n[voice storage="%dynamic"]\nWrong.[p]'
        rows, issues = importer.candidates(Path("scene.ks"), text,
            {"a": [Path("/fake/a.ogg")], "b": [Path("/fake/b.ogg"), Path("/other/b.ogg")]}, "fixture", "en", "voice")
        self.assertEqual(len(rows), 1)
        self.assertFalse(rows[0]["verified_original"])
        self.assertEqual(rows[0]["text"], "Hello.")
        self.assertEqual(len(issues), 2)

    def test_importer_does_not_cross_control_or_partial_lines(self):
        text = '[voice storage="a"]\nHello[r]\nworld[p]\n[voice storage="a"]\n[jump target="x"]\nWrong[p]'
        rows, issues = importer.candidates(Path("scene.ks"), text, {"a": [Path("/fake/a.ogg")]}, "fixture", "en", "voice")
        self.assertFalse(rows)
        self.assertEqual(len(issues), 2)

    def test_worker_protocol_and_timeout(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fake = root / "fake.py"
            fake.write_text('import json,sys\nprint(json.dumps({"ready":True}),flush=True)\nfor line in sys.stdin:\n r=json.loads(line)\n print(json.dumps({"keys":sorted(r)}),flush=True)\n', encoding="utf-8")
            worker = bench.Worker([sys.executable, str(fake)], root / "normal.log", 10)
            try:
                self.assertEqual(worker.exchange({"audio_base64": "AA=="})["keys"], ["audio_base64"])
            finally:
                worker.close()
            fake.write_text('import time\ntime.sleep(20)\n', encoding="utf-8")
            with self.assertRaises(TimeoutError):
                bench.Worker([sys.executable, str(fake)], root / "timeout.log", .2)

    def test_compare_rejects_changed_manifest(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in ("a", "b"):
                folder = root / name
                folder.mkdir()
                bench.write_json(folder / "config.json", {"manifest_sha256": name, "split": "test", "selected": 1})
                bench.write_json(folder / "summary.json", {"complete": True, "overall": {"errors": 0}})
            with self.assertRaisesRegex(ValueError, "same frozen"):
                bench.compare(argparse.Namespace(baseline=root / "a", candidate=root / "b", out=root / "diff.json"))

    def test_runner_scores_displayed_segments_without_leaking_labels(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            rows = self.make_rows(root)
            bench.write_manifest(root / "input.jsonl", rows[:1])
            bench.prepare(argparse.Namespace(input=root / "input.jsonl", out=root / "manifest.jsonl", seed=0, smoke_only=True))
            model = root / "model"
            model.mkdir()
            (model / "model.bin").write_bytes(b"test-only model placeholder")
            requests = []

            class FakeWorker:
                def __init__(self, *args):
                    pass

                def exchange(self, request):
                    requests.append(request)
                    return {"text": "wrong unsplit text", "segments": [{"text": "word number 0"}], "turn_status": "split"}

                def close(self):
                    pass

            args = argparse.Namespace(manifest=root / "manifest.jsonl", split="smoke", limit=0,
                model=model, turns=False, turn_model=None, out=root / "run", device="cpu", beam=2, dll_dir=[], timeout=10)
            with patch.object(bench, "Worker", FakeWorker):
                self.assertEqual(bench.run(args), 0)
            self.assertEqual(set(requests[0]), {"type", "sample_rate", "audio_base64", "language", "turn_enhancement"})
            result = bench.read_rows(root / "run/results.jsonl")[0]
            self.assertEqual(result["char_edits"], 0)
            bench.compare(argparse.Namespace(baseline=args.out, candidate=args.out, out=root / "comparison.json"))

    def test_probe_continued_compressed_index_and_read_only(self):
        def chunk(name, body):
            return name + struct.pack("<Q", len(body)) + body
        name = "voice.ogg".encode("utf-16le")
        info = struct.pack("<IQQH", 0x80000000, 100, 100, len(name) // 2) + name
        index = chunk(b"File", chunk(b"info", info))
        packed = zlib.compress(index)
        header = probe.MAGIC + struct.pack("<Q", 19)
        link = b"\x80" + struct.pack("<QQ", 0, 36)
        data = header + link + b"\x01" + struct.pack("<QQ", len(packed), len(index)) + packed
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "test.xp3"
            path.write_bytes(data)
            result = probe.inventory(path)
            self.assertEqual(result["entries"], 1)
            self.assertEqual(result["flags"]["protected"], 1)
            self.assertEqual(result["index_blocks"], 2)
            self.assertEqual(path.read_bytes(), data)

    def test_probe_rejects_cyclic_index(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "bad.xp3"
            path.write_bytes(probe.MAGIC + struct.pack("<Q", 19) + b"\x80" + struct.pack("<QQ", 0, 19))
            with self.assertRaisesRegex(ValueError, "cyclic"):
                probe.inventory(path)


if __name__ == "__main__":
    unittest.main()
