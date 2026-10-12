"""Compare local translation engines on the same subtitle lines.

Run from the repository root with the project venv:
    .\\.venv\\Scripts\\python.exe scripts\\compare_translators.py [engine-id ...]

Each engine runs in its own translate_worker process, exactly as the app uses it. Lines are sent
in order so context-aware engines see the preceding subtitles. Results go to
scripts/translator-comparison.md and .json.
"""
from __future__ import annotations

import json
import statistics
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PYTHON = ROOT / ".venv" / "Scripts" / "python.exe"
WORKER = ROOT / "src" / "ACGTranslator.App" / "Resources" / "translate_worker.py"
MODELS = ROOT / "models"
LLAMA = MODELS / "runtimes" / "llama-cuda13" / "llama-server.exe"
CUBLAS = MODELS / "runtimes" / "cuda12-cublas"

ENGINES = {
    "argos": ["--engine", "argos", "--model", str(MODELS / "translators" / "argos")],
    "nllb-600m": ["--engine", "nllb", "--model", str(MODELS / "translators" / "nllb-600m"), "--device", "cuda", "--dll-dir", str(CUBLAS)],
    "nllb-1.3b": ["--engine", "nllb", "--model", str(MODELS / "translators" / "nllb-1.3b"), "--device", "cuda", "--dll-dir", str(CUBLAS)],
    "hymt2-1.8b": ["--engine", "llm", "--style", "hymt", "--model", str(MODELS / "translators" / "hymt2-1.8b" / "Hy-MT2-1.8B-Q6_K.gguf"), "--device", "gpu", "--llama-server", str(LLAMA)],
    "hymt2-7b": ["--engine", "llm", "--style", "hymt", "--model", str(MODELS / "translators" / "hymt2-7b" / "Hy-MT2-7B-Q4_K_M.gguf"), "--device", "gpu", "--llama-server", str(LLAMA)],
    "qwen3-8b": ["--engine", "llm", "--style", "qwen", "--model", str(MODELS / "translators" / "qwen3-8b" / "Qwen3-8B-Q4_K_M.gguf"), "--device", "gpu", "--llama-server", str(LLAMA)],
    "sakura-7b": ["--engine", "llm", "--style", "sakura", "--model", str(MODELS / "translators" / "sakura-7b" / "Sakura-Galtransl-7B-v3.7.gguf"), "--device", "gpu", "--llama-server", str(LLAMA), "--languages", "ja"],
}
JAPANESE_ONLY = {"sakura-7b"}
# --cpu: what users without an NVIDIA GPU get. The Vulkan build also contains the CPU backend.
LLAMA_CPU = MODELS / "runtimes" / "llama-vulkan" / "llama-server.exe"


def cpu_variant(args: list[str]) -> list[str]:
    out = list(args)
    if "--device" in out:
        out[out.index("--device") + 1] = "cpu"
    if "--llama-server" in out:
        out[out.index("--llama-server") + 1] = str(LLAMA_CPU)
    return out

# Short dialogue scenes, like what Whisper produces from anime, dramas and news.
SCENES = [
    ("ja", "日常对话", [
        "ねえ、昨日のこと覚えてる？",
        "うん、まさかあんなことになるとは思わなかったよ。",
        "でもさ、結局は先輩のおかげで助かったんだよね。",
        "今度ちゃんとお礼言わなきゃ。",
    ]),
    ("ja", "动画台词", [
        "お前なんかに負けるわけねえだろ！",
        "諦めるのはまだ早い。最後まで戦うんだ。",
        "この力…まさか、封印が解けたのか？",
    ]),
    ("ja", "新闻", [
        "政府は来年度から新たな少子化対策を実施すると発表しました。",
        "東京都内では今日、最高気温が三十五度を超える猛暑日となりました。",
    ]),
    ("ja", "省略主语", [
        "もう行かなきゃ。",
        "えっ、もう？まだ来たばかりじゃん。",
        "ごめん、明日早いんだ。",
    ]),
    ("en", "日常对话", [
        "Hey, are you coming to the party tonight?",
        "I don't know, I've got a ton of work to catch up on.",
        "Come on, it's Friday. You can't be serious.",
    ]),
    ("en", "影视台词", [
        "We're running out of time. The whole city is counting on us.",
        "I never asked for any of this.",
        "Whatever happens, don't look back.",
    ]),
    ("en", "新闻/讲解", [
        "The central bank decided to keep interest rates unchanged for the third consecutive month.",
        "In this video, we'll walk through how to set up the development environment step by step.",
    ]),
]


