"""Reproducible clip comparison and continuous-audio ASR replay (no reference prompts).

Clip CER is a development diagnostic on assistant-curated, selection-biased data.
Replay timing simulates one serialized recognizer; it does not measure translation,
live capture jitter, or GPU contention with a game. Sparse labels cannot give full CER.
"""
import argparse
import base64
from collections import Counter
import hashlib
import json
from pathlib import Path
import statistics
import sys
import time
import wave

import galgame_bench as bench


def percentile(values, fraction):
    return sorted(values)[min(len(values)-1, int((len(values)-1)*fraction))] if values else None


def queue_simulation(rows, backlog=3):
    """Replay measured service times using capture-time arrivals and DropOldest."""
    pending, completed, dropped = [], [], []
    finish = 0.
    running = None
    def start_next():
        nonlocal finish, running
        running = pending.pop(0)
        finish = max(finish, running['emitted_at_s']) + running['elapsed_s']
    for row in rows:
        arrival = row['emitted_at_s']
        while running is not None and finish <= arrival:
            completed.append(dict(id=running['id'], delay_s=finish-running['end_s']))
            running = None
            if pending: start_next()
        if running is None:
            pending.append(row); start_next()
        else:
            if len(pending) == backlog: dropped.append(pending.pop(0)['id'])
            pending.append(row)
    while running is not None:
        completed.append(dict(id=running['id'], delay_s=finish-running['end_s']))
        running = None
        if pending: start_next()
    return dict(dropped_ids=dropped, completed=len(completed),
                p50_delay_s=percentile([r['delay_s'] for r in completed], .5),
                p95_delay_s=percentile([r['delay_s'] for r in completed], .95))


