"""JSON-lines protocol shared by the local workers."""
import json
import os
import sys

# Libraries occasionally print to stdout; route that to stderr so the protocol pipe stays clean.
_protocol = sys.stdout
sys.stdout = sys.stderr
_dll_handles = []


def emit(value):
    # ASCII escaping keeps the pipe independent of the console code page.
    _protocol.write(json.dumps(value, ensure_ascii=True) + "\n")
    _protocol.flush()


def add_dll_directories(paths):
    """Make CUDA runtime DLLs shipped as downloadable components visible to native extensions."""
    for path in paths or []:
        if not path or not os.path.isdir(path):
            continue
        _dll_handles.append(os.add_dll_directory(path))
        os.environ["PATH"] = path + os.pathsep + os.environ.get("PATH", "")


def requests():
    """Yield decoded requests until stdin closes or a shutdown request arrives."""
    # The host writes UTF-8. Without this, Chinese Windows decodes stdin as GBK and garbles Japanese text.
    sys.stdin.reconfigure(encoding="utf-8", errors="replace")
    for line in sys.stdin:
        line = line.lstrip("\ufeff").strip()
        if not line:
            continue
        try:
            request = json.loads(line)
        except json.JSONDecodeError as exc:
            emit({"error": f"无法解析请求：{exc}"})
            continue
        if request.get("type") == "shutdown":
            return
        if request.get("type") == "ping":
            emit({"pong": True})
            continue
        yield request
