param([switch]$Launch, [switch]$Tests)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$projectFile = @(Get-ChildItem -LiteralPath $projectRoot -File -Filter '*.csproj')
if ($projectFile.Count -ne 1) { throw 'Expected one C# project file in this folder.' }
[xml]$project = Get-Content -LiteralPath $projectFile[0].FullName -Raw
$assemblyName = [string]$project.SelectSingleNode("//*[local-name()='AssemblyName']").InnerText
if ($assemblyName -notmatch '^[A-Za-z][A-Za-z0-9_.-]*$') { throw 'Invalid AssemblyName.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework C# compiler was not found.' }
$sources = @($project.SelectNodes("//*[local-name()='Compile']") | ForEach-Object {
    $path = [IO.Path]::GetFullPath((Join-Path $projectRoot $_.Include))
    if (-not $path.StartsWith($projectRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Source files must be inside this project.' }
    if (-not (Test-Path -LiteralPath $path)) { throw "Source file missing: $path" }
    $path
})
$stage = Join-Path $projectRoot 'obj\command-line'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$temporary = Join-Path $stage ($assemblyName + '.exe')
$destination = Join-Path $projectRoot ($assemblyName + '.exe')
$arguments = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/warn:4','/warnaserror+', '/main:PhotoFocus.Program', "/out:$temporary", '/r:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Web.Extensions.dll')
& $compiler @arguments @sources
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed. The previous executable has been preserved.' }
try { Move-Item -LiteralPath $temporary -Destination $destination -Force }
catch { throw 'Close the running application and build again. The previous executable has been preserved.' }
Write-Host "Built from local source: $destination"
if ($Tests) { & (Join-Path $projectRoot 'Run-Tests.ps1'); if ($LASTEXITCODE -ne 0) { throw 'Project checks failed.' } }
if ($Launch) { Start-Process -FilePath $destination -WorkingDirectory $projectRoot }
