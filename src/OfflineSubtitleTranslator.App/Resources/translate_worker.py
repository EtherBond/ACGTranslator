"""Local translation JSON-lines worker.

Engines:
  argos  Argos Translate packages (ja→en→zh pivot)
  nllb   NLLB-200 converted for CTranslate2
  llm    GGUF model served by a private llama.cpp server; prompt style per model family
"""
import argparse
import collections
import json
import os
import re
import secrets
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request

from worker_protocol import add_dll_directories, emit, requests

TARGET = "zh"
# Previous lines given to context-aware engines.
HISTORY_LINES = 3
WARM_UP = {"ja": "こんにちは。", "en": "Hello."}


class ArgosEngine:
    PIVOT = "en"

    def __init__(self, args):
        # Must be set before argostranslate reads its settings.
        os.environ["ARGOS_TRANSLATE_PACKAGE_DIR"] = args.model
        os.environ["ARGOS_DEVICE_TYPE"] = "cuda" if args.device == "cuda" else "cpu"
        force_offline_stanza()
        import argostranslate.translate as argos
        self._argos = argos
        self._cache = {}

    def _lookup(self, source, target):
        key = (source, target)
        if key not in self._cache:
            try:
                self._cache[key] = self._argos.get_translation_from_codes(source, target)
            except Exception:
                self._cache[key] = None
        return self._cache[key]

    def translate(self, text, source, history):
        direct = self._lookup(source, TARGET)
        if direct is not None:
            return direct.translate(text)
        first, second = self._lookup(source, self.PIVOT), self._lookup(self.PIVOT, TARGET)
        if first is None or second is None:
            raise RuntimeError(f"Argos 翻译包中没有 {source}→{TARGET} 的路径")
        return second.translate(first.translate(text))


def force_offline_stanza():
    """Stanza checks GitHub for resources.json updates on every Pipeline() by default; the packages already ship it."""
    import functools
    import stanza

    original = stanza.Pipeline

    @functools.wraps(original)
    def offline_pipeline(*args, **kwargs):
        kwargs["download_method"] = None
        return original(*args, **kwargs)

    stanza.Pipeline = offline_pipeline


class NllbEngine:
    CODES = {"ja": "jpn_Jpan", "en": "eng_Latn", "zh": "zho_Hans"}

    def __init__(self, args):
        import ctranslate2
        import sentencepiece
        # sentencepiece's C++ loader cannot open non-ASCII paths on Windows (e.g. E:\翻译器), so pass the bytes.
        with open(os.path.join(args.model, "sentencepiece.bpe.model"), "rb") as model_file:
            self._sp = sentencepiece.SentencePieceProcessor(model_proto=model_file.read())
        self._translator = ctranslate2.Translator(args.model, device=args.device)

    def translate(self, text, source, history):
        tokens = [self.CODES[source]] + self._sp.encode(text, out_type=str) + ["</s>"]
        result = self._translator.translate_batch(
            [tokens], target_prefix=[[self.CODES[TARGET]]], beam_size=4, max_decoding_length=256, repetition_penalty=1.1)
        return self._sp.decode(result[0].hypotheses[0][1:])


