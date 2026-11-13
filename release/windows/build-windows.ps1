<#
  PrintBridge: Windows release build (build, sign, check, zip, checksum).

  Run it from the PrintBridge project folder in PowerShell:

    # Unsigned test build (for your own testing only)
    powershell -ExecutionPolicy Bypass -File release\windows\build-windows.ps1 -Method None

    # Signed with Azure Artifact Signing (formerly Trusted Signing)
    powershell -ExecutionPolicy Bypass -File release\windows\build-windows.ps1 -Method Azure `
        -DlibPath "C:\signing\Azure.CodeSigning.Dlib.dll" -MetadataPath "C:\signing\metadata.json"

    # Signed with a certificate from a certificate authority (USB token or certificate store)
    powershell -ExecutionPolicy Bypass -File release\windows\build-windows.ps1 -Method Certificate

  Result: dist\PrintBridge-<version>-win-x64.zip and a .sha256.txt file next to it.
#>
param(
    [ValidateSet('None', 'Azure', 'Certificate')][string]$Method = 'None',
    [string]$DlibPath = '',
    [string]$MetadataPath = '',
    [string]$TimestampUrl = '',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root

function Step($text) { Write-Host ''; Write-Host "=== $text ===" -ForegroundColor Cyan }
function Stop-Here($text) { Write-Host ''; Write-Host "STOP: $text" -ForegroundColor Red; exit 1 }

# ---- 1. Check the tools ----
Step '1 of 6  Checking this computer'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Stop-Here 'The .NET SDK is not installed. Install ".NET 10 SDK" from https://dotnet.microsoft.com/download and run this again.' }
if (-not ((& dotnet --list-sdks) -match '^10\.')) { Stop-Here 'The .NET 10 SDK is required. Install it from https://dotnet.microsoft.com/download and run this again.' }

$csproj = Join-Path $root 'src\LtzPrintBridge\LtzPrintBridge.csproj'
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { Stop-Here 'Could not read the version number from the project file.' }
Write-Host "Building PrintBridge version $version (signing method: $Method)"

function Find-SignTool {
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $found = Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
    if ($found) { return $found.FullName }
    return $null
}

$signtool = $null
if ($Method -ne 'None') {
    $signtool = Find-SignTool
    if (-not $signtool) { Stop-Here 'signtool.exe was not found. Install the "Windows SDK" (tick "Windows SDK Signing Tools for Desktop Apps") from https://developer.microsoft.com/windows/downloads/windows-sdk and run this again.' }
    Write-Host "Using $signtool"
}
if ($Method -eq 'Azure') {
    if (-not $DlibPath -or -not (Test-Path $DlibPath)) { Stop-Here 'For -Method Azure, pass -DlibPath with the full path to Azure.CodeSigning.Dlib.dll.' }
    if (-not $MetadataPath -or -not (Test-Path $MetadataPath)) { Stop-Here 'For -Method Azure, pass -MetadataPath with the full path to your metadata.json file.' }
    if (-not $TimestampUrl) { $TimestampUrl = 'http://timestamp.acs.microsoft.com' }
}
if ($Method -eq 'Certificate' -and -not $TimestampUrl) { $TimestampUrl = 'http://timestamp.digicert.com' }

# ---- 2. Automatic tests ----
Step '2 of 6  Running the automatic tests'
if ($SkipTests) { Write-Host 'Skipped (-SkipTests).' }
else {
    & dotnet test (Join-Path $root 'tests\LtzPrintBridge.Tests') --nologo
    if ($LASTEXITCODE -ne 0) { Stop-Here 'The automatic tests failed. Do not publish. Send the red text above to the developer.' }
}

# ---- 3. Build the program ----
Step '3 of 6  Building PrintBridge.exe'
$out = Join-Path $root 'dist\win-staging'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
& dotnet publish (Join-Path $root 'src\LtzPrintBridge') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { Stop-Here 'The build failed. Send the red text above to the developer.' }
$exe = Join-Path $out 'PrintBridge.exe'
if (-not (Test-Path $exe)) { Stop-Here 'PrintBridge.exe was not produced.' }

# ---- 4. Sign ----
Step '4 of 6  Signing'
switch ($Method) {
    'None' { Write-Host 'Not signed. This build is for testing only and Windows will warn about it.' -ForegroundColor Yellow }
    'Azure' {
        & $signtool sign /v /debug /fd SHA256 /tr $TimestampUrl /td SHA256 /dlib $DlibPath /dc $MetadataPath $exe
        if ($LASTEXITCODE -ne 0) { Stop-Here 'Signing failed. Check that you are signed in to Azure (az login) and that your user has the "Artifact Signing Certificate Profile Signer" role.' }
    }
    'Certificate' {
        & $signtool sign /v /fd SHA256 /tr $TimestampUrl /td SHA256 /a $exe
        if ($LASTEXITCODE -ne 0) { Stop-Here 'Signing failed. Is the USB token plugged in and its software installed? Enter the token password when asked.' }
    }
}
if ($Method -ne 'None') {
    & $signtool verify /pa /v $exe
    if ($LASTEXITCODE -ne 0) { Stop-Here 'The signature did not verify. Do not publish.' }
    Write-Host 'Signature is valid.' -ForegroundColor Green
}

# ---- 5. Package ----
Step '5 of 6  Creating the ZIP'
Copy-Item (Join-Path $PSScriptRoot 'files\README.txt') $out -Force
Copy-Item (Join-Path $PSScriptRoot 'files\policy.sample.json') $out -Force
$zip = Join-Path $root "dist\PrintBridge-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out 'PrintBridge.exe'), (Join-Path $out 'README.txt'), (Join-Path $out 'policy.sample.json') -DestinationPath $zip

# ---- 6. Checksum ----
Step '6 of 6  Checksum'
$zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash
$exeHash = (Get-FileHash $exe -Algorithm SHA256).Hash
$sumFile = Join-Path $root "dist\PrintBridge-$version-win-x64.sha256.txt"
@("SHA-256 checksums for PrintBridge $version (Windows)", '', "$zipHash  $(Split-Path $zip -Leaf)", "$exeHash  PrintBridge.exe (inside the ZIP)") | Set-Content $sumFile

Write-Host ''
Write-Host 'DONE.' -ForegroundColor Green
Write-Host "  ZIP:      $zip"
Write-Host "  Checksum: $sumFile"
if ($Method -eq 'None') { Write-Host '  (unsigned test build, do not publish)' -ForegroundColor Yellow }