def coverage(references, segments):
    items = []
    for ref in references:
        a, b = ref['source_start_s'], ref['source_end_s']
        intervals = sorted((max(a, r['start_s']), min(b, r['end_s'])) for r in segments
                           if r['end_s'] > a and r['start_s'] < b)
        cursor, covered = a, 0.
        for x, y in intervals:
            covered += max(0, y-max(cursor,x)); cursor = max(cursor,y)
        items.append(dict(id=ref['id'], coverage=covered/(b-a), pieces=len(intervals)))
    return dict(scope='coverage of annotated clip intervals, including padding; not speech recall',
                mean_coverage=statistics.mean(r['coverage'] for r in items),
                below_90_percent=[r for r in items if r['coverage'] < .9],
                split_across_segments=sum(r['pieces'] > 1 for r in items))


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--dataset', type=Path, required=True)
    p.add_argument('--root', type=Path, default=Path(__file__).resolve().parents[1])
    p.add_argument('--out', type=Path, required=True)
    p.add_argument('--suite', choices=['clips','replay'], default='clips')
    p.add_argument('--replay', type=Path)
    p.add_argument('--baseline-worker', type=Path, required=True)
    p.add_argument('--profiles', nargs='+', default=['baseline','adaptive','int8','small'])
    p.add_argument('--turn-model', type=Path)
    p.add_argument('--hotwords-file', type=Path)
    p.add_argument('--split', choices=['train','dev','test'])
    args = p.parse_args()
    args.out.mkdir(parents=True, exist_ok=False)
    refs = bench.read_rows(args.dataset)
    if len({r['id'] for r in refs}) != len(refs): raise ValueError('Duplicate IDs')
    root = args.root.resolve()
    report = dict(schema=1, suite=args.suite, dataset_sha256=bench.file_hash(args.dataset),
                  benchmark_sha256=bench.file_hash(Path(__file__)),
                  independent_evaluation=False, default_promotion_allowed=False,
                  scope='Single-recording development diagnostic; labels were selected with Whisper agreement.', profiles={})
    worker_path = root/'src/ACGTranslator.App/Resources/asr_worker.py'
    for profile in args.profiles:
        if profile not in ('baseline','baseline-context','aligned','adaptive','int8','small','speech','speech-adaptive','glossary','context','context-fast'):
            raise ValueError(f'Unknown profile {profile}')
        speech = profile.startswith('speech')
        source = args.replay/('speech' if speech else 'legacy')/'segments.jsonl' if args.suite == 'replay' else args.dataset
        source_rows = bench.read_rows(source)
        if args.split:
            source_rows = [r for r in source_rows if r.get('split')==args.split]
        if not source_rows: raise ValueError('Empty selected split')
        model = root/'models/asr'/('whisper-small' if profile=='small' else 'whisper-large-v3-turbo')
        script = args.baseline_worker if profile in ('baseline','baseline-context') else worker_path
        command = [sys.executable, str(script), '--model', str(model), '--device', 'cuda',
                   '--dll-dir', str(root/'models/runtimes/cuda12-cublas'), '--beam-size', '5']
        if profile in ('adaptive','speech-adaptive','context-fast'): command += ['--adaptive']
        if profile == 'int8': command += ['--compute-type','int8_float16']
        if speech: command += ['--presegmented']
        if profile in ('baseline-context','context','context-fast'): command += ['--vad-presence-only']
        if profile=='glossary':
            if not args.hotwords_file: raise ValueError('glossary requires --hotwords-file')
            command += ['--hotwords',args.hotwords_file.read_text(encoding='utf-8').strip()]
        if args.turn_model: command += ['--turn-model',str(args.turn_model)]
        worker = bench.Worker(command,args.out/(profile+'.stderr.log'),180)
        records = []
        try:
            # Same unscored warm-up sample for each profile.
            warm = bench.audio((source.parent/source_rows[0]['audio']).resolve())
            worker.exchange(dict(type='transcribe', sample_rate=16000,
                audio_base64=base64.b64encode(warm.tobytes()).decode('ascii'), language='ja', turn_enhancement=bool(args.turn_model)))
            with (args.out/(profile+'.jsonl')).open('x',encoding='utf-8') as out:
                for index,row in enumerate(source_rows):
                    samples = bench.audio((source.parent/row['audio']).resolve())
                    request = dict(type='transcribe', sample_rate=16000,
                        audio_base64=base64.b64encode(samples.tobytes()).decode('ascii'),
                        language=row.get('language','ja'), turn_enhancement=bool(args.turn_model))
                    started = time.perf_counter()
                    result = worker.exchange(request)
                    elapsed = time.perf_counter()-started
                    if result.get('error'): raise RuntimeError(result['error'])
                    separator = ' ' if row.get('language','ja') == 'en' else ''
                    hypothesis = separator.join(s['text'] for s in result.get('segments',[])) or result.get('text','')
                    record = dict(row, hypothesis=hypothesis, result=result, elapsed_s=elapsed,
                                  duration_s=len(samples)/16000)
                    if args.suite=='clips': record.update(bench.score(row['text'],hypothesis,row['language']))
                    records.append(record)
                    out.write(json.dumps(record,ensure_ascii=False)+'\n'); out.flush()
                    if index%40 == 0: print(json.dumps(dict(profile=profile, done=index+1,total=len(source_rows))),flush=True)
        finally: worker.close()
        times = [r['elapsed_s'] for r in records]
        summary = dict(samples=len(records), elapsed_s=sum(times), p50_s=percentile(times,.5), p95_s=percentile(times,.95),
            rtf=sum(times)/sum(r['duration_s'] for r in records),
            retries=sum(r['result'].get('decode',{}).get('retried',False) for r in records),
            empty=sum(not r['hypothesis'] for r in records),
            worker_sha256=bench.file_hash(script), model_sha256=bench.file_hash(model/'model.bin'),command=command,
            selected_split=args.split,hotwords_sha256=bench.file_hash(args.hotwords_file) if profile=='glossary' else None)
        if args.suite=='clips':
            summary['cer'] = sum(r['char_edits'] for r in records)/sum(r['ref_chars'] for r in records)
        else:
            summary['coverage'] = coverage(refs, records)
            summary['simulated_asr_queue'] = queue_simulation(records)
        summary['turn_status'] = dict(Counter(r['result'].get('turn_status','none') for r in records))
        summary['turn_strategy'] = dict(Counter(r['result'].get('turn',{}).get('strategy','none') for r in records))
        summary['turn_metrics'] = {
            key: sum(r['result'].get('turn',{}).get(key,0) for r in records)
            for key in ('extra_decodes','embedding_calls','embedding_s','detection_s','elapsed_s')} if any('turn' in r['result'] for r in records) else None
        summary['detector_sha256'] = bench.file_hash(script.with_name('turn_detector.py')) if args.turn_model else None
        report['profiles'][profile] = summary
        bench.write_json(args.out/'report.json',report)
        print(json.dumps(dict(profile=profile,summary=summary),ensure_ascii=True),flush=True)
    report['complete']=True
    bench.write_json(args.out/'report.json',report)


if __name__=='__main__': main()
