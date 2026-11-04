$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root
npx -y gitnexus@latest analyze --index-only --name MAIC
