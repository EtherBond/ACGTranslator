"""Export assistant-reviewed capture evidence as ASR training data, never tune settings.

The assistant writes explicit decisions after checking images and audio. This tool
validates immutable evidence and cuts reviewed ranges; it does not invent labels.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import shutil
import wave


def digest(path):
    value = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            value.update(chunk)
    return value.hexdigest()


def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def read_rows(path):
    return [json.loads(line) for line in Path(path).read_text(encoding='utf-8-sig').splitlines() if line.strip()]


def write_rows(path, rows):
    with Path(path).open('x', encoding='utf-8') as stream:
        for row in rows:
            stream.write(json.dumps(row, ensure_ascii=False) + '\n')


def safe(root, relative):
    if not isinstance(relative, str) or not relative or Path(relative).is_absolute():
        raise ValueError('Expected a relative evidence path')
    root = Path(root).resolve()
    path = (root / relative).resolve()
    if not path.is_relative_to(root) or path == root:
        raise ValueError('Evidence path escapes bundle')
    return path


def load_bundle(folder):
    folder = Path(folder).resolve()
    bundle = read_json(folder / 'bundle.json')
    if bundle.get('schema') != 'acg.capture-handoff.v1':
        raise ValueError('Unsupported/incomplete capture bundle')
    for name, expected in bundle['files'].items():
        path = safe(folder, name)
        if not path.is_file() or digest(path) != expected:
            raise ValueError(f'Evidence missing or changed: {name}')
    if not {'session.json', 'candidates.jsonl', 'capture.wav'} <= bundle['files'].keys():
        raise ValueError('Bundle lacks required evidence')
    rows = read_rows(folder / 'candidates.jsonl')
    if len(rows) != bundle['candidate_count'] or len({r['id'] for r in rows}) != len(rows):
        raise ValueError('Candidate count or IDs are invalid')
    with wave.open(str(folder / 'capture.wav'), 'rb') as audio:
        if (audio.getframerate(), audio.getnchannels(), audio.getsampwidth(), audio.getcomptype()) != (16000, 1, 2, 'NONE'):
            raise ValueError('Expected 16 kHz mono PCM16 source recording')
        frames = audio.getnframes()
        if len(audio.readframes(frames)) != frames * 2:
            raise ValueError('Source recording is truncated')
    return bundle, rows, frames / 16000


def export(folder, decisions_path, output):
    folder, output = Path(folder).resolve(), Path(output).resolve()
    if output == folder or output.is_relative_to(folder) or folder.is_relative_to(output):
        raise ValueError('Output must be separate from the evidence bundle')
    bundle, rows, duration = load_bundle(folder)
    source_hash = digest(folder / 'bundle.json')
    patches = read_rows(decisions_path)
    indexed = {r['id']: r for r in rows}
    decisions = {}
    for patch in patches:
        key = patch['id']
        if key not in indexed or key in decisions:
            raise ValueError('Unknown or duplicate decision ID')
        if patch.get('bundle_sha256') != source_hash:
            raise ValueError('Decision refers to a different bundle snapshot')
        if patch.get('status') not in ('accept', 'pending', 'exclude'):
            raise ValueError('Invalid decision status')
        if not str(patch.get('reason', '')).strip():
            raise ValueError('Each decision requires a reason')
        if patch['status'] == 'accept':
            if patch.get('text_verified') is not True or patch.get('audio_verified') is not True:
                raise ValueError('Accepted samples require explicit text and audio verification')
            if not patch.get('reviewer') or not patch.get('verification_method'):
                raise ValueError('Accepted samples require reviewer and verification method')
            if not isinstance(patch.get('text'), str) or not patch['text'].strip():
                raise ValueError('Accepted sample needs corrected original text')
            if not patch.get('text_evidence') or not patch.get('audio_evidence'):
                raise ValueError('Accepted sample needs image and audio review evidence')
            for screenshot in patch['text_evidence']:
                if screenshot not in bundle['files'] or not safe(folder, screenshot).is_file():
                    raise ValueError('Text evidence must be an existing hashed bundle file')
                if Path(screenshot).suffix.lower() != '.png':
                    raise ValueError('Text evidence must reference screenshots')
            start, end = float(patch['start_s']), float(patch['end_s'])
            if not all(math.isfinite(n) for n in (start, end)) or start < 0 or end > duration or not .25 <= end - start <= 120:
                raise ValueError('Invalid reviewed audio range')
            patch['start_s'], patch['end_s'] = start, end
        decisions[key] = patch
    # Validate everything before creating an output; never overwrite an earlier export.
    output.mkdir(parents=True, exist_ok=False)
    clips = output / 'audio'; clips.mkdir()
    train, pending, excluded = [], [], []
    with wave.open(str(folder / 'capture.wav'), 'rb') as source:
        for row in rows:
            patch = decisions.get(row['id'], {'id': row['id'], 'status': 'pending', 'reason': 'Not yet reviewed by assistant'})
            if patch['status'] != 'accept':
                (excluded if patch['status'] == 'exclude' else pending).append({'candidate': row, 'decision': patch})
                continue
            first, last = round(patch['start_s'] * 16000), round(patch['end_s'] * 16000)
            source.setpos(first); pcm = source.readframes(last - first)
            if len(pcm) != (last - first) * 2 or not any(pcm):
                raise ValueError('Accepted audio is truncated or entirely silent; export incomplete')
            relative = f'audio/{len(train) + 1:06d}.wav'
            with wave.open(str(output / relative), 'wb') as target:
                target.setparams((1, 2, 16000, 0, 'NONE', 'not compressed')); target.writeframes(pcm)
            train.append(dict(id=row['id'], audio=relative, text=patch['text'].strip(), language='ja',
                duration_s=(last-first)/16000, game=row['game'], recording_group=bundle['session_id'],
                source_start_s=first/16000, source_end_s=last/16000, source_bundle_sha256=source_hash,
                original_ocr=row['original_ocr'], translation_zh=patch.get('translation_zh', ''),
                translation_verified=patch.get('translation_verified', False),
                speaker=patch.get('speaker', ''), pcm_sha256=hashlib.sha256(pcm).hexdigest(),
                reviewer=patch['reviewer'], verification_method=patch['verification_method'],
                audio_evidence=patch['audio_evidence'], text_evidence=patch['text_evidence'], verified_original=True))
    write_rows(output / 'train.jsonl', train); write_rows(output / 'pending.jsonl', pending); write_rows(output / 'excluded.jsonl', excluded)
    shutil.copyfile(decisions_path, output / 'decisions.jsonl')
    summary = dict(schema='acg.asr-dataset.v1', accepted=len(train), pending=len(pending), excluded=len(excluded),
        duration_s=sum(r['duration_s'] for r in train), bundle=str(folder), bundle_sha256=source_hash,
        decisions_sha256=digest(decisions_path), script_sha256=digest(__file__),
        split='Unassigned; keep this recording together when making independent evaluation sets',
        complete=True)
    (output / 'summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding='utf-8')
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    for name in ('check', 'draft', 'export'):
        command = commands.add_parser(name); command.add_argument('--bundle', type=Path, required=True)
        if name in ('draft', 'export'): command.add_argument('--out', type=Path, required=True)
        if name == 'export': command.add_argument('--decisions', type=Path, required=True)
    args = parser.parse_args()
    if args.command == 'export':
        result = export(args.bundle, args.decisions, args.out)
    else:
        bundle, rows, duration = load_bundle(args.bundle)
        result = dict(candidates=len(rows), duration_s=duration, missing_evidence=bundle.get('missing_files', []))
        if args.command == 'draft':
            stamp = digest(args.bundle / 'bundle.json')
            write_rows(args.out, [dict(id=r['id'], bundle_sha256=stamp, status='pending', reason='Awaiting assistant review') for r in rows])
    print(json.dumps(result, ensure_ascii=True, indent=2))


if __name__ == '__main__':
    main()
