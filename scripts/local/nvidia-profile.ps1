# Returns backend-only environment overrides. Never write the profile/key to the host output.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProfilePath)
$ErrorActionPreference = 'Stop'
$overrides = @{ Embeddings__Provider = 'None'; TextGeneration__Provider = 'None'; Embeddings__ApiKey = ''; TextGeneration__ApiKey = '' }
if (-not (Test-Path -LiteralPath $ProfilePath)) { return $overrides }
try { $profile = Get-Content -LiteralPath $ProfilePath -Raw | ConvertFrom-Json -AsHashtable }
catch { throw 'NVIDIA profile must be valid JSON; contents omitted.' }
if ($profile -isnot [System.Collections.IDictionary]) { throw 'NVIDIA profile must be a JSON object.' }
$key = $profile['ApiKey']
if ($null -ne $key -and $key -isnot [string]) { throw 'NVIDIA ApiKey must be a string.' }
if ([string]::IsNullOrWhiteSpace($key)) { return $overrides }
if ($key.Length -gt 4096 -or $key -cmatch '[^\x21-\x7E]') { throw 'NVIDIA ApiKey must contain visible ASCII without whitespace.' }
foreach ($section in @('TextGeneration', 'Embeddings', 'AiExecution')) {
  if ($profile[$section] -isnot [System.Collections.IDictionary]) { throw "NVIDIA profile requires object section $section." }
  foreach ($entry in $profile[$section].GetEnumerator()) {
    if ($entry.Key -notmatch '^[A-Za-z][A-Za-z0-9]*$' -or $entry.Key -eq 'ApiKey') { throw "Invalid setting name in NVIDIA $section section." }
    if ($entry.Value -is [System.Collections.IDictionary]) {
      if ($section -ne 'AiExecution' -or $entry.Key -notin @('Fast','Balanced','Quality')) { throw 'Unexpected nested NVIDIA setting.' }
      foreach ($price in $entry.Value.GetEnumerator()) {
        if ($price.Key -notin @('InputPerMillionTokens','OutputPerMillionTokens') -or $price.Value -isnot [ValueType]) { throw 'Invalid NVIDIA token price.' }
        $overrides["${section}__$($entry.Key)__$($price.Key)"] = [Convert]::ToString($price.Value, [Globalization.CultureInfo]::InvariantCulture)
      }
    } elseif ($entry.Value -is [string] -or $entry.Value -is [ValueType]) {
      $overrides["${section}__$($entry.Key)"] = [Convert]::ToString($entry.Value, [Globalization.CultureInfo]::InvariantCulture)
    } else { throw 'NVIDIA setting must be a scalar value.' }
  }
}
# The shared key is supplied to API/Worker only, never to Next.js/Vite processes.
$overrides['Embeddings__ApiKey'] = $key
$overrides['TextGeneration__ApiKey'] = $key
return $overrides
