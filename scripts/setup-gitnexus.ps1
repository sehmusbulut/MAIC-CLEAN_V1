$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

Write-Host "Checking Node..."
node --version

Write-Host "Indexing MAIC with GitNexus (index-only; project instructions remain untouched)..."
npx -y gitnexus@latest analyze --index-only --name MAIC

Write-Host "GitNexus status:"
npx -y gitnexus@latest status

Write-Host "Done. .codex/config.toml already configures the GitNexus MCP server with a response budget."
Write-Host "Open Codex from the repository root and verify MCP tools are available."
