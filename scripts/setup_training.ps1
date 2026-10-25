param([string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
$python = Join-Path $ProjectRoot '.venv-training\Scripts\python.exe'
$runtimePython = Join-Path $ProjectRoot '.venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $python)) {
    & $runtimePython -m venv (Join-Path $ProjectRoot '.venv-training')
    if ($LASTEXITCODE -ne 0) { throw 'Training environment creation failed' }
}
$cache = Join-Path $ProjectRoot '.cache\pip'
& $python -m pip install --cache-dir $cache torch==2.9.1 --index-url https://download.pytorch.org/whl/cu128
if ($LASTEXITCODE -ne 0) { throw 'CUDA PyTorch installation failed' }
& $python -m pip install --cache-dir $cache -r (Join-Path $ProjectRoot 'requirements-training.txt')
if ($LASTEXITCODE -ne 0) { throw 'Training dependencies installation failed' }
& $python -c "import torch; assert torch.cuda.is_available(), 'CUDA unavailable'; print(torch.__version__, torch.cuda.get_device_name(0))"
if ($LASTEXITCODE -ne 0) { throw 'CUDA check failed' }
