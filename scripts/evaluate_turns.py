"""Local synthetic checks, NOT a real-conversation accuracy benchmark."""
import json, sys, time, subprocess, wave, os
from pathlib import Path
import numpy as np
ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/"src/ACGTranslator.App/Resources"))
from turn_detector import SpeakerEmbedding, TurnDetector
from asr_worker import transcribe
from faster_whisper import WhisperModel
TEMP=ROOT/"scripts/.tmp/turns"
TEMP.mkdir(parents=True,exist_ok=True)

PS = r"""
param($Target)
Add-Type -AssemblyName System.Speech
$s = New-Object System.Speech.Synthesis.SpeechSynthesizer
$voices = @($s.GetInstalledVoices() | Where-Object { $_.VoiceInfo.Culture.Name -eq 'en-US' })
if ($voices.Count -lt 2) { throw 'Two en-US voices required' }
$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
foreach ($spec in @(@('a1',0,'Can you help me find the station?'),@('a2',0,'Yes, it is just around the corner.'),@('b2',1,'Yes, it is just around the corner.'))) {
    $s.SelectVoice($voices[[int]$spec[1]].VoiceInfo.Name)
    $s.SetOutputToWaveFile((Join-Path $Target ($spec[0]+'.wav')), $format)
    $s.Speak($spec[2])
    $s.SetOutputToNull()
}
$s.Dispose()
"""
script=TEMP/"synthesize.ps1"; script.write_text(PS,encoding="utf-8-sig")
subprocess.run(["powershell","-NoProfile","-File",str(script),str(TEMP)],check=True)
def read(name):
    with wave.open(str(TEMP/(name+".wav")),"rb") as f:
        a=np.frombuffer(f.readframes(f.getnframes()),dtype="<i2").astype(np.float32)/32768
    active=np.flatnonzero(np.abs(a)>.005)
    return a[max(0,active[0]-320):min(len(a),active[-1]+320)]
a,b,c=read("a1"),read("a2"),read("b2")
embed=SpeakerEmbedding(ROOT/"models/speakers/wespeaker-resnet34/speaker.onnx")
embed(np.zeros(16000,dtype=np.float32))
lat=[]
for _ in range(10):
    t=time.monotonic(); embed(a[:16000]); lat.append(time.monotonic()-t)
detector=TurnDetector(embed)
model=WhisperModel(str(ROOT/"models/asr/whisper-base"),device="cpu",compute_type="int8",cpu_threads=4)
opts=dict(vad_filter=True,condition_on_previous_text=False,beam_size=2,temperature=0.,
          compression_ratio_threshold=2.4,log_prob_threshold=-1.,no_speech_threshold=.55,
          vad_parameters=dict(min_speech_duration_ms=250,min_silence_duration_ms=320,speech_pad_ms=120))
list(model.transcribe(np.zeros(16000,dtype=np.float32),language="en",beam_size=1)[0])
rows=[]
for name,audio,boundary in [
    ("single",a,None),
    ("same_voice_no_gap",np.concatenate([a,b]),None),
    ("two_voices_no_gap",np.concatenate([a,c]),len(a)/16000),
    ("two_voices_short_gap",np.concatenate([a,np.zeros(2400,dtype=np.float32),c]),len(a)/16000+.075)]:
    results={}
    for mode in ("off","on"):
        t=time.monotonic()
        result=transcribe(model,audio,"en",opts,detector if mode=="on" else None)
        results[mode]=dict(seconds=round(time.monotonic()-t,3),**result)
    rows.append(dict(case=name,expected_boundary=boundary,duration=len(audio)/16000,results=results))
    print(json.dumps(rows[-1],ensure_ascii=True),flush=True)
report=dict(scope="Windows TTS synthetic English only; not real speech accuracy",embedding_1s_median_seconds=float(np.median(lat)),cases=rows)
(ROOT/"scripts/turn-evaluation.json").write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding="utf-8")
print("embedding median:",report["embedding_1s_median_seconds"])
