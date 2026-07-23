# Builds an unsigned, per-user Inno Setup installer for TextTemplateManager.
# No code-signing certificate is required to install the result.
#
# Prerequisites: .NET SDK, Node.js, and Inno Setup 6.3+ (https://jrsoftware.org/isdl.php).
# Usage: pwsh -File package.ps1                   # version from latest git tag, else 0.0.0-dev
#        pwsh -File package.ps1 -Version 0.9.3     # explicit version (CI passes the tag here)
param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "Marflow Software - TextTemplateManager.csproj"
$publishRel = "publish\win-x64"                       # relative to the project (no spaces)
$publishDir = Join-Path $root $publishRel

# Version: explicit -Version wins; otherwise derive from the latest git tag (v0.9.3 -> 0.9.3);
# otherwise a dev marker so a tag-less local package (or one without git) still builds.
if (-not $Version) {
    $tag = $null
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $tag = (git -C $root describe --tags --abbrev=0 2>$null)
    }
    $Version = if ($tag) { $tag -replace '^v', '' } else { "0.0.0-dev" }
}
Write-Host "==> Version $Version" -ForegroundColor Cyan

Write-Host "==> Building editor bundle" -ForegroundColor Cyan
Push-Location (Join-Path $root "editor")
try { & node build.mjs; if ($LASTEXITCODE -ne 0) { throw "editor build failed" } }
finally { Pop-Location }

# Stamp the manual cover with the release version. (The in-build target is incremental and
# would otherwise keep an older, dev-versioned PDF; regenerate it here before publishing.)
Write-Host "==> Generating manual (version $Version)" -ForegroundColor Cyan
& dotnet run --project (Join-Path $root "tools\ManualGen\ManualGen.csproj") -c Release -- `
    (Join-Path $root "docs\Manual.md") (Join-Path $root "Assets\Manual.pdf") $Version
if ($LASTEXITCODE -ne 0) { throw "manual generation failed" }

Write-Host "==> Publishing unpackaged, self-contained (Release / win-x64)" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
& dotnet publish $proj -c Release -p:Platform=x64 -r win-x64 --self-contained true `
    -p:Version=$Version `
    -p:WindowsPackageType=None `
    -p:WindowsAppSDKSelfContained=true `
    -p:PublishTrimmed=false `
    -p:PublishDir="$publishRel\"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# File manifest (sha256 of every published file) — the enabler for computing a delta against the
# previous release later. Published as a release asset; sorted for a stable, diffable file.
Write-Host "==> Generating file manifest" -ForegroundColor Cyan
$installerDir = Join-Path $root "installer"
New-Item -ItemType Directory -Force -Path $installerDir | Out-Null
$prefixLen = $publishDir.Length + 1
$files = [ordered]@{}
Get-ChildItem -Path $publishDir -Recurse -File | Sort-Object FullName | ForEach-Object {
    $rel = $_.FullName.Substring($prefixLen) -replace '\\', '/'
    $files[$rel] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
$manifestPath = Join-Path $installerDir "manifest.json"
[ordered]@{ version = $Version; files = $files } | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Host "==> Manifest: $manifestPath ($($files.Count) files)" -ForegroundColor Green

Write-Host "==> Locating Inno Setup compiler (ISCC.exe)" -ForegroundColor Cyan
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
if (-not $iscc) { throw "ISCC.exe not found. Install Inno Setup 6.3+ from https://jrsoftware.org/isdl.php" }

Write-Host "==> Compiling installer (version $Version)" -ForegroundColor Cyan
& $iscc "/DMyAppVersion=$Version" (Join-Path $root "installer.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed" }

$setup = Join-Path $root "installer\TextTemplateManager-Setup.exe"
Write-Host "==> Done. Installer: $setup" -ForegroundColor Green
