"""CPU-only speaker equivalence/latency diagnostic; never starts ASR or downloads.

Uses fixed recorded word alignments, real Japanese windows and constructed mixed
speaker controls. This measures implementation equivalence, not turn accuracy.
"""
import argparse,hashlib,importlib.util,json,os,statistics,sys,time,wave
from pathlib import Path
import numpy as np
sys.dont_write_bytecode=True
def load(path,name):
 spec=importlib.util.spec_from_file_location(name,path);m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m);return m
def rows(path):return [json.loads(x) for x in path.read_text(encoding='utf-8-sig').splitlines() if x.strip()]
def read(path):
 with wave.open(str(path),'rb') as f:
  assert (f.getframerate(),f.getnchannels(),f.getsampwidth())==(16000,1,2)
  return np.frombuffer(f.readframes(f.getnframes()),dtype='<i2').astype(np.float32)/32768
def digest(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def stat(values):return dict(median_ms=1000*statistics.median(values),p95_ms=1000*float(np.percentile(values,95)),mean_ms=1000*statistics.mean(values))
class SessionTimer:
 def __init__(self,session):self.session=session;self.elapsed=0.;self.feature=None
 def run(self,*args):
  self.feature=next(iter(args[1].values()))
  t=time.perf_counter();v=self.session.run(*args);self.elapsed=time.perf_counter()-t;return v
def main():
 p=argparse.ArgumentParser(description=__doc__);p.add_argument('--root',type=Path,required=True);p.add_argument('--work',type=Path,required=True);p.add_argument('--before',type=Path,required=True);p.add_argument('--after',type=Path,required=True);p.add_argument('--out',type=Path,required=True);a=p.parse_args()
 before=load(a.before,'speaker_before');after=load(a.after,'speaker_after')
 model=a.root/'models/speakers/wespeaker-resnet34/speaker.onnx';bundle=a.root/'data/game-captures/646815f4f9ca435d83401f2f5d9b3e22/handoffs/20261018-150756-1bfa8904'
 source=read(bundle/'capture.wav');aligned=rows(a.work/'whisper-large-v3-turbo-recheck.jsonl')
 cases=[]
 for r in aligned:
  words=[w for seg in r['segments'] for w in seg['words'] if w['end']>w['start']]
  if len(before.candidates(words))<2:continue
  clip=source[round(r['start_s']*16000):round(r['end_s']*16000)]
  cases.append((f'real-{r["n"]}',clip,words))
  if len(cases)==12:break
 natural=list(cases)
 # Deliberate joins exercise persisted-dissimilarity branches, not ground truth.
 for i in range(4):
  x=natural[i][1][:32000];y=natural[-i-1][1][:32000];clip=np.concatenate([x,y])
  words=[dict(start=j*.2,end=min((j+1)*.2,len(clip)/16000)) for j in range(int(len(clip)/3200))]
  cases.append((f'constructed-join-{i}',clip,words))
 windows=[audio[:min(len(audio),n)] for _,audio,_ in natural[:8] for n in (12800,16000,23680,25600)]
 reference_vectors=None;reference_features=None;reference_detection=None;report={}
 for profile,cls,kwargs in [('before2',before.SpeakerEmbedding,{}),('after2',after.SpeakerEmbedding,{'intra_op_num_threads':2}),('after4',after.SpeakerEmbedding,{'intra_op_num_threads':4})]:
  embed=cls(model,**kwargs);timer=SessionTimer(embed.session);embed.session=timer
  for x in windows[:4]:embed(x)
  totals=[];inference=[];feature=[];vectors=[];features=[];cpu=[]
  for repeat in range(5):
   c=time.process_time();tbatch=time.perf_counter()
   for x in windows:
    t=time.perf_counter();v=embed(x);elapsed=time.perf_counter()-t
    totals.append(elapsed);inference.append(timer.elapsed);feature.append(elapsed-timer.elapsed)
    if repeat==0:vectors.append(v);features.append(timer.feature.copy())
   cpu.append((time.process_time()-c)/len(windows))
  if reference_vectors is None:reference_vectors=vectors;reference_features=features
  detections=[]
  for name,x,words in cases:
   item={'case':name,'candidate_points':before.candidates(words)}
   for budget in [float('inf'),.65]:
    calls=[]
    def traced(samples):
     v=embed(samples);calls.append((hashlib.sha256(samples.tobytes()).hexdigest(),v.tolist()));return v
    d=before.TurnDetector(traced,budget_seconds=budget);t=time.perf_counter()
    try:boundary=d.detect(x,words);status='completed'
    except TimeoutError:boundary=None;status='timeout'
    key='unbounded' if budget==float('inf') else 'budget_650ms'
    item[key]={'status':status,'boundary':boundary,'elapsed_s':time.perf_counter()-t,'stats':d.last_stats,'window_hashes':[c[0] for c in calls],'vectors':[c[1] for c in calls]}
   detections.append(item)
  if reference_detection is None:reference_detection=detections
  differences=[]
  for ref,got in zip(reference_detection,detections):
   for key in ['unbounded','budget_650ms']:
    r,g=ref[key],got[key]
    if any(r[k]!=g[k] for k in ['status','boundary','window_hashes']):differences.append({'case':got['case'],'mode':key,'before_status':r['status'],'after_status':g['status'],'before_boundary':r['boundary'],'after_boundary':g['boundary']})
  report[profile]={'total':stat(totals),'inference':stat(inference),'feature_and_normalization':stat(feature),'cpu_mean_ms_per_call':1000*statistics.mean(cpu),'max_vector_error':max(float(np.max(np.abs(x-y))) for x,y in zip(reference_vectors,vectors)),'max_feature_error':max(float(np.max(np.abs(x-y))) for x,y in zip(reference_features,features)),'detection_differences':differences,'detections':detections}
  print(profile,json.dumps({k:v for k,v in report[profile].items() if k!='detections'}),flush=True)
  del embed,timer
 result={'scope':__doc__,'cpu_count':os.cpu_count(),'model_sha256':digest(model),'before_sha256':digest(a.before),'after_sha256':digest(a.after),'window_count':len(windows),'iterations':5,'natural_detection_cases':len(natural),'constructed_join_cases':4,'profiles':report}
 a.out.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
if __name__=='__main__':main()
