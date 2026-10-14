"""Run real local ASR worker with optional speaker model and missing-model fallback."""
import base64,json,os,subprocess,sys
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
RESOURCES=ROOT/"Resources"
if not RESOURCES.exists(): RESOURCES=ROOT/"src/ACGTranslator.App/Resources"
model=ROOT/"models/speakers/wespeaker-resnet34/speaker.onnx"
if not model.exists(): raise FileNotFoundError("Install WeSpeaker in model manager before this optional test")
request={"type":"transcribe","language":"en","sample_rate":16000,
         "audio_base64":base64.b64encode(bytes(64000)).decode()}
for candidate in (model, model.with_name("missing-test-model.onnx")):
    response=subprocess.run([sys.executable,"-u",str(RESOURCES/"asr_worker.py"),
        "--model",str(ROOT/"models/asr/whisper-base"),"--device","cpu",
        "--turn-model",str(candidate)],
        input="\n".join(json.dumps(x) for x in [request,{"type":"shutdown"}])+"\n",
        capture_output=True,text=True,encoding="utf-8",timeout=90,
        env=dict(os.environ,HF_HUB_OFFLINE="1"))
    assert response.returncode==0, response.stderr
    lines=[json.loads(l) for l in response.stdout.splitlines()]
    assert lines[0]["ready"] and lines[1]["text"]=="", lines
    if candidate==model: assert "warning" not in lines[1],lines
    else: assert "warning" in lines[1],lines
print("optional speaker worker + missing model fallback passed")
