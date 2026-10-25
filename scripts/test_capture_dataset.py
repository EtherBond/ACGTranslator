import json
from pathlib import Path
import tempfile
import unittest
import wave

import capture_dataset as data


class DatasetTests(unittest.TestCase):
    def fixture(self, root):
        bundle = root / 'bundle'; bundle.mkdir()
        with wave.open(str(bundle / 'capture.wav'), 'wb') as audio:
            audio.setparams((1, 2, 16000, 0, 'NONE', 'not compressed')); audio.writeframes(b'\x10\x00' * 32000)
        (bundle / 'frame.png').write_bytes(b'fixture-image')
        (bundle / 'session.json').write_text('{}', encoding='utf-8')
        data.write_rows(bundle / 'candidates.jsonl', [dict(id=str(i), game='test', original_ocr='誤字', previous_review='已排除') for i in range(3)])
        metadata = dict(schema='acg.capture-handoff.v1', session_id='session-one', candidate_count=3,
            files={p.name: data.digest(p) for p in bundle.iterdir()}, missing_files=[])
        (bundle / 'bundle.json').write_text(json.dumps(metadata), encoding='utf-8')
        decision = dict(id='0', bundle_sha256=data.digest(bundle / 'bundle.json'), status='accept', reason='checked fixture',
            text_verified=True, audio_verified=True, reviewer='test', verification_method='fixture', text='原文',
            start_s=.25, end_s=1.25, text_evidence=['frame.png'], audio_evidence='synthetic test only')
        return bundle, decision

    def test_reviewed_cut_and_unreviewed_partition_without_three_scene_requirement(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); bundle, decision = self.fixture(root)
            patches = root / 'decisions.jsonl'
            data.write_rows(patches, [decision, dict(id='1', bundle_sha256=decision['bundle_sha256'], status='exclude', reason='unvoiced')])
            result = data.export(bundle, patches, root / 'export')
            self.assertEqual((result['accepted'], result['pending'], result['excluded']), (1, 1, 1))
            row = data.read_rows(root / 'export/train.jsonl')[0]
            self.assertEqual(row['text'], '原文'); self.assertEqual(row['original_ocr'], '誤字')
            self.assertEqual(row['recording_group'], 'session-one')
            with wave.open(str(root / 'export' / row['audio']), 'rb') as audio:
                self.assertEqual(audio.getnframes(), 16000)
            self.assertEqual(data.read_rows(bundle / 'candidates.jsonl')[0]['original_ocr'], '誤字')
            with self.assertRaises(FileExistsError): data.export(bundle, patches, root / 'export')

    def test_changed_evidence_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            bundle, _ = self.fixture(Path(temp)); (bundle / 'frame.png').write_bytes(b'changed')
            with self.assertRaisesRegex(ValueError, 'changed'): data.load_bundle(bundle)

    def test_unverified_or_stale_decisions_do_not_create_training_output(self):
        for change in [dict(audio_verified=False), dict(text_verified=False), dict(bundle_sha256='wrong'), dict(start_s=-1), dict(text_evidence=['../outside.png'])]:
            with self.subTest(change=change), tempfile.TemporaryDirectory() as temp:
                root = Path(temp); bundle, decision = self.fixture(root); decision.update(change)
                data.write_rows(root / 'patches.jsonl', [decision])
                with self.assertRaises(ValueError): data.export(bundle, root / 'patches.jsonl', root / 'export')
                self.assertFalse((root / 'export').exists())

    def test_evidence_path_escape_is_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            with self.assertRaises(ValueError): data.safe(Path(temp), '../escape.txt')


if __name__ == '__main__': unittest.main()
