"""Conservative static KAG candidate importer; never executes a game script.

Supported subset: [voice storage="file.ogg"] (or @voice storage=...), then one
plain dialogue line ending in [p]/[l]. Configure the voice tag.
Unknown tags, expressions, ambiguous audio names and multi-line text are rejected.
Candidates require review before galgame_bench.py prepare accepts them.
"""
from __future__ import annotations

import argparse
from collections import defaultdict
import json
from pathlib import Path
import re

VOICE = re.compile(r'^\[([A-Za-z_][\w]*)\s+storage\s*=\s*"([^"\r\n]+)"\s*\]$')
VOICE_AT = re.compile(r'^@([A-Za-z_][\w]*)\s+storage\s*=\s*"([^"\r\n]+)"\s*$')
SPEAKER = re.compile(r'^\[name\s+name\s*=\s*"([^"\r\n]+)"\s*\]$')


def candidates(script, text, lookup, game, language, voice_tag):
    rows, issues = [], []
    pending, speaker, order = None, "", 0

    def reject(reason, line):
        issues.append({"script": str(script), "line": line, "reason": reason})

    for number, raw in enumerate(text.splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith(";"):
            continue
        voice = VOICE.fullmatch(line) or VOICE_AT.fullmatch(line)
        if voice and voice[1] == voice_tag:
            if pending:
                reject("Previous voice has no supported dialogue", pending[1])
            name = voice[2].replace("\\", "/")
            # Dynamic names/macros cannot become filenames or implied labels.
            if any(c in name for c in "%&{}[]$") or ".." in Path(name).parts:
                reject("Dynamic/unsafe storage expression", number)
                pending = None
            else:
                pending = (name, number)
            continue
        name = SPEAKER.fullmatch(line)
        if name:
            speaker = name[1]
            continue
        if line.startswith(("@", "*")) or ("[" in line and not re.fullmatch(r'[^\[\]]+(?:\[(?:p|l|r)\])+', line)):
            if pending:
                reject("Unknown/control tag after voice; pairing discarded", number)
            pending, speaker = None, ""
            order += 1
            continue
        cleaned = re.sub(r'\[(?:p|l|r)\]', "", line).strip()
        order += 1  # Includes unvoiced lines, so missing dialogue cannot create artificial adjacency.
        if not pending:
            continue
        storage, source_line = pending
        pending = None
        matches = lookup.get(storage.casefold(), [])
        if len(matches) != 1:
            reject(f"Missing or ambiguous audio for storage: {storage}", source_line)
            continue
        if not cleaned or any(c in cleaned for c in "[]{}") or not line.endswith(("[p]", "[l]")) or "[r]" in line:
            reject("Unsupported markup or unconfirmed end of utterance", number)
            continue
        rows.append({"id": f"{script.as_posix()}:{source_line}", "audio": str(matches[0]),
            "text": cleaned, "language": language, "game": game, "scene": script.as_posix(),
            "order": order, "speaker": speaker, "source": f"{script.as_posix()}:{number}",
            "verified_original": False})
    if pending:
        reject("Voice at end of script without dialogue", pending[1])
    return rows, issues


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--scripts", type=Path, required=True)
    parser.add_argument("--audio", type=Path, required=True)
    parser.add_argument("--game", required=True)
    parser.add_argument("--language", choices=["ja", "en"], required=True)
    parser.add_argument("--voice-tag", default="voice")
    parser.add_argument("--encoding", default="utf-8-sig")
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    if not args.scripts.is_dir() or not args.audio.is_dir():
        parser.error("Readable script/audio directories are required")
    lookup = defaultdict(list)
    for path in sorted(args.audio.rglob("*")):
        if path.suffix.lower() not in (".wav", ".ogg", ".mp3", ".flac", ".opus"):
            continue
        relative = path.relative_to(args.audio).as_posix()
        for key in {relative.casefold(), str(Path(relative).with_suffix("")).replace("\\", "/").casefold(), path.name.casefold(), path.stem.casefold()}:
            lookup[key].append(path.resolve())
    rows, issues, files = [], [], 0
    for script in sorted(args.scripts.rglob("*.ks")):
        files += 1
        try:
            text = script.read_text(encoding=args.encoding)
        except (UnicodeError, OSError) as exc:
            issues.append({"script": str(script), "reason": str(exc)})
            continue
        found, rejected = candidates(script.relative_to(args.scripts), text, lookup, args.game, args.language, args.voice_tag)
        rows.extend(found)
        issues.extend(rejected)
    args.out.mkdir(parents=True, exist_ok=False)
    with (args.out / "candidates.jsonl").open("w", encoding="utf-8") as stream:
        for row in rows:
            stream.write(json.dumps(row, ensure_ascii=False) + "\n")
    report = {"script_files": files, "candidates": len(rows), "issues": issues,
        "status": "needs_original_text_and_pairing_review" if rows else "no_supported_pairs",
        "warning": "Static subset only. No XP3 extraction or engine macro execution. Review original language, full utterance boundaries, speaker and patch precedence before marking verified_original=true."}
    (args.out / "import-report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"script_files": files, "candidates": len(rows), "issues": len(issues)}))
    return int(not rows)


if __name__ == "__main__":
    raise SystemExit(main())
