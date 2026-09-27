param([string]$DotnetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
$sourcePath = Join-Path $PSScriptRoot '../../Universal x86 Tuning Utility/Scripts/AMD Backend/RyzenSmu.cs'
$source = [IO.File]::ReadAllText($sourcePath)
$start = $source.IndexOf('    static class SMUCommands')
$end = $source.IndexOf('    class Smu', $start)
if ($start -lt 0 -or $end -le $start) { throw 'Cannot locate the actual SMUCommands source.' }
$header = @'
using System.Collections.Concurrent;
using Universal_x86_Tuning_Utility.Scripts.Misc;
using static RyzenSmu.RyzenSMU;
using AutoOcDiagnostics = TestDiagnostics;
namespace RyzenSmu {
'@
$generatedDirectory = Join-Path $PSScriptRoot 'obj'
[IO.Directory]::CreateDirectory($generatedDirectory) | Out-Null
[IO.File]::WriteAllText((Join-Path $generatedDirectory 'SMUCommands.extracted.cs'), $header + $source.Substring($start, $end - $start) + '}')
& $DotnetPath run --project (Join-Path $PSScriptRoot 'AutoOcSmuTests.csproj') --configuration Release
if ($LASTEXITCODE -ne 0) { throw "SMU diagnostic tests failed with exit code $LASTEXITCODE." }
