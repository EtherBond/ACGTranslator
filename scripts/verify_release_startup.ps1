param([Parameter(Mandatory=$true)][string]$Package, [Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference = 'Stop'
$packagePath = (Resolve-Path -LiteralPath $Package).Path
$outputPath = [System.IO.Path]::GetFullPath($Output)
$exe = Join-Path $packagePath 'ACGTranslator.App.exe'
$assembly = Join-Path $packagePath 'ACGTranslator.App.dll'
$version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($assembly).FileVersion
if (!(Test-Path -LiteralPath (Join-Path $packagePath 'System.Private.CoreLib.dll'))) { throw 'Expected self-contained Windows publish' }
if (Test-Path -LiteralPath $outputPath) { throw 'Startup result already exists' }
$isolation = Join-Path (Split-Path $outputPath) 'startup-isolated'
New-Item -ItemType Directory -Path $isolation -Force | Out-Null
$oldRoot = $env:SUBTITLE_ROOT
$oldData = $env:SUBTITLE_DATA_DIR
$oldModels = $env:SUBTITLE_MODELS_DIR
$runs = @()
try {
    $env:SUBTITLE_ROOT = $isolation
    $env:SUBTITLE_DATA_DIR = Join-Path $isolation 'settings'
    $env:SUBTITLE_MODELS_DIR = Join-Path $isolation 'models'
    foreach ($mode in @('main','capture')) {
        $owned = $null
        try {
            $arguments = @{ FilePath=$exe; WorkingDirectory=$packagePath; PassThru=$true; WindowStyle='Hidden' }
            if ($mode -eq 'capture') { $arguments.ArgumentList = '--capture' }
            $owned = Start-Process @arguments
            $until = [DateTime]::UtcNow.AddSeconds(20)
            do {
                Start-Sleep -Milliseconds 200
                $owned.Refresh()
                if ($owned.HasExited) { throw "Application exited during $mode startup (code $($owned.ExitCode))" }
            } while ($owned.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $until)
            if ($owned.MainWindowHandle -eq 0) { throw "No WPF main window observed for $mode" }
            $runs += @{ mode=$mode; title=$owned.MainWindowTitle; windowCreated=$true; responding=$owned.Responding }
            if (!$owned.Responding) { throw 'UI is not responding' }
            $null = $owned.CloseMainWindow()
            if (!$owned.WaitForExit(10000)) { throw 'UI did not close normally' }
        } finally {
            if ($owned) {
                if (!$owned.HasExited) { Stop-Process -Id $owned.Id -Force }
                $owned.Dispose()
            }
        }
    }
    $result = @{ ok=$true; version=($version -replace '\.0$',''); assemblySha256=(Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash.ToLowerInvariant();
        runs=$runs; scope='Actual self-contained executable main/capture window startup and normal close, isolated settings/root, no audio capture. Models deliberately absent; offline ASR/translation verified separately.' }
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $outputPath -Encoding UTF8
} finally {
    $env:SUBTITLE_ROOT = $oldRoot
    $env:SUBTITLE_DATA_DIR = $oldData
    $env:SUBTITLE_MODELS_DIR = $oldModels
}
