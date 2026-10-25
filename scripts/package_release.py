"""Package a clean, committed, self-contained Windows publish; never upload.

Run dotnet publish and scripts/verify_release_startup.ps1 first. Source ZIP is
git archive HEAD, so the source and build metadata name one exact revision.
"""
from pathlib import Path
import datetime
import hashlib
import json
import shutil
import subprocess
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
VERSION = ET.parse(ROOT / 'src/ACGTranslator.App/ACGTranslator.App.csproj').findtext('.//Version')
OUT = ROOT / 'artifacts' / f'acgtranslator-v{VERSION}'
PACKAGE = OUT / f'ACGTranslator-v{VERSION}-win-x64'

def digest(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            value.update(chunk)
    return value.hexdigest()

def git(*args):
    return subprocess.check_output(['git', '-c', f'safe.directory={ROOT.as_posix()}', *args], cwd=ROOT)

def main():
    if git('status', '--porcelain').strip():
        raise RuntimeError('Commit/review all source changes before packaging; source ZIP must match HEAD.')
    required = ['ACGTranslator.App.exe', 'ACGTranslator.App.dll', 'System.Private.CoreLib.dll',
                'Resources/asr_worker.py', 'Resources/turn_detector.py', 'Resources/worker_protocol.py',
                'Resources/translate_worker.py', 'Resources/ocr_worker.ps1',
                'Resources/autotune.py', 'Resources/galgame_bench.py', 'Resources/model-catalog.json']
    for name in required:
        if not (PACKAGE/name).is_file(): raise FileNotFoundError(f'Missing self-contained publish file: {name}')
    validation = ROOT / f'docs/v{VERSION}-validation.md'
    startup = OUT/'UI-STARTUP.json'
    check = json.loads(startup.read_text(encoding='utf-8-sig'))
    if not check.get('ok') or check.get('version') != VERSION:
        raise RuntimeError('Verify final package startup first.')
    for source in (ROOT/'src/ACGTranslator.App/Resources').iterdir():
        if source.suffix in ('.py', '.ps1', '.json'):
            assert digest(source) == digest(PACKAGE/'Resources'/source.name), f'Stale resource: {source.name}'
    for name in ('autotune.py','galgame_bench.py'):
        assert digest(ROOT/'scripts'/name) == digest(PACKAGE/'Resources'/name), f'Stale linked script: {name}'
    assert digest(PACKAGE/'ACGTranslator.App.dll') == check['assemblySha256'], 'Startup checked a different assembly'
    info = dict(product='ACGTranslator', version=VERSION, platform='win-x64', configuration='Release',
                selfContained=True, targetFramework='net8.0-windows', sourceCommit=git('rev-parse','HEAD').decode().strip(),
                sourceIncludesUncommittedChanges=False,
                packagedAtUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                publishCommand=f'dotnet publish src/ACGTranslator.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o artifacts/acgtranslator-v{VERSION}/ACGTranslator-v{VERSION}-win-x64')
    (PACKAGE/'BUILD-INFO.json').write_text(json.dumps(info,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    for name in ('Setup.cmd','Setup.ps1','Start.cmd','README-Release.md','THIRD-PARTY.md',f'RELEASE-NOTES-v{VERSION}.md'):
        shutil.copy2(ROOT/'release-tools'/name, PACKAGE/name)
    for name in ('requirements-asr.txt','CHANGELOG.md'):
        shutil.copy2(ROOT/name,PACKAGE/name)
    (PACKAGE/'scripts').mkdir(exist_ok=True)
    for name in ('prepare_models.py','smoke_test.py','smoke_turns.py','capture_dataset.py'):
        shutil.copy2(ROOT/'scripts'/name,PACKAGE/'scripts'/name)
    shutil.copytree(ROOT/'docs',PACKAGE/'docs',dirs_exist_ok=True)
    shutil.copy2(validation,OUT/'VALIDATION.md')
    shutil.copy2(ROOT/'release-tools'/f'RELEASE-NOTES-v{VERSION}.md',OUT/f'RELEASE-NOTES-v{VERSION}.md')
    binary = OUT/f'ACGTranslator-v{VERSION}-win-x64.zip'
    source_zip = OUT/f'ACGTranslator-v{VERSION}-source.zip'
    if binary.exists() or source_zip.exists(): raise FileExistsError('Release ZIP already exists; do not silently overwrite deliverables.')
    with zipfile.ZipFile(binary,'x',zipfile.ZIP_DEFLATED,compresslevel=6) as archive:
        for path in sorted(PACKAGE.rglob('*')):
            if path.is_file(): archive.write(path,Path(PACKAGE.name)/path.relative_to(PACKAGE))
    git('archive','--format=zip',f'--prefix=ACGTranslator-v{VERSION}-source/',f'--output={source_zip}','HEAD')
    for path in (binary,source_zip):
        with zipfile.ZipFile(path) as archive:
            assert archive.testzip() is None, f'Corrupt ZIP: {path.name}'
            for name in archive.namelist():
                parts = set(Path(name).parts)
                assert not (parts & {'.git','.venv','.venv-training','models','data','artifacts','__pycache__'}), name
                assert not name.endswith(('history.jsonl','settings.json','manifest-six.jsonl','.wav','.png')), name
    with zipfile.ZipFile(binary) as archive:
        for name in required + ['scripts/capture_dataset.py','Setup.cmd',f'docs/v{VERSION}-validation.md']:
            assert PACKAGE.name+'/'+name in archive.namelist(), name
    deliverables = [binary,source_zip,OUT/'VALIDATION.md',OUT/f'RELEASE-NOTES-v{VERSION}.md',startup]
    (OUT/'SHA256SUMS.txt').write_text('\n'.join(digest(path)+'  '+path.name for path in deliverables)+'\n',encoding='utf-8')
    print(json.dumps([dict(name=p.name,bytes=p.stat().st_size,sha256=digest(p)) for p in deliverables],ensure_ascii=True))

if __name__ == '__main__': main()
