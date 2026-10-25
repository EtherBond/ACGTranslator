import argparse
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import wave

import finetune_asr as ft
from optimization_bench import queue_simulation

class TrainingDataTests(unittest.TestCase):
    def fixture(self,root):
        rows=[]
        for i in range(60):
            path=root/f'{i}.wav';raw=(i+1).to_bytes(2,'little')*16000
            with wave.open(str(path),'wb') as f:
                f.setparams((1,2,16000,0,'NONE','not compressed'));f.writeframes(raw)
            rows.append(dict(id=str(i),audio=path.name,text=f'文章番号{i}',language='ja',verified_original=True,
                pcm_sha256=hashlib.sha256(raw).hexdigest(),recording_group='one',source_start_s=i*10,source_end_s=i*10+1))
        path=root/'input.jsonl';ft.bench.write_manifest(path,rows)
        return path,rows
    def test_one_recording_cannot_claim_independent_evaluation(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);path,_=self.fixture(root)
            with self.assertRaisesRegex(ValueError,'three recording'):
                ft.prepare(argparse.Namespace(dataset=path,out=root/'out',exploratory=False))
            self.assertFalse((root/'out').exists())
    def test_exploratory_split_has_time_gap_and_blocks_promotion(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);path,_=self.fixture(root)
            ft.prepare(argparse.Namespace(dataset=path,out=root/'out',exploratory=True))
            rows=ft.bench.read_rows(root/'out/manifest.jsonl')
            a=[r for r in rows if r['split']=='train'];b=[r for r in rows if r['split']=='dev']
            self.assertGreater(min(r['source_start_s'] for r in b)-max(r['source_end_s'] for r in a),30)
            self.assertFalse(json.loads((root/'out/split.json').read_text())['deployment_allowed'])
    def test_modified_audio_is_rejected(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);path,_=self.fixture(root)
            with wave.open(str(root/'0.wav'),'wb') as f:
                f.setparams((1,2,16000,0,'NONE','not compressed'));f.writeframes(b'\0\0'*16000)
            with self.assertRaisesRegex(ValueError,'hash mismatch'):
                ft.prepare(argparse.Namespace(dataset=path,out=root/'out',exploratory=True))
    def test_duplicate_text_cannot_cross_splits(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);path,rows=self.fixture(root)
            rows[-1]['text']=rows[0]['text']
            path.write_text('\n'.join(json.dumps(r) for r in rows),encoding='utf-8')
            ft.prepare(argparse.Namespace(dataset=path,out=root/'out',exploratory=True))
            actual=ft.bench.read_rows(root/'out/manifest.jsonl')
            self.assertEqual(actual[0]['split'],'duplicate_excluded')
            self.assertEqual(actual[-1]['split'],'duplicate_excluded')
    def test_queue_drops_oldest_waiting_and_keeps_running(self):
        rows=[dict(id=str(i),emitted_at_s=float(i),end_s=float(i),elapsed_s=10.) for i in range(5)]
        r=queue_simulation(rows,backlog=2)
        self.assertEqual(r['dropped_ids'],['1','2'])
        self.assertEqual(r['completed'],3)

if __name__=='__main__': unittest.main()
