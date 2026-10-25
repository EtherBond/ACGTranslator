"""Make explicit synthetic control cases from previously generated Windows TTS."""
import hashlib,json,wave
from pathlib import Path
import numpy as np
ROOT=Path(__file__).resolve().parents[1]
out=ROOT/'artifacts/turn-optimization-20261018/english-controls'
out.mkdir(exist_ok=False)
source=ROOT/'scripts/.tmp/turns'

def read(name):
    with wave.open(str(source/(name+'.wav')),'rb') as f:
        assert (f.getframerate(),f.getnchannels(),f.getsampwidth())==(16000,1,2)
        pcm=np.frombuffer(f.readframes(f.getnframes()),dtype='<i2')
    active=np.flatnonzero(np.abs(pcm.astype(np.int32))>164)
    return pcm[max(0,active[0]-320):min(len(pcm),active[-1]+320)]

a,b,c=read('a1'),read('a2'),read('b2')
first='Can you help me find the station?'
second='Yes, it is just around the corner.'
rows=[]
for name,pcm,text,boundary in [
    ('single',a,first,None),
    ('same_voice_no_gap',np.concatenate([a,b]),first+' '+second,None),
    ('two_voices_no_gap',np.concatenate([a,c]),first+' '+second,len(a)/16000),
    ('two_voices_short_gap',np.concatenate([a,np.zeros(2400,dtype='<i2'),c]),first+' '+second,len(a)/16000+.075),
    ('silence',np.zeros(48000,dtype='<i2'),' ',None),
]:
    with wave.open(str(out/(name+'.wav')),'wb') as f:
        f.setparams((1,2,16000,0,'NONE','not compressed')); f.writeframes(pcm.astype('<i2').tobytes())
    rows.append(dict(id=name,audio=name+'.wav',text=text,language='en',expected_boundary=boundary,
                     scope='Windows TTS synthetic control, not natural conversation ground truth',
                     pcm_sha256=hashlib.sha256(pcm.astype('<i2').tobytes()).hexdigest()))
(out/'manifest.jsonl').write_text(''.join(json.dumps(r)+'\n' for r in rows),encoding='utf-8')
print(len(rows),'synthetic controls prepared')
