param([Parameter(Mandatory=$true)][string]$RunFolder)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$runRoot = [System.IO.Path]::GetFullPath($RunFolder)
if (Test-Path -LiteralPath $runRoot) { throw 'Use a new output directory.' }
New-Item -ItemType Directory -Path $runRoot | Out-Null
Add-Type -AssemblyName System.Speech
$sentences = @(
 'We should leave before the rain starts this evening.',
 'Please remember to close the window when you leave.',
 'There is a small bookstore beside the train station.',
 'I thought you were going to meet us after school.',
 'The weather is much colder than it was yesterday.',
 'Would you like another cup of tea before we begin?',
 'Nobody knows why the lights went out last night.',
 'She has been waiting at the entrance for an hour.',
 'This is the first time I have visited this town.',
 'You can keep the book until the end of next week.',
 'We need to find a different way across the river.',
 'I will explain everything when we get back home.'
)
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$rows = @()
try {
 $voice = $synth.GetInstalledVoices() | Where-Object { $_.VoiceInfo.Culture.Name -eq 'en-US' } | Select-Object -First 1
 if ($null -eq $voice) { throw 'An installed en-US Windows TTS voice is required.' }
 $synth.SelectVoice($voice.VoiceInfo.Name)
 $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
 for ($i = 0; $i -lt $sentences.Count; $i++) {
  $wav = Join-Path $runRoot ("sample-{0}.wav" -f $i)
  $synth.SetOutputToWaveFile($wav, $format); $synth.Speak($sentences[$i]); $synth.SetOutputToNull()
  $rows += @{ id="tts-$i"; audio=$wav; text=$sentences[$i]; language='en'; game='synthetic-smoke-only'; scene="scene-$([math]::Floor($i / 4))"; source='Windows TTS fixture'; verified_original=$true; kind='synthetic' }
 }
} finally { $synth.Dispose() }
$manifest = Join-Path $runRoot 'input.jsonl'
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllLines($manifest, [string[]]($rows | ForEach-Object { $_ | ConvertTo-Json -Compress }), $utf8)
$model = Join-Path $projectRoot 'models\asr\whisper-base'
$config = @{ inputs=@($manifest); mode='balanced'; max_rtf=1.0; budget_seconds=240; worker_path=(Join-Path $projectRoot 'src\ACGTranslator.App\Resources\asr_worker.py'); candidates=@(
 @{ name='base-cpu-beam2'; model=$model; device='cpu'; beam=2; language='en' },
 @{ name='base-cpu-beam1'; model=$model; device='cpu'; beam=1; language='en' }
) }
$configFile = Join-Path $runRoot 'config.json'
[System.IO.File]::WriteAllText($configFile, ($config | ConvertTo-Json -Depth 6), $utf8)
& (Join-Path $projectRoot '.venv\Scripts\python.exe') (Join-Path $PSScriptRoot 'autotune.py') --config $configFile --out (Join-Path $runRoot 'run')
if ($LASTEXITCODE -ne 0) { throw 'Calibration smoke failed; inspect the report.' }
Write-Output 'Synthetic smoke complete. No user settings were changed. This is not a game-accuracy benchmark.'
