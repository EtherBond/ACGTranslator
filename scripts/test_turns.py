"""Deterministic regression tests; no downloads or microphone access."""
import sys, unittest
from pathlib import Path
from types import SimpleNamespace as NS
import numpy as np
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src/ACGTranslator.App/Resources"))
from turn_detector import TurnDetector, candidates
from asr_worker import transcribe

WORDS = [{"start": 0., "end": 1., "text": "Hello"},
         {"start": 1., "end": 2., "text": "there"}]


class DetectionTests(unittest.TestCase):
    def test_no_gap_is_a_candidate(self):
        self.assertEqual(candidates(WORDS), [1.])

    def test_continuous_long_segment_candidates_cover_late_turns(self):
        words=[dict(start=i*.2,end=(i+1)*.2) for i in range(60)]
        points=candidates(words)
        self.assertLessEqual(len(points),6)
        self.assertLess(min(points),2.)
        self.assertGreater(max(points),10.)
        self.assertTrue(any(5. <= point <= 7. for point in points))

    def test_short_response_is_not_forced(self):
        self.assertEqual(candidates([dict(start=0,end=.2),dict(start=.2,end=.6)]), [])

    def test_same_speaker_is_unchanged(self):
        d = TurnDetector(lambda _: np.array([1., 0.]))
        self.assertIsNone(d.detect(np.zeros(32000), WORDS))

    def test_confirmed_change_without_pause(self):
        audio = np.concatenate([np.ones(16000), -np.ones(16000)])
        d = TurnDetector(lambda a: np.array([1.,0.]) if a.mean()>0 else np.array([0.,1.]))
        self.assertEqual(d.detect(audio, WORDS), 1.)

    def test_unstable_boundary_is_not_change(self):
        vectors = iter([np.array([1.,0.]),np.array([0.,1.]),
                        np.array([0.,1.]),np.array([1.,0.])])
        self.assertIsNone(TurnDetector(lambda _: next(vectors)).detect(np.zeros(32000), WORDS))

    def test_silence_does_not_trigger_embeddings(self):
        def fail(_): raise AssertionError("should skip sparse speech")
        sparse=[dict(start=0.,end=.1),dict(start=1.9,end=2.)]
        self.assertIsNone(TurnDetector(fail).detect(np.zeros(32000), sparse))

    def test_budget_is_checked_after_inference(self):
        ticks=iter([0.,0.,0.,1.])
        d=TurnDetector(lambda _: np.array([1.,0.]), clock=lambda: next(ticks))
        with self.assertRaises(TimeoutError): d.detect(np.zeros(32000),WORDS)

    def test_candidate_count_is_bounded(self):
        words=[dict(start=i*.15,end=(i+1)*.15) for i in range(80)]
        self.assertLessEqual(len(candidates(words)),6)


class FakeModel:
    def __init__(self, texts):
        self.texts=iter(texts); self.calls=[]
    def transcribe(self,audio,**kwargs):
        self.calls.append(kwargs)
        text=next(self.texts)
        segment=NS(text=text,start=0.,end=len(audio)/16000,
                   words=[NS(start=w["start"],end=w["end"],word=w["text"]) for w in WORDS])
        return iter([segment]),NS(language="en",all_language_probs=[])


class FixedDetector:
    def __init__(self, result=1., error=None): self.result=result; self.error=error
    def detect(self,*_):
        if self.error: raise self.error
        return self.result


class WorkerTests(unittest.TestCase):
    def test_disabled_has_one_decode_no_word_timestamps(self):
        m=FakeModel(["ordinary"])
        result=transcribe(m,np.zeros(32000),"en",{})
        self.assertEqual(result["text"],"ordinary")
        self.assertEqual(len(m.calls),1)
        self.assertNotIn("word_timestamps",m.calls[0])

    def test_change_redecodes_both_sides_and_offsets_time(self):
        m=FakeModel(["merged","first","second"])
        r=transcribe(m,np.zeros(32000),"en",{},FixedDetector())
        self.assertEqual([s["text"] for s in r["segments"]],["first","second"])
        self.assertEqual(r["segments"][0]["end"],1.)
        self.assertEqual(r["segments"][1]["start"],1.)
        self.assertEqual(r["segments"][1]["end"],2.)
        self.assertEqual(len(m.calls),3)
        self.assertNotIn("word_timestamps",m.calls[1])

    def test_empty_retry_keeps_original(self):
        r=transcribe(FakeModel(["keep","one",""]),np.zeros(32000),"en",{},FixedDetector())
        self.assertEqual(r["text"],"keep")
        self.assertNotIn("segments",r)
        self.assertIn("warning",r)

    def test_detection_error_keeps_original(self):
        r=transcribe(FakeModel(["keep"]),np.zeros(32000),"en",{},FixedDetector(error=ValueError("test")))
        self.assertEqual(r["text"],"keep")
        self.assertEqual(r["turn_status"],"fallback")

    def test_budget_keeps_original(self):
        r=transcribe(FakeModel(["keep"]),np.zeros(32000),"en",{},FixedDetector(error=TimeoutError()))
        self.assertEqual(r["text"],"keep")
        self.assertEqual(r["turn_status"],"budget")

    def test_single_speaker_never_redecodes(self):
        m=FakeModel(["keep"])
        r=transcribe(m,np.zeros(32000),"en",{},FixedDetector(None))
        self.assertEqual(r["text"],"keep")
        self.assertEqual(len(m.calls),1)


if __name__ == "__main__": unittest.main()
