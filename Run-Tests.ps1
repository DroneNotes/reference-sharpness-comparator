param([switch]$Ui)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$output = Join-Path $projectRoot 'tests\output'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$name = 'PhotoCompare'
$comparison = $true
$exe = Join-Path $projectRoot ($name + '.exe')
if (-not (Test-Path -LiteralPath $exe)) { throw 'Run BUILD.bat before running checks.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$sources = @('ProductInfo.cs','FocusAnalysis.cs','AnalysisSession.cs','ReportHtml.cs','BatchAnalysis.cs') | ForEach-Object { Join-Path $projectRoot ('src\' + $_) }
$testExe = Join-Path $output 'CoreChecks.exe'
& $compiler /nologo /target:exe /optimize+ "/out:$testExe" /r:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Web.Extensions.dll @sources (Join-Path $projectRoot 'tests\PhotoTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Core-check build failed.' }
& $testExe $output
if ($LASTEXITCODE -ne 0) { throw 'Numerical checks failed.' }
function Invoke-App([string[]]$Options, [int]$Expected = 0) {
    $quoted = $Options | ForEach-Object { '"' + $_ + '"' }
    $run = Start-Process -FilePath $exe -WorkingDirectory $projectRoot -ArgumentList $quoted -WindowStyle Hidden -Wait -PassThru
    if ($run.ExitCode -ne $Expected) { throw "Unexpected application exit code: $($run.ExitCode)." }
}
$reference = Join-Path $output 'reference.png'
$working = Join-Path $output 'working.png'
$baseArguments = @('--image',$working)
if ($comparison) { $baseArguments += @('--reference',$reference) }
foreach ($format in @('json','html','csv','txt')) { Invoke-App ($baseArguments + @('--out',(Join-Path $output ('application.' + $format)))) }
$report = Get-Content -LiteralPath (Join-Path $output 'application.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $report.ToolVersion.StartsWith($name + ' ') -or $report.Working.Width -ne 320) { throw 'Application identity or output check failed.' }
if ($comparison) {
    Invoke-App @('--image',$reference,'--reference',$reference,'--out',(Join-Path $output 'identity.json'))
    $identity = Get-Content -LiteralPath (Join-Path $output 'identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([Math]::Abs($identity.RelativeDetailPercent - 100) -gt 0.0000001) { throw 'Identity comparison failed.' }
}
$batch = Join-Path $output 'batch-input'
New-Item -ItemType Directory -Path $batch -Force | Out-Null
Copy-Item -LiteralPath $reference,$working -Destination $batch -Force
Set-Content -LiteralPath (Join-Path $batch 'broken.png') -Value 'not an image' -Encoding Ascii
$batchArguments = @('--batch',$batch,'--out',(Join-Path $output 'batch-application.json'))
if ($comparison) { $batchArguments += @('--reference',$reference) }
Invoke-App $batchArguments 1
$rows = Get-Content -LiteralPath (Join-Path $output 'batch-application.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($rows.Count -ne 3 -or @($rows | Where-Object { $_.Error }).Count -ne 1) { throw 'Batch error handling failed.' }
$before = (Get-FileHash -LiteralPath $working -Algorithm SHA256).Hash
Invoke-App ($baseArguments + @('--out',$working)) 2
if ((Get-FileHash -LiteralPath $working -Algorithm SHA256).Hash -ne $before) { throw 'A source image was modified.' }
if ($Ui) {
    Invoke-App @('--smoke',$output)
    Invoke-App @('--smoke-batch',$output)
    $prefix = if ($comparison) { 'compare' } else { 'single' }
    foreach ($file in @("$prefix-ui.png","$prefix-ui.json","$prefix-batch-ui.png","$prefix-batch-ui.json")) {
        if (-not (Test-Path -LiteralPath (Join-Path $output $file))) { throw "UI output missing: $file" }
    }
}
Write-Host "PASS: $name local numerical, command-line, export and batch checks."