class LlamaServer:
    """A llama.cpp server bound to loopback with a random API key, owned by this worker."""

    def __init__(self, executable, model, gpu, extra_args=()):
        self._key = secrets.token_hex(16)
        port = _free_port()
        self._url = f"http://127.0.0.1:{port}"
        # Never route loopback requests through a system proxy.
        self._opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        self._log = collections.deque(maxlen=30)
        command = [
            executable, "-m", model, "--host", "127.0.0.1", "--port", str(port), "--api-key", self._key,
            "-c", "4096", "-np", "1", "--no-webui", "--offline", "-ngl", "999" if gpu else "0", *extra_args,
        ]
        self._process = subprocess.Popen(
            command, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
            cwd=os.path.dirname(executable), creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        threading.Thread(target=self._drain, daemon=True).start()
        self._wait_ready(timeout=300)

    def _drain(self):
        for raw in self._process.stderr:
            line = raw.decode("utf-8", errors="replace").strip()
            if line:
                self._log.append(line)

    def _last_error(self):
        for line in reversed(self._log):
            if any(word in line.lower() for word in ("error", "failed", "unable", "out of memory")):
                return line
        return self._log[-1] if self._log else "没有输出"

    def _wait_ready(self, timeout):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            if self._process.poll() is not None:
                raise RuntimeError(f"llama.cpp 启动失败（退出码 {self._process.returncode}）：{self._last_error()}")
            try:
                with self._opener.open(f"{self._url}/health", timeout=2) as response:
                    if response.status == 200:
                        return
            except (urllib.error.URLError, OSError):
                pass
            time.sleep(0.25)
        raise RuntimeError(f"llama.cpp 在 {timeout} 秒内没有加载完成：{self._last_error()}")

    def chat(self, messages, **params):
        body = json.dumps({"messages": messages, "stream": False, **params}).encode("utf-8")
        request = urllib.request.Request(
            f"{self._url}/v1/chat/completions", data=body,
            headers={"Content-Type": "application/json", "Authorization": f"Bearer {self._key}"})
        try:
            with self._opener.open(request, timeout=120) as response:
                data = json.load(response)
        except urllib.error.HTTPError as exc:
            raise RuntimeError(f"llama.cpp 请求失败（HTTP {exc.code}）：{exc.read().decode('utf-8', 'replace')[:200]}")
        except (urllib.error.URLError, OSError) as exc:
            if self._process.poll() is not None:
                raise RuntimeError(f"llama.cpp 已退出：{self._last_error()}")
            raise RuntimeError(f"llama.cpp 请求失败：{exc}")
        return data["choices"][0]["message"]["content"] or ""

    def close(self):
        if self._process.poll() is None:
            self._process.terminate()
            try:
                self._process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self._process.kill()


def _free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


LANGUAGE_NAMES = {"ja": "日语", "en": "英语"}

SAKURA_SYSTEM = (
    "你是一个视觉小说翻译模型，可以通顺地使用给定的术语表以指定的风格将日文翻译成简体中文，"
    "并联系上下文正确使用人称代词，注意不要混淆使役态和被动态的主语和宾语，"
    "不要擅自添加原文中没有的特殊符号，也不要擅自增加或减少换行。"
)

QWEN_SYSTEM = (
    "你是专业的影视字幕译者。把用户给出的{language}字幕翻译成自然流畅、符合中文口语习惯的简体中文。"
    "只输出这一句字幕的译文：不要解释，不要输出原文，不要加引号或注释。人名和专有名词前后保持一致。"
)


def sakura_prompt(text, source, history):
    # Template from the Sakura-GalTransl v3 model card; the glossary is left empty.
    parts = []
    if history:
        parts.append("历史翻译：" + "\n".join(translation for _, translation in history) + "\n")
    parts.append("参考以下术语表（可为空，格式为src->dst #备注）：\n")
    parts.append("根据以上术语表的对应关系和备注，结合历史剧情和上下文，将下面的文本从日文翻译成简体中文：\n" + text)
    messages = [{"role": "system", "content": SAKURA_SYSTEM}, {"role": "user", "content": "\n".join(parts)}]
    return messages, {"temperature": 0.3, "top_p": 0.8, "frequency_penalty": 0.1}


def hymt_prompt(text, source, history):
    # Templates from the Hy-MT2 model card ("default" and "background information"). No system prompt.
    if history:
        background = "\n".join(f"{original} → {translation}" for original, translation in history)
        content = (f"【背景信息】\n以下是前几句字幕及其译文：\n{background}\n\n"
                   f"请结合背景信息将以下文本翻译为中文，注意只需要输出翻译后的结果，不要额外解释。\n\n【待翻译文本】\n{text}")
    else:
        content = f"将以下文本翻译为中文，注意只需要输出翻译后的结果，不要额外解释：\n\n{text}"
    return [{"role": "user", "content": content}], {"temperature": 0.7, "top_p": 0.6, "top_k": 20, "repeat_penalty": 1.05}


def qwen_prompt(text, source, history):
    system = QWEN_SYSTEM.format(language=LANGUAGE_NAMES.get(source, "外语"))
    if history:
        context = "\n".join(f"{original} → {translation}" for original, translation in history)
        content = f"前文（仅供理解语境，不要翻译）：\n{context}\n\n需要翻译的字幕：\n{text}"
    else:
        content = text
    params = {"temperature": 0.3, "top_p": 0.8, "top_k": 20, "chat_template_kwargs": {"enable_thinking": False}}
    return [{"role": "system", "content": system}, {"role": "user", "content": content}], params


PROMPTS = {"sakura": sakura_prompt, "hymt": hymt_prompt, "qwen": qwen_prompt}
ANSWER_PREFIX = re.compile(r"^(译文|翻译|中文|简体中文)\s*[:：]\s*")
QUOTE_PAIRS = {"「": "」", "“": "”", "\"": "\"", "『": "』"}


def clean_llm_output(output, source_text):
    output = re.sub(r"<think>.*?</think>", "", output, flags=re.S)
    lines = [line.strip() for line in output.splitlines() if line.strip()]
    text = ANSWER_PREFIX.sub("", " ".join(lines)).strip()
    # Drop quotes the model wrapped around the whole answer, unless the source was quoted too.
    if len(text) > 2 and QUOTE_PAIRS.get(text[0]) == text[-1] and not source_text.strip().startswith(text[0]):
        text = text[1:-1].strip()
    return text


class LlmEngine:
    def __init__(self, args):
        if not args.llama_server or not os.path.isfile(args.llama_server):
            raise RuntimeError("没有安装 llama.cpp 运行组件，请在模型管理中下载")
        self._prompt = PROMPTS[args.style]
        extra = ["--reasoning-budget", "0"] if args.style == "qwen" else []
        self._server = LlamaServer(args.llama_server, args.model, gpu=args.device != "cpu", extra_args=extra)

    def translate(self, text, source, history):
        messages, params = self._prompt(text, source, history)
        # Subtitles are short; this bounds the cost of a model that starts rambling.
        params["max_tokens"] = min(512, 48 + 3 * len(text))
        return clean_llm_output(self._server.chat(messages, **params), text)

    def close(self):
        self._server.close()


ENGINES = {"argos": ArgosEngine, "nllb": NllbEngine, "llm": LlmEngine}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--engine", choices=sorted(ENGINES), default="argos")
    parser.add_argument("--model", required=True, help="model directory, or .gguf file for the llm engine")
    parser.add_argument("--device", choices=["cpu", "cuda", "gpu"], default="cpu")
    parser.add_argument("--style", choices=sorted(PROMPTS), default="qwen")
    parser.add_argument("--llama-server", default="")
    parser.add_argument("--dll-dir", action="append", default=[])
    parser.add_argument("--languages", default="ja,en", help="source languages to warm up")
    args = parser.parse_args()
    add_dll_directories(args.dll_dir)
    try:
        engine = ENGINES[args.engine](args)
    except Exception as exc:
        emit({"error": f"无法加载翻译引擎：{exc}"})
        return 2
    history = collections.deque(maxlen=HISTORY_LINES)
    try:
        # Load models and sentence splitters before reporting ready, so the first subtitle is not delayed.
        for source in args.languages.split(","):
            if source in WARM_UP:
                try:
                    engine.translate(WARM_UP[source], source, [])
                except Exception as exc:
                    print(f"warm-up {source}->{TARGET} failed: {exc}", file=sys.stderr)
        emit({"ready": True})
        for request in requests():
            try:
                kind = request.get("type")
                if kind == "reset":
                    history.clear()
                    emit({"ok": True})
                    continue
                if kind != "translate":
                    emit({"error": "未知请求类型"})
                    continue
                text, source = request["text"].strip(), request["source"]
                if source not in LANGUAGE_NAMES:
                    raise RuntimeError(f"不支持的源语言：{source}")
                translation = engine.translate(text, source, list(history)).strip()
                if translation:
                    history.append((text, translation))
                emit({"text": translation})
            except Exception as exc:
                emit({"error": str(exc)})
    finally:
        if hasattr(engine, "close"):
            engine.close()
    return 0


if __name__ == "__main__": sys.exit(main())
