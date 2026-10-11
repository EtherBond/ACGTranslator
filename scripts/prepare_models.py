"""Explicitly provision local ASR and translation models.

Run this script while online. Runtime workers never download models.
"""
import os
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MODELS = ROOT / "models"
os.environ["HF_HOME"] = str(ROOT / ".cache" / "huggingface")
os.environ["HF_HUB_DISABLE_XET"] = "1"


def prepare_asr():
    from huggingface_hub import snapshot_download
    destination = MODELS / "asr" / "whisper-base"
    print("Preparing Whisper base model...", flush=True)
    snapshot_download(
        "Systran/faster-whisper-base",
        local_dir=destination,
        allow_patterns=["config.json", "model.bin", "tokenizer.json", "vocabulary.*", "preprocessor_config.json"],
    )


def prepare_translation():
    packages_dir = MODELS / "translators" / "argos"
    packages_dir.mkdir(parents=True, exist_ok=True)
    # Install the packages beside the application so the runtime can use the
    # same directory without depending on a user's global Argos installation.
    os.environ["ARGOS_TRANSLATE_PACKAGE_DIR"] = str(packages_dir)
    import argostranslate.package
    argostranslate.package.update_package_index()
    available = argostranslate.package.get_available_packages()
    for source, target in (("en", "zh"), ("ja", "en")):
        package = next((item for item in available if item.from_code == source and item.to_code == target), None)
        if package is None:
            raise RuntimeError(f"No Argos package found for {source}->{target}")
        archive = packages_dir / f"translate-{source}_{target}.argosmodel"
        if not archive.exists():
            print(f"Downloading {source}->{target} translation package...", flush=True)
            downloaded = Path(package.download())
            shutil.copyfile(downloaded, archive)
        argostranslate.package.install_from_path(str(archive))


def main():
    MODELS.mkdir(parents=True, exist_ok=True)
    prepare_asr()
    prepare_translation()
    print("Local models are ready. Runtime can now operate offline.", flush=True)


if __name__ == "__main__":
    main()
