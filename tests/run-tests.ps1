# Runs the test suite.
#
# The two -p: properties are required, not optional polish. The tests reference the app assembly, whose
# module initializer boots the Windows App Runtime as soon as any of its types load. Built the default
# (framework-dependent, packaged-type-unset) way, that initializer throws REGDB_E_CLASSNOTREG inside a
# test host and every test fails before it starts. These properties build the app the same way
# package.ps1 ships it — unpackaged and self-contained — which resolves the runtime locally instead.
#
# Usage: pwsh -File tests/run-tests.ps1            # run everything
#        pwsh -File tests/run-tests.ps1 -Filter SyncEngine
param(
    [string]$Filter = "",
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$proj = Join-Path $PSScriptRoot "TextTemplateManager.Tests\TextTemplateManager.Tests.csproj"

$dotnetArgs = @(
    "test", $proj,
    "-c", $Configuration,
    "-p:Platform=x64",
    "-p:WindowsPackageType=None",
    "-p:WindowsAppSDKSelfContained=true",
    "--nologo"
)
if ($Filter) { $dotnetArgs += @("--filter", $Filter) }

& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { throw "tests failed" }
Write-Host "==> All tests passed" -ForegroundColor Green
