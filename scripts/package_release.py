"""Package the already-published v1.0.1 build and exact working-tree source.

Run after dotnet publish to artifacts/acgtranslator-v1.0.1/ACGTranslator-v1.0.1-win-x64.
Does not upload anything. Model weights, Python environment and user data are excluded.
"""
from pathlib import Path
import datetime, hashlib, json, shutil, subprocess, zipfile

ROOT = Path(__file__).resolve().parents[1]
VERSION = "1.0.1"
OUT = ROOT / "artifacts" / f"acgtranslator-v{VERSION}"
PACKAGE = OUT / f"ACGTranslator-v{VERSION}-win-x64"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def git(*args):
    return subprocess.check_output(["git", "-c", f"safe.directory={ROOT.as_posix()}", *args], cwd=ROOT)


def main():
    assert (PACKAGE / "ACGTranslator.App.exe").is_file(), "Run dotnet publish first"
    assert (ROOT / "docs/v1.0.1-validation.md").is_file()
    assert (OUT / "UI-STARTUP.txt").is_file(), "Verify final build startup first"
    for source in (ROOT / "src/ACGTranslator.App/Resources").iterdir():
        if source.suffix in (".py", ".json"):
            assert digest(source) == digest(PACKAGE / "Resources" / source.name), f"Stale published resource: {source}"
    assert digest(PACKAGE / "ACGTranslator.App.dll") == digest(ROOT / "src/ACGTranslator.App/bin/Release/net8.0-windows/win-x64/ACGTranslator.App.dll")
    info = {
        "product": "ACGTranslator", "version": VERSION, "channel": "experimental",
        "platform": "win-x64", "configuration": "Release", "selfContained": True,
        "targetFramework": "net8.0-windows",
        "sourceBaseCommit": git("rev-parse", "HEAD").decode().strip(),
        "sourceIncludesUncommittedChanges": True,
        "packagedAtUtc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "publishCommand": "dotnet publish src/ACGTranslator.App/ACGTranslator.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false -o artifacts/acgtranslator-v1.0.1/ACGTranslator-v1.0.1-win-x64"
    }
    (ROOT / "BUILD-INFO.json").write_text(json.dumps(info, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    for name in ["Setup.cmd", "Setup.ps1", "Start.cmd", "README-Release.md", "THIRD-PARTY.md", f"RELEASE-NOTES-v{VERSION}.md"]:
        shutil.copy2(ROOT / "release-tools" / name, PACKAGE / name)
    for name in ["requirements-asr.txt", "BUILD-INFO.json", "CHANGELOG.md"]:
        shutil.copy2(ROOT / name, PACKAGE / name)
    (PACKAGE / "scripts").mkdir(exist_ok=True)
    for name in ["prepare_models.py", "smoke_test.py", "smoke_turns.py"]:
        shutil.copy2(ROOT / "scripts" / name, PACKAGE / "scripts" / name)
    shutil.copytree(ROOT / "docs", PACKAGE / "docs", dirs_exist_ok=True)
    shutil.copy2(ROOT / "docs/v1.0.1-validation.md", OUT / "VALIDATION.md")
    shutil.copy2(ROOT / f"release-tools/RELEASE-NOTES-v{VERSION}.md", OUT / f"RELEASE-NOTES-v{VERSION}.md")
    binary = OUT / f"ACGTranslator-v{VERSION}-win-x64.zip"
    with zipfile.ZipFile(binary, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(PACKAGE.rglob("*")):
            if path.is_file():
                archive.write(path, Path(PACKAGE.name) / path.relative_to(PACKAGE))
    paths = sorted(set(git("ls-files", "-z", "--cached", "--others", "--exclude-standard").decode("utf-8").split("\0")) - {""})
    source_zip = OUT / f"ACGTranslator-v{VERSION}-source.zip"
    with zipfile.ZipFile(source_zip, "w", zipfile.ZIP_DEFLATED) as archive:
        for name in paths:
            path = ROOT / name
            if path.is_file():
                archive.write(path, Path(f"ACGTranslator-v{VERSION}-source") / name)
    for path in [binary, source_zip]:
        with zipfile.ZipFile(path) as archive:
            assert archive.testzip() is None
            names = archive.namelist()
            assert not any("/.venv/" in n or "/models/" in n or "/.git/" in n or n.endswith(("history.jsonl", "settings.json")) for n in names)
    with zipfile.ZipFile(binary) as archive:
        for required in ["ACGTranslator.App.exe", "Resources/turn_detector.py", "Setup.cmd", "requirements-asr.txt", "docs/v1.0.1-validation.md"]:
            assert PACKAGE.name + "/" + required in archive.namelist()
    deliverables = [binary, source_zip, OUT / "VALIDATION.md", OUT / f"RELEASE-NOTES-v{VERSION}.md", OUT / "UI-STARTUP.txt"]
    (OUT / "SHA256SUMS.txt").write_text("\n".join(digest(p)+"  "+p.name for p in deliverables)+"\n", encoding="utf-8")
    print(json.dumps([{"name":p.name,"bytes":p.stat().st_size} for p in deliverables], ensure_ascii=False))


if __name__ == "__main__":
    main()
