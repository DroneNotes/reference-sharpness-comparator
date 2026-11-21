$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$output = Join-Path $projectRoot 'tests\output'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
$sources = @('ProductInfo.cs','FocusAnalysis.cs','AnalysisSession.cs','ReportHtml.cs') | ForEach-Object { Join-Path $projectRoot ('src\' + $_) }
$exe = Join-Path $output 'Benchmark.exe'
& $compiler /nologo /optimize+ /target:exe /define:PHOTO_V2 "/out:$exe" /r:System.dll,System.Core.dll,System.Drawing.dll,System.Web.Extensions.dll @sources (Join-Path $projectRoot 'tests\Benchmark.cs')
if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }
& $exe (Join-Path $output 'benchmark.json')
if ($LASTEXITCODE -ne 0) { throw 'Benchmark failed.' }