class Worker:
    def __init__(self, args: list[str]):
        self.process = subprocess.Popen(
            [str(PYTHON), "-u", str(WORKER), *args], cwd=WORKER.parent,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        start = time.perf_counter()
        ready = self._read()
        self.load_seconds = time.perf_counter() - start
        if "error" in ready:
            raise RuntimeError(ready["error"])

    def _read(self) -> dict:
        line = self.process.stdout.readline()
        if not line:
            raise RuntimeError(f"worker exited with {self.process.wait()}")
        return json.loads(line.decode("ascii"))

    def send(self, message: dict) -> dict:
        self.process.stdin.write((json.dumps(message, ensure_ascii=False) + "\n").encode("utf-8"))
        self.process.stdin.flush()
        return self._read()

    def close(self):
        try:
            self.send({"type": "shutdown"})
        except Exception:
            pass
        try:
            self.process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            self.process.kill()


def run_engine(engine: str, cpu: bool = False) -> dict:
    print(f"== {engine}{' (cpu)' if cpu else ''}", flush=True)
    try:
        worker = Worker(cpu_variant(ENGINES[engine]) if cpu else ENGINES[engine])
    except Exception as exc:
        print(f"   failed to start: {exc}", flush=True)
        return {"engine": engine + (" (cpu)" if cpu else ""), "error": str(exc)}
    print(f"   loaded in {worker.load_seconds:.1f}s", flush=True)
    scenes, latencies = [], []
    try:
        for language, title, lines in SCENES:
            if language == "en" and engine in JAPANESE_ONLY:
                continue
            # Each scene starts without context, like a new video.
            worker.send({"type": "reset"})
            results = []
            for line in lines:
                start = time.perf_counter()
                response = worker.send({"type": "translate", "source": language, "target": "zh", "text": line})
                elapsed = time.perf_counter() - start
                latencies.append(elapsed)
                text = response.get("text", "") if "error" not in response else f"[错误] {response['error']}"
                results.append({"source": line, "translation": text, "seconds": round(elapsed, 3)})
                print(f"   {elapsed:5.2f}s  {line}  →  {text}", flush=True)
            scenes.append({"language": language, "title": title, "lines": results})
    finally:
        worker.close()
    return {
        "engine": engine + (" (cpu)" if cpu else ""),
        "load_seconds": round(worker.load_seconds, 1),
        "median_seconds": round(statistics.median(latencies), 3),
        "max_seconds": round(max(latencies), 3),
        "scenes": scenes,
    }


def write_report(results: list[dict], suffix: str = ""):
    json_path = ROOT / "scripts" / f"translator-comparison{suffix}.json"
    json_path.write_text(json.dumps(results, ensure_ascii=False, indent=1), encoding="utf-8")
    out = ["# 翻译引擎对比", "", f"生成时间：{time.strftime('%Y-%m-%d %H:%M')}", "",
           "| 引擎 | 加载 | 单句中位延迟 | 单句最大延迟 |", "|---|---|---|---|"]
    for r in results:
        if "error" in r:
            out.append(f"| {r['engine']} | 启动失败：{r['error']} | | |")
        else:
            out.append(f"| {r['engine']} | {r['load_seconds']} s | {r['median_seconds']} s | {r['max_seconds']} s |")
    ok = [r for r in results if "error" not in r]
    for language, title, lines in SCENES:
        out += ["", f"## {'日语' if language == 'ja' else '英语'} · {title}", ""]
        for index, line in enumerate(lines):
            out.append(f"**{line}**")
            out.append("")
            for r in ok:
                scene = next((s for s in r["scenes"] if s["language"] == language and s["title"] == title), None)
                if scene:
                    out.append(f"- {r['engine']}：{scene['lines'][index]['translation']}")
            out.append("")
    md_path = ROOT / "scripts" / f"translator-comparison{suffix}.md"
    md_path.write_text("\n".join(out), encoding="utf-8")
    print(f"report: {md_path}")


def main() -> int:
    cpu = "--cpu" in sys.argv
    selected = [a for a in sys.argv[1:] if a != "--cpu"] or list(ENGINES)
    unknown = [e for e in selected if e not in ENGINES]
    if unknown:
        print(f"unknown engines: {unknown}; choose from {list(ENGINES)}", file=sys.stderr)
        return 2
    results = [run_engine(engine, cpu) for engine in selected]
    write_report(results, "-cpu" if cpu else "")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
