"""Lossless turn partitioning and invalid-alignment fallback regression tests."""
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace as NS
import numpy as np
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'src/ACGTranslator.App/Resources'))
from asr_worker import aligned_turn_segments, transcribe


class AlignmentTests(unittest.TestCase):
    def split(self, words, text='Hello there', boundary=1.):
        return aligned_turn_segments(dict(text=text,language='en'), words, boundary, 2.)

    def words(self):
        return [dict(start=0.,end=1.,text='Hello'),dict(start=1.,end=2.,text=' there')]

    def test_english_gapless_turn(self):
        parts=self.split(self.words())
        self.assertEqual([p['text'] for p in parts],['Hello','there'])
        self.assertEqual(parts[1]['start'],1.)

    def test_japanese_punctuation_preserved(self):
        words=self.words(); words[0]['text']='待って！'; words[1]['text']='何？'
        parts=self.split(words,text='待って！何？')
        self.assertEqual(''.join(p['text'] for p in parts),'待って！何？')

    def test_incomplete_text_rejected(self):
        self.assertIsNone(self.split(self.words(),text='Hello over there'))

    def test_crossing_word_rejected(self):
        self.assertIsNone(self.split(self.words(),boundary=.9))

    def test_invalid_times_rejected(self):
        for start,end in [(-.1,.8),(.5,2.1),(float('nan'),1.),(.5,.4)]:
            words=self.words(); words[0].update(start=start,end=end)
            self.assertIsNone(self.split(words))

    def test_overlap_rejected(self):
        words=self.words(); words[1]['start']=.9
        self.assertIsNone(self.split(words))

    def test_invalid_boundary_rejected(self):
        for boundary in [float('nan'),float('inf'),-1.,0.,2.,3.]:
            self.assertIsNone(self.split(self.words(),boundary=boundary))

    def test_empty_side_rejected(self):
        words=self.words(); words[1]['text']=' '
        self.assertIsNone(self.split(words,text='Hello'))

    def test_real_worker_reuses_one_decode(self):
        class Model:
            calls=0
            def transcribe(self,audio,**kwargs):
                self.calls+=1
                segment=NS(text='Hello there',start=0.,end=2.,words=[
                    NS(start=0.,end=1.,word='Hello'),NS(start=1.,end=2.,word=' there')])
                return iter([segment]),NS(language='en',all_language_probs=[])
        class Detector:
            def detect(self,*args): return 1.
        model=Model()
        result=transcribe(model,np.zeros(32000),'en',{},Detector())
        self.assertEqual(model.calls,1)
        self.assertEqual(result['turn']['strategy'],'aligned')
        self.assertEqual(result['turn']['extra_decodes'],0)
        self.assertEqual([s['text'] for s in result['segments']],['Hello','there'])


if __name__=='__main__': unittest.main()
