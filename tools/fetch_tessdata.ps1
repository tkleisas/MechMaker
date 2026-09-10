# Downloads the fast English Tesseract model into tessdata/ (once).
# OCR (screen_find_text) needs it; everything else runs without it.

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$target = Join-Path $repo "tessdata\eng.traineddata"

if (Test-Path $target) { Write-Host "tessdata already present ($((Get-Item $target).Length) bytes)"; exit 0 }

New-Item -ItemType Directory -Force (Join-Path $repo "tessdata") | Out-Null
Write-Host "downloading eng.traineddata (tessdata_fast, ~4 MB)..."
Invoke-WebRequest "https://github.com/tesseract-ocr/tessdata_fast/raw/main/eng.traineddata" -OutFile $target -MaximumRedirection 5
Write-Host "done: $((Get-Item $target).Length) bytes"
