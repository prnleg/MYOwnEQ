# Local Windows build (needs Rust + .NET 10 SDK). Output: windows\publish\MyOwnEQ.exe
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    cargo build --release -p myowneq-ffi
    dotnet publish windows/MyOwnEQ/MyOwnEQ.csproj -c Release -r win-x64 --self-contained true -o windows/publish
    Write-Host "Done: windows\publish\MyOwnEQ.exe"
} finally { Pop-Location }
