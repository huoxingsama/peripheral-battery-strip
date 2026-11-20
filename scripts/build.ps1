[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist')
)

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$sourceDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'src'
$sources = @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' | Sort-Object Name | Select-Object -ExpandProperty FullName)
$manifest = Join-Path $sourceDirectory 'app.manifest'
$jsonAssembly = Join-Path $framework 'System.Web.Extensions.dll'

if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The Windows .NET Framework x64 compiler was not found.'
}

$output = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
$exe = Join-Path $output 'BatteryHubStrip.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 "/reference:$jsonAssembly" "/win32manifest:$manifest" "/out:$exe" @sources
if ($LASTEXITCODE -ne 0) { throw "The compiler exited with code $LASTEXITCODE." }
Get-Item -LiteralPath $exe | Select-Object -ExpandProperty FullName
