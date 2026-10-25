import importlib.util
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch

ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('asr_under_test',ROOT/'src/ACGTranslator.App/Resources/asr_worker.py')
worker=importlib.util.module_from_spec(spec)
with patch.dict(sys.modules, {'worker_protocol': types.SimpleNamespace(add_dll_directories=lambda _:None,emit=lambda _:None,requests=lambda:[])}):
    spec.loader.exec_module(worker)

def segment(text='こんにちは', logprob=-.2):
    return types.SimpleNamespace(text=text,start=0,end=2,words=[],avg_logprob=logprob,compression_ratio=1,no_speech_prob=.1)

class Model:
    def __init__(self, replies): self.replies=list(replies); self.calls=[]
    def transcribe(self,audio,**options):
        self.calls.append(options)
        return iter(self.replies.pop(0)),types.SimpleNamespace(language='ja',duration_after_vad=2,all_language_probs=[('ja',.99)])

class OptimizationTests(unittest.TestCase):
    def test_normal_decode_stays_single_and_fast(self):
        m=Model([[segment()]])
        result=worker.transcribe(m,[0]*32000,'ja',{'beam_size':5},adaptive=True)
        self.assertEqual(len(m.calls),1); self.assertEqual(m.calls[0]['beam_size'],2)
        self.assertFalse(result['decode']['retried'])
    def test_uncertain_clip_gets_exactly_one_retry_without_prompt(self):
        m=Model([[segment(logprob=-1)], [segment('修正結果')]])
        r=worker.transcribe(m,[0]*32000,'ja',{'beam_size':5},adaptive=True)
        self.assertEqual([c['beam_size'] for c in m.calls],[2,5])
        self.assertEqual(r['text'],'修正結果')
        self.assertTrue(all('initial_prompt' not in c for c in m.calls))
    def test_default_keeps_original_beam(self):
        m=Model([[segment(logprob=-1)]])
        worker.transcribe(m,[0]*32000,'ja',{'beam_size':5})
        self.assertEqual(len(m.calls),1); self.assertEqual(m.calls[0]['beam_size'],5)
    def test_impossible_short_turn_skips_alignment(self):
        m=Model([[segment()]])
        detector=types.SimpleNamespace(detect=lambda *args: self.fail('Short clip cannot split'))
        worker.transcribe(m,[0]*16000,'ja',{'beam_size':5},detector)
        self.assertNotIn('word_timestamps',m.calls[0])
    def test_japanese_vocabulary_does_not_leak_into_english_or_auto(self):
        for language in ('en','auto'):
            m=Model([[segment()]])
            worker.transcribe(m,[0]*32000,language,{'beam_size':5,'hotwords':'来海 天音'})
            self.assertNotIn('hotwords',m.calls[0])
    def test_fixed_japanese_preserves_short_vocabulary(self):
        m=Model([[segment()]])
        worker.transcribe(m,[0]*32000,'ja',{'beam_size':5,'hotwords':'来海 天音'})
        self.assertEqual(m.calls[0]['hotwords'],'来海 天音')

if __name__=='__main__': unittest.main()
