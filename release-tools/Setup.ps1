param([switch]$SkipModels)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'
$python = Join-Path $PSScriptRoot '.venv\Scripts\python.exe'
function Invoke-Checked {
    param([string]$Executable, [string[]]$Arguments)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed (exit $LASTEXITCODE): $Executable $Arguments" }
}
try {
    if (!(Test-Path -LiteralPath $python)) {
        $basePython = $null
        $check = 'import sys,struct; assert (3,10)<=sys.version_info[:2]<=(3,13) and struct.calcsize(chr(80))==8; print(sys.executable)'
        $launcher = Get-Command py.exe -ErrorAction SilentlyContinue
        if ($launcher) {
            foreach ($version in @('-3.12','-3.11','-3.13','-3.10')) {
                try {
                    $candidate = & $launcher.Source $version -c $check 2>$null
                    if ($LASTEXITCODE -eq 0 -and $candidate) { $basePython = [string]($candidate | Select-Object -Last 1); break }
                } catch { }
            }
        }
        if (!$basePython) {
            $command = Get-Command python.exe -ErrorAction SilentlyContinue
            if ($command) {
                try {
                    $candidate = & $command.Source -c $check 2>$null
                    if ($LASTEXITCODE -eq 0 -and $candidate) { $basePython = [string]($candidate | Select-Object -Last 1) }
                } catch { }
            }
        }
        if (!$basePython) { throw 'Install 64-bit Python 3.10-3.13 (3.12 recommended) from https://www.python.org/downloads/windows/ with the Python launcher, then run Setup.cmd again.' }
        Invoke-Checked $basePython @('-m','venv',(Join-Path $PSScriptRoot '.venv'))
    }
    Invoke-Checked $python @('-m','pip','install','-r',(Join-Path $PSScriptRoot 'requirements-asr.txt'))
    if (!$SkipModels) {
        Invoke-Checked $python @((Join-Path $PSScriptRoot 'scripts\prepare_models.py'))
        Invoke-Checked $python @((Join-Path $PSScriptRoot 'scripts\smoke_test.py'))
    }
    Write-Host 'Setup completed. Run Start.cmd. Models must be installed before offline use.' -ForegroundColor Green
    exit 0
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host 'Setup failed. Check the message above; you can rerun Setup.cmd after fixing it.'
    exit 1
}
