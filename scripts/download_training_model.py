"""Explicit download step; training itself is strictly local/offline."""
import argparse
import os
from pathlib import Path

REVISIONS = {
    'whisper-small': '973afd24965f72e36ca33b3055d56a652f456b4d',
    'whisper-large-v3-turbo': '41f01f3fe87f28c78e2fbf8b568835947dd65ed9',
}
def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('model',choices=REVISIONS)
    p.add_argument('--root',type=Path,default=Path(__file__).resolve().parents[1])
    args=p.parse_args()
    os.environ['HF_HOME']=str(args.root/'.cache/huggingface')
    os.environ['HF_HUB_DISABLE_XET']='1'
    from huggingface_hub import snapshot_download
    snapshot_download('openai/'+args.model,revision=REVISIONS[args.model],
        local_dir=str(args.root/'models/training'/args.model),max_workers=2,
        allow_patterns=['*.json','*.txt','*.tiktoken','model.safetensors'])

if __name__=='__main__': main()
