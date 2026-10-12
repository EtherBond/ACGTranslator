"""Compare local Whisper models on synthesized speech.

Run from the repository root with the project venv:
    .\\.venv\\Scripts\\python.exe scripts\\compare_asr.py [model-id ...]

Test audio is generated with the Windows speech synthesizer (Japanese and English voices), so the
error rates are optimistic compared with anime or film audio; the comparison is still useful for
relative accuracy and for latency. Each model runs in its own asr_worker process, as in the app.
"""
from __future__ import annotations

import base64
import json
import statistics
import subprocess
import sys
import tempfile
import time
import unicodedata
import wave
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PYTHON = ROOT / ".venv" / "Scripts" / "python.exe"
WORKER = ROOT / "src" / "ACGTranslator.App" / "Resources" / "asr_worker.py"
MODELS = ROOT / "models" / "asr"
CUBLAS = ROOT / "models" / "runtimes" / "cuda12-cublas"

LINES = {
    "ja": [
        "ねえ、昨日のこと覚えてる？",
        "でもさ、結局は先輩のおかげで助かったんだよね。",
        "諦めるのはまだ早い。最後まで戦うんだ。",
        "政府は来年度から新たな少子化対策を実施すると発表しました。",
        "東京都内では今日、最高気温が三十五度を超える猛暑日となりました。",
        "えっ、もう？まだ来たばかりじゃん。",
    ],
    "en": [
        "Hey, are you coming to the party tonight?",
        "I don't know, I've got a ton of work to catch up on.",
        "We're running out of time. The whole city is counting on us.",
        "The central bank decided to keep interest rates unchanged for the third consecutive month.",
    ],
}
VOICES = {"ja": "ja-JP", "en": "en-US"}

SYNTHESIZE = r"""
# Inside a script block $args means the block's own arguments, so copy the script's first.
$culture, $wavPath, $textPath = $args[0], $args[1], $args[2]
Add-Type -AssemblyName System.Speech
$s = New-Object System.Speech.Synthesis.SpeechSynthesizer
$voice = $s.GetInstalledVoices() | Where-Object { $_.VoiceInfo.Culture.Name -eq $culture } | Select-Object -First 1
if ($null -eq $voice) { exit 3 }
$s.SelectVoice($voice.VoiceInfo.Name)
$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$s.SetOutputToWaveFile($wavPath, $format)
$s.Speak([System.IO.File]::ReadAllText($textPath, [System.Text.Encoding]::UTF8))
$s.Dispose()
"""


def synthesize(language: str, text: str, directory: Path, index: int) -> list[float]:
    wav = directory / f"{language}-{index}.wav"
    text_file = directory / f"{language}-{index}.txt"
    text_file.write_text(text, encoding="utf-8")
    script = directory / "speak.ps1"
    script.write_text(SYNTHESIZE, encoding="utf-8-sig")
    subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script), VOICES[language], str(wav), str(text_file)], check=True)
    with wave.open(str(wav), "rb") as source:
        frames = source.readframes(source.getnframes())
    import array
    samples = array.array("h", frames)
    return [value / 32768.0 for value in samples]


def normalize(text: str) -> str:
    # Character-level comparison ignoring punctuation, spacing and case.
    text = unicodedata.normalize("NFKC", text).lower()
    return "".join(ch for ch in text if unicodedata.category(ch)[0] in "LN")


def edit_distance(a: str, b: str) -> int:
    previous = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        current = [i]
        for j, cb in enumerate(b, 1):
            current.append(min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + (ca != cb)))
        previous = current
    return previous[-1]


class Worker:
    def __init__(self, args: list[str]):
        self.process = subprocess.Popen([str(PYTHON), "-u", str(WORKER), *args], cwd=WORKER.parent,
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        start = time.perf_counter()
        ready = self.read()
        self.load_seconds = time.perf_counter() - start
        if "error" in ready:
            raise RuntimeError(ready["error"])

    def read(self) -> dict:
        line = self.process.stdout.readline()
        if not line:
            raise RuntimeError(f"worker exited with {self.process.wait()}")
        return json.loads(line.decode("ascii"))

    def transcribe(self, samples: list[float], language: str) -> dict:
        import struct
        audio = base64.b64encode(struct.pack(f"<{len(samples)}f", *samples)).decode()
        self.process.stdin.write((json.dumps({"type": "transcribe", "sample_rate": 16000, "audio_base64": audio, "language": language}) + "\n").encode())
        self.process.stdin.flush()
        return self.read()

    def close(self):
        try:
            self.process.stdin.write(b'{"type":"shutdown"}\n')
            self.process.stdin.flush()
            self.process.wait(timeout=15)
        except Exception:
            self.process.kill()


def run(model: str, device: str, clips: dict) -> dict:
    label = f"{model} ({device})"
    print(f"== {label}", flush=True)
    args = ["--model", str(MODELS / model), "--device", device]
    if device == "cuda":
        args += ["--dll-dir", str(CUBLAS)]
    try:
        worker = Worker(args)
    except Exception as exc:
        print(f"   failed: {exc}", flush=True)
        return {"model": label, "error": str(exc)}
    result = {"model": label, "load_seconds": round(worker.load_seconds, 1)}
    try:
        for language, items in clips.items():
            errors = total = 0
            latencies, rows = [], []
            for reference, samples in items:
                # Auto-detect, as in the default app setting.
                start = time.perf_counter()
                response = worker.transcribe(samples, "auto")
                latencies.append(time.perf_counter() - start)
                text = response.get("text", f"[错误] {response.get('error')}")
                wrong_language = response.get("language") != language
                distance = edit_distance(normalize(reference), normalize(text))
                errors += distance
                total += len(normalize(reference))
                rows.append({"reference": reference, "text": text, "language": response.get("language"), "wrong_language": wrong_language})
                print(f"   {latencies[-1]:5.2f}s  [{response.get('language')}] {text}", flush=True)
            result[language] = {"cer": round(errors / total, 3), "median_seconds": round(statistics.median(latencies), 3), "rows": rows}
    finally:
        worker.close()
    return result


def main() -> int:
    models = sys.argv[1:] or [p.name for p in sorted(MODELS.iterdir()) if (p / "model.bin").exists()]
    with tempfile.TemporaryDirectory() as temp:
        directory = Path(temp)
        clips = {language: [(text, synthesize(language, text, directory, i)) for i, text in enumerate(lines)] for language, lines in LINES.items()}
    results = []
    for model in models:
        results.append(run(model, "cuda", clips))
    # CPU numbers for the small models show what users without a GPU can expect.
    for model in [m for m in models if m in ("whisper-base", "whisper-small")]:
        results.append(run(model, "cpu", clips))
    out = ["# 识别模型对比（合成语音）", "", "| 模型 | 加载 | 日语字错率 | 日语单段延迟 | 英语字错率 | 英语单段延迟 |", "|---|---|---|---|---|---|"]
    for r in results:
        if "error" in r:
            out.append(f"| {r['model']} | 失败：{r['error']} | | | | |")
            continue
        out.append(f"| {r['model']} | {r['load_seconds']} s | {r['ja']['cer']:.1%} | {r['ja']['median_seconds']} s | {r['en']['cer']:.1%} | {r['en']['median_seconds']} s |")
    (ROOT / "scripts" / "asr-comparison.md").write_text("\n".join(out) + "\n", encoding="utf-8")
    (ROOT / "scripts" / "asr-comparison.json").write_text(json.dumps(results, ensure_ascii=False, indent=1), encoding="utf-8")
    print("\n".join(out))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
