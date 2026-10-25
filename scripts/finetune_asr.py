"""Local Whisper decoder LoRA experiment. Never changes the application's model/settings.

prepare: validate immutable audio, split by recording; exploratory single-recording
mode uses chronological regions + an embargo and cannot pass the deployment gate.
train: frozen encoder cache, decoder q/v LoRA, dev CER early stopping, final test once.
"""
import argparse
from collections import Counter
import hashlib
import importlib.metadata
import json
from pathlib import Path
import random
import sys
import time
import wave

import galgame_bench as bench


def pcm(path):
    with wave.open(str(path)) as w:
        if (w.getnchannels(),w.getsampwidth(),w.getframerate(),w.getcomptype()) != (1,2,16000,'NONE'):
            raise ValueError(f'Expected mono 16 kHz PCM16: {path}')
        return w.readframes(w.getnframes())


def prepare(args):
    rows = bench.read_rows(args.dataset)
    seen = set()
    for r in rows:
        if r['id'] in seen or r.get('verified_original') is not True or not r['text'].strip():
            raise ValueError('Duplicate ID, unverified pairing or empty reference')
        if r['language'] not in ('ja','en'): raise ValueError('Unsupported language')
        seen.add(r['id'])
        path=(args.dataset.parent/r['audio']).resolve()
        raw=pcm(path)
        if hashlib.sha256(raw).hexdigest()!=r['pcm_sha256']: raise ValueError(f'Audio hash mismatch: {r["id"]}')
        if not 0 < len(raw)/32000 <= 30: raise ValueError('Training clips must be 0-30 seconds')
        r['audio']=str(path)
    groups=sorted({r['recording_group'] for r in rows})
    independent=len(groups)>=3
    if not independent and not args.exploratory:
        raise ValueError('Need at least three recording groups, or explicitly use --exploratory (no deployment claims).')
    if independent:
        random.Random(42).shuffle(groups)
        n=max(1,len(groups)//5)
        for r in rows:
            index=groups.index(r['recording_group'])
            r['split']='test' if index<n else 'dev' if index<2*n else 'train'
    else:
        if len(groups)!=1: raise ValueError('Exploratory chronological split requires exactly one recording')
        ordered=sorted(rows,key=lambda r:r['source_start_s'])
        cut1=ordered[int(len(rows)*.65)]['source_start_s']
        cut2=ordered[int(len(rows)*.82)]['source_start_s']
        for r in rows:
            a,b=r['source_start_s'],r['source_end_s']
            r['split']='train' if b<cut1-15 else 'dev' if a>cut1+15 and b<cut2-15 else 'test' if a>cut2+15 else 'embargo'
    # Remove cross-split duplicates from ALL affected splits, including repeated
    # phrases. Never silently let a held-out exact transcript into training.
    keys={}
    for r in rows:
        if r['split']=='embargo': continue
        for key in [('audio',r['pcm_sha256']),('text',r['language'],bench.normalize(r['text'],r['language']))]:
            keys.setdefault(key,set()).add(r['split'])
    for r in rows:
        if any(len(keys.get(key,set()))>1 for key in [('audio',r['pcm_sha256']),('text',r['language'],bench.normalize(r['text'],r['language']))]):
            r['split']='duplicate_excluded'
    counts=Counter(r['split'] for r in rows)
    if min(counts[s] for s in ('train','dev','test'))<3: raise ValueError(f'Insufficient split sizes after exclusion: {counts}')
    args.out.mkdir(parents=True,exist_ok=False)
    bench.write_manifest(args.out/'manifest.jsonl', rows)
    report=dict(source_sha256=bench.file_hash(args.dataset), independent_recordings=len(groups),
                independent_evaluation=independent, exploratory=not independent, counts=dict(counts),
                split_policy='whole recording groups' if independent else 'chronological 65/17/18 with 15s embargo on both sides',
                label_scope='assistant visual labels with Whisper crosschecks, not human-audited gold',
                deployment_allowed=False, manifest_sha256=bench.file_hash(args.out/'manifest.jsonl'))
    bench.write_json(args.out/'split.json',report)
    print(json.dumps(report),flush=True)


def train(args):
    import os
    os.environ['HF_HUB_OFFLINE']='1'
    os.environ['TRANSFORMERS_OFFLINE']='1'
    import numpy as np
    import torch
    from transformers import WhisperForConditionalGeneration, WhisperProcessor
    from transformers.modeling_outputs import BaseModelOutput
    from peft import LoraConfig, get_peft_model, set_peft_model_state_dict
    from safetensors.torch import load_file
    torch.manual_seed(42); random.seed(42); np.random.seed(42)
    if not torch.cuda.is_available(): raise RuntimeError('CUDA training environment required; refusing accidental CPU training')
    split=json.loads((args.data/'split.json').read_text(encoding='utf-8'))
    manifest=args.data/'manifest.jsonl'
    if bench.file_hash(manifest)!=split['manifest_sha256']: raise ValueError('Split manifest was modified')
    rows=bench.read_rows(manifest)
    parts={s:[r for r in rows if r['split']==s] for s in ['train','dev','test']}
    args.out.mkdir(parents=True,exist_ok=False)
    processor=WhisperProcessor.from_pretrained(str(args.model),local_files_only=True)
    base=WhisperForConditionalGeneration.from_pretrained(str(args.model),local_files_only=True,attn_implementation='sdpa').cuda()
    base.config.use_cache=False
    base.generation_config.forced_decoder_ids=None
    config=LoraConfig(r=args.rank,lora_alpha=args.rank*2,lora_dropout=.05,bias='none',
        target_modules=r'model\.decoder\.layers\.\d+\.(self_attn|encoder_attn)\.(q_proj|v_proj)')
    model=get_peft_model(base,config)
    model.print_trainable_parameters()
    for name,p in model.named_parameters():
        if p.requires_grad and ('encoder.' in name or 'lora_' not in name): raise RuntimeError(f'Unexpected trainable parameter {name}')
    dtype=torch.bfloat16 if torch.cuda.is_bf16_supported() else torch.float16
    # Encoder is frozen and input is unchanged in this first experiment. Cache
    # features once; augmentation would require separate encoder caches.
    cache={}
    model.eval()
    selected=parts['train']+parts['dev']+parts['test']
    for i,r in enumerate(selected):
        raw=pcm(r['audio'])
        if hashlib.sha256(raw).hexdigest()!=r['pcm_sha256']: raise ValueError('Audio changed after splitting')
        x=np.frombuffer(raw,dtype='<i2').astype(np.float32)/32768
        extracted=processor.feature_extractor(x,sampling_rate=16000,return_tensors='pt',return_attention_mask=True)
        features=extracted.input_features.cuda()
        with torch.no_grad(),torch.autocast('cuda',dtype=dtype):
            hidden=base.model.encoder(features).last_hidden_state.cpu()
        processor.tokenizer.set_prefix_tokens(language=r['language'],task='transcribe',predict_timestamps=False)
        labels=processor.tokenizer(r['text'],return_tensors='pt').input_ids[0]
        if labels[0].item()==base.config.decoder_start_token_id: labels=labels[1:]
        cache[r['id']]=(hidden,labels,extracted.attention_mask)
        if i%20==0: print(json.dumps(dict(stage='cache',done=i+1,total=len(selected))),flush=True)

    def evaluate(which):
        model.eval(); results=[]
        started=time.perf_counter()
        for r in parts[which]:
            hidden,_,mask=cache[r['id']]
            with torch.no_grad(),torch.autocast('cuda',dtype=dtype):
                ids=model.generate(encoder_outputs=BaseModelOutput(last_hidden_state=hidden.cuda()),attention_mask=mask.cuda(),
                    language=r['language'],task='transcribe',num_beams=2,max_new_tokens=128,
                    do_sample=False,return_timestamps=False,use_cache=True)
            text=processor.batch_decode(ids,skip_special_tokens=True)[0]
            results.append(dict(id=r['id'],reference=r['text'],hypothesis=text,**bench.score(r['text'],text,r['language'])))
        return dict(cer=sum(r['char_edits'] for r in results)/sum(r['ref_chars'] for r in results),
                    samples=len(results),seconds=time.perf_counter()-started,rows=results)

    baseline=evaluate('dev')
    bench.write_json(args.out/'baseline-dev.json',baseline)
    print(json.dumps(dict(stage='baseline',dev_cer=baseline['cer'])),flush=True)
    optimizer=torch.optim.AdamW([p for p in model.parameters() if p.requires_grad],lr=args.lr,weight_decay=.01)
    scaler=torch.amp.GradScaler('cuda',enabled=dtype==torch.float16)
    best=baseline['cer']; best_epoch=0; history=[]; stale=0
    start=time.perf_counter()
    for epoch in range(1,args.epochs+1):
        model.train(); base.model.encoder.eval()
        order=list(parts['train']); random.Random(42+epoch).shuffle(order)
        total_loss=0.
        for k in range(0,len(order),args.accumulation):
            group=order[k:k+args.accumulation]
            optimizer.zero_grad(set_to_none=True)
            for r in group:
                hidden,labels,mask=cache[r['id']]
                with torch.autocast('cuda',dtype=dtype):
                    result=model(encoder_outputs=BaseModelOutput(last_hidden_state=hidden.cuda()),attention_mask=mask.cuda(),labels=labels[None].cuda(),use_cache=False)
                    loss=result.loss/len(group)
                if not torch.isfinite(loss): raise RuntimeError('Non-finite training loss')
                total_loss+=result.loss.item()
                scaler.scale(loss).backward()
            scaler.unscale_(optimizer)
            torch.nn.utils.clip_grad_norm_([p for p in model.parameters() if p.requires_grad],1.)
            scaler.step(optimizer); scaler.update()
        dev=evaluate('dev')
        bench.write_json(args.out/f'epoch-{epoch}-dev.json',dev)
        item=dict(epoch=epoch,train_loss=total_loss/len(order),dev_cer=dev['cer'])
        history.append(item); print(json.dumps(item),flush=True)
        if dev['cer']<best:
            best=dev['cer']; best_epoch=epoch; stale=0
            model.save_pretrained(str(args.out/'best-adapter'),safe_serialization=True)
        else:
            stale+=1
            if stale>=2: break
    model.save_pretrained(str(args.out/'last-adapter'),safe_serialization=True)
    # Test is run only after model selection on development data has finished.
    if best_epoch:
        set_peft_model_state_dict(model,load_file(str(args.out/'best-adapter/adapter_model.safetensors')))
        test=evaluate('test'); bench.write_json(args.out/'candidate-test.json',test)
    else: test=None
    with model.disable_adapter(): baseline_test=evaluate('test')
    bench.write_json(args.out/'baseline-test.json',baseline_test)
    merged=None
    if best_epoch:
        merged=args.out/'merged'
        merged_model=model.merge_and_unload()
        merged_model.save_pretrained(str(merged),safe_serialization=True)
        processor.save_pretrained(str(merged))
    report=dict(complete=True,split=split,base_model=str(args.model.resolve()),
        base_sha256=bench.file_hash(args.model/'model.safetensors'),rank=args.rank,lr=args.lr,
        training_script_sha256=bench.file_hash(Path(__file__)),gpu=torch.cuda.get_device_name(),
        seed=42,encoder_cached=True,augmentation=False,history=history,best_epoch=best_epoch,
        baseline_dev_cer=baseline['cer'],best_dev_cer=best,baseline_test_cer=baseline_test['cer'],
        candidate_test_cer=test['cer'] if test else None,merged_model=str(merged) if merged else None,
        training_seconds=time.perf_counter()-start,peak_gpu_allocated_bytes=torch.cuda.max_memory_allocated(),
        deployment_allowed=False,reason='Independent recordings and English regression evaluation still required',
        versions={n:importlib.metadata.version(n) for n in ['torch','transformers','peft','accelerate']})
    bench.write_json(args.out/'report.json',report)
    print(json.dumps({k:v for k,v in report.items() if k!='history'}),flush=True)


def main():
    p=argparse.ArgumentParser(description=__doc__)
    sub=p.add_subparsers(dest='command',required=True)
    prep=sub.add_parser('prepare'); prep.add_argument('--dataset',type=Path,required=True); prep.add_argument('--out',type=Path,required=True)
    prep.add_argument('--exploratory',action='store_true')
    tr=sub.add_parser('train'); tr.add_argument('--data',type=Path,required=True); tr.add_argument('--model',type=Path,required=True); tr.add_argument('--out',type=Path,required=True)
    tr.add_argument('--rank',type=int,default=4); tr.add_argument('--lr',type=float,default=1e-4); tr.add_argument('--epochs',type=int,default=3); tr.add_argument('--accumulation',type=int,default=8)
    args=p.parse_args()
    if args.command=='prepare': prepare(args)
    else:
        if not 1<=args.rank<=32 or not 1<=args.epochs<=10 or not 1<=args.accumulation<=64: p.error('Invalid experimental budget')
        train(args)


if __name__=='__main__': main()
