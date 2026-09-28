param([string]$DotnetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
$sourcePath = Join-Path $PSScriptRoot '../../Universal x86 Tuning Utility/Scripts/AMD Backend/RyzenSmu.cs'
$source = [IO.File]::ReadAllText($sourcePath)
$start = $source.IndexOf('    internal sealed class RyzenSMU : IDisposable')
if ($start -lt 0) { throw 'Cannot locate the actual RyzenSMU transport source.' }
$header = @'
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Universal_x86_Tuning_Utility.Scripts.AMD_Backend;
namespace RyzenSmu {
'@
$generatedDirectory = Join-Path $PSScriptRoot 'obj'
[IO.Directory]::CreateDirectory($generatedDirectory) | Out-Null
[IO.File]::WriteAllText((Join-Path $generatedDirectory 'RyzenSMU.extracted.cs'), $header + $source.Substring($start))
& $DotnetPath run --project (Join-Path $PSScriptRoot 'AutoOcTransportTests.csproj') --configuration Release
if ($LASTEXITCODE -ne 0) { throw "Transport tests failed with exit code $LASTEXITCODE." }
