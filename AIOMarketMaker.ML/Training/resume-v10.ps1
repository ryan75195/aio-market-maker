# Resume v10 variant classifier training from latest checkpoint.
# Checkpoint: step 24500 / 71540 (epoch 1.71)
# Uses v9 train.py with v10 data and output dirs.

$TrainScript = Join-Path $PSScriptRoot "v9\train.py"
$DataFile    = Join-Path $PSScriptRoot "data\labeled_pairs_v10.csv"
$OutputDir   = "E:/Dev/ml-training/variant-classifier/v10/pytorch"

Write-Host "Resuming v10 training from latest checkpoint..." -ForegroundColor Cyan
Write-Host "  Script:     $TrainScript"
Write-Host "  Data:       $DataFile"
Write-Host "  Output:     $OutputDir"
Write-Host "  Checkpoint: ${OutputDir}_checkpoints/checkpoint-24500"
Write-Host ""

py -3.12 -u $TrainScript `
    --data $DataFile `
    --output-dir $OutputDir `
    --resume
