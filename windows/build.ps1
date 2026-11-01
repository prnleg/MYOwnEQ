# Local Windows build (needs Rust + .NET 10 SDK). Output: windows\publish\MyOwnEQ.exe
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    cargo build --release -p myowneq-ffi -p myowneq-engine
    dotnet publish windows/MyOwnEQ/MyOwnEQ.csproj -c Release -r win-x64 --self-contained true -o windows/publish
    Write-Host "Done: windows\publish\MyOwnEQ.exe"
    $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (Test-Path $iscc) {
        $version = if ($env:VERSION) { $env:VERSION } else { "0.0.0" }
        & $iscc "/DMyAppVersion=$version" windows/installer/MyOwnEQ.iss
        Write-Host "Installer: dist\MyOwnEQ-Setup-$version.exe"
    }
} finally { Pop-Location }
