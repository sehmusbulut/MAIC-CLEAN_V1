$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$loader = Join-Path $PSScriptRoot 'nvidia-profile.ps1'
$testFile = [IO.Path]::GetTempFileName()
function Assert-Profile($condition, $message) { if (-not $condition) { throw $message } }
try {
  $profile = Get-Content -LiteralPath (Join-Path $repoRoot 'backend/nvidia.settings.example.json') -Raw | ConvertFrom-Json -AsHashtable
  Assert-Profile ($profile.ApiKey -ceq '') 'Committed example key must be empty.'
  $profile | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $testFile
  $disabled = & $loader -ProfilePath $testFile
  Assert-Profile ($disabled.Embeddings__Provider -eq 'None' -and $disabled.TextGeneration__Provider -eq 'None') 'Blank key must disable both providers.'
  Assert-Profile ($disabled.Embeddings__ApiKey -eq '' -and $disabled.TextGeneration__ApiKey -eq '') 'Blank profile must override inherited keys.'
  $profile.ApiKey = 'offline-test-key'
  $profile | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $testFile
  $enabled = & $loader -ProfilePath $testFile
  Assert-Profile ($enabled.Embeddings__Provider -eq 'OpenAICompatible' -and $enabled.TextGeneration__Provider -eq 'OpenAICompatible') 'Filled key must enable both providers.'
  Assert-Profile ($enabled.Embeddings__ApiKey -ceq $profile.ApiKey -and $enabled.TextGeneration__ApiKey -ceq $profile.ApiKey) 'Shared key must reach both backend providers.'
  Assert-Profile ($enabled.TextGeneration__FastModel -eq 'nvidia/nemotron-3.5-lightning-30b-a3b') 'FAST mapping incorrect.'
  Assert-Profile ($enabled.TextGeneration__BalancedModel -eq 'nvidia/nemotron-3-super-120b-a12b' -and $enabled.TextGeneration__QualityModel -eq $enabled.TextGeneration__BalancedModel) 'Super mappings incorrect.'
  Assert-Profile ($enabled.Embeddings__Model -eq 'nvidia/nemotron-3-embed-1b' -and $enabled.Embeddings__Dimensions -eq '2048') 'Embedding mapping incorrect.'
  Assert-Profile ($enabled.Embeddings__DocumentInputType -eq 'passage' -and $enabled.Embeddings__QueryInputType -eq 'query') 'Embedding request modes incorrect.'
  Assert-Profile ($enabled.TextGeneration__EnableThinking -eq 'False' -and $enabled.TextGeneration__TokenLimitParameter -eq 'MaxTokens') 'Generation options incorrect.'
  foreach ($tier in @('Fast','Balanced','Quality')) {
    Assert-Profile ($enabled["AiExecution__${tier}__InputPerMillionTokens"] -eq '0' -and $enabled["AiExecution__${tier}__OutputPerMillionTokens"] -eq '0') 'Trial prices must bind explicitly.'
  }
  Assert-Profile (@($enabled.Keys | Where-Object { $_ -match 'NEXT_PUBLIC|VITE_' }).Count -eq 0) 'Provider secrets must have no frontend setting.'
  Set-Content -LiteralPath $testFile -Value '{"ApiKey":"secret-sentinel",invalid}'
  $rejected = $false
  try { $null = & $loader -ProfilePath $testFile } catch {
    Assert-Profile (-not $_.Exception.Message.Contains('secret-sentinel')) 'Malformed JSON error exposed profile contents.'
    $rejected = $true
  }
  Assert-Profile $rejected 'Malformed profile must fail before host launch.'
  Write-Output 'PASS: blank-key isolation, filled-key model/mode/pricing mapping, backend-only keys and sanitized malformed JSON.'
} finally { Remove-Item -LiteralPath $testFile }
