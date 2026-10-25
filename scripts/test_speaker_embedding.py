"""Constructor policy regression tests without loading speaker weights."""
import importlib.util,sys,unittest
from pathlib import Path
from types import SimpleNamespace as NS
from unittest.mock import patch
target=Path(__file__).resolve().parents[1]/'src/ACGTranslator.App/Resources/turn_detector.py'
if not target.exists():target=Path(__file__).parent/'turn_detector.py'
spec=importlib.util.spec_from_file_location('turn_detector',target)
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
class Options:
 def __init__(self):self.frame_opts=NS();self.mel_opts=NS()
class Session:
 def __init__(self,path,sess_options,providers):self.options=sess_options
 def get_inputs(self):return [NS(name='features')]
class Tests(unittest.TestCase):
 def build(self,cpu,threads=None,affinity=None):
  ort=NS(SessionOptions=lambda:NS(),InferenceSession=Session)
  with patch.dict(sys.modules,onnxruntime=ort,kaldi_native_fbank=NS(FbankOptions=Options)),patch.object(module.os,'cpu_count',return_value=cpu),patch.object(module.os,'sched_getaffinity',return_value=set(range(affinity)),create=True) if affinity is not None else patch.object(module.os,'cpu_count',return_value=cpu):
   return module.SpeakerEmbedding('unused',intra_op_num_threads=threads)
 def test_conservative_small_machine(self):self.assertEqual(self.build(4).session.options.intra_op_num_threads,2)
 def test_unknown_cpu_count(self):self.assertEqual(self.build(None).session.options.intra_op_num_threads,2)
 def test_parallel_large_machine(self):self.assertEqual(self.build(8).session.options.intra_op_num_threads,4)
 def test_explicit_override(self):self.assertEqual(self.build(16,threads=1).session.options.intra_op_num_threads,1)
 def test_affinity_is_respected_when_available(self):self.assertEqual(self.build(32,affinity=4).session.options.intra_op_num_threads,2)
 def test_feature_options_unchanged(self):
  opts=self.build(4).fbank_options
  self.assertEqual(vars(opts.frame_opts),dict(samp_freq=16000,dither=0,window_type='hamming',snip_edges=True))
  self.assertEqual(vars(opts.mel_opts),dict(num_bins=80,low_freq=20,high_freq=0));self.assertEqual(opts.energy_floor,0)
if __name__=='__main__':unittest.main()
