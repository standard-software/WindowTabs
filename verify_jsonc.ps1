# Verifies Bemo.JsoncHelper against the cases that broke the settings file.
#
# On 2026-08-26 the settings were wiped while WindowTabs was running: comments
# were stripped with a regular expression that did not know where a JSON string
# begins and ends, so a saved value holding a URL lost everything from its "//"
# to the end of the line. That broke the JSON for the whole file, the read fell
# back to empty settings, and the next periodic save wrote those defaults over
# the real file.
#
# JsoncHelper is shared by the settings file and every Language\*.json, so a
# regression here loses user data. Run this after touching it:
#
#   pwsh -File verify_jsonc.ps1                    # uses WtProgram\bin\Debug
#   pwsh -File verify_jsonc.ps1 -Configuration Release
#
# Reflection is used on purpose: this tests the assembly that actually ships,
# not a copy of the algorithm. The exe is copied aside first so a running
# WindowTabs does not block the build, and so loading it does not lock it.
param(
    [ValidateSet('Debug','Release')] [string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'
$failed = $false

function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) {
        "  PASS  $name"
    } else {
        $script:failed = $true
        "  FAIL  $name -- $detail"
    }
}

$binDir = Join-Path $PSScriptRoot "WtProgram\bin\$Configuration"
$exe = Join-Path $binDir 'WindowTabs.exe'
if (-not (Test-Path $exe)) { throw "Build $Configuration first: $exe not found" }

# Load from a copy: the original stays free for the next build.
$work = Join-Path ([IO.Path]::GetTempPath()) ("wt_jsonc_" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
try {
    Copy-Item "$binDir\*" $work -Force -ErrorAction SilentlyContinue
    $asm = [Reflection.Assembly]::LoadFrom((Join-Path $work 'WindowTabs.exe'))
    $strip = $asm.GetType('Bemo.JsoncHelper').GetMethod('removeJsoncComments')
    if ($null -eq $strip) { throw 'Bemo.JsoncHelper.removeJsoncComments not found' }

    function Strip([string]$json) { $strip.Invoke($null, @([string]$json)) }
    function ValueOf([string]$json, [string]$key) {
        (Strip $json | ConvertFrom-Json).$key
    }

    'Strings are never treated as comments'
    Check 'https URL kept' `
        ((ValueOf '{ "t": "https://example.com/path" }' 't') -eq 'https://example.com/path') 'URL truncated'
    Check 'http URL kept' `
        ((ValueOf '{ "t": "http://example.com/path" }' 't') -eq 'http://example.com/path') 'URL truncated'
    Check '// inside a string kept' `
        ((ValueOf '{ "t": "// not a comment" }' 't') -eq '// not a comment') 'string truncated'
    Check '/* */ inside a string kept' `
        ((ValueOf '{ "t": "/* not a comment */" }' 't') -eq '/* not a comment */') 'string truncated'
    Check 'escaped quote does not end the string' `
        ((ValueOf '{ "t": "escaped quote: \" // still text" }' 't') -eq 'escaped quote: " // still text') 'string ended early'
    Check 'trailing backslash pair kept' `
        ((ValueOf '{ "t": "ends with a backslash \\" }' 't') -eq 'ends with a backslash \') 'escape pair mishandled'
    Check 'a string that is only a URL' `
        ((ValueOf '{ "t": "//" }' 't') -eq '//') 'string truncated'

    'Real comments are still removed'
    Check 'line comment removed' `
        ((ValueOf "{`n  // comment`n  `"t`": 1`n}" 't') -eq 1) 'line comment survived'
    Check 'block comment removed' `
        ((ValueOf '{ /* comment */ "t": 1 }' 't') -eq 1) 'block comment survived'
    Check 'line comment after a value removed' `
        ((ValueOf "{ `"t`": 1 // trailing`n}" 't') -eq 1) 'trailing comment survived'

    'Removal does not corrupt what is left'
    # A comment replaced by nothing would join the tokens either side of it.
    Check 'block comment does not join tokens' `
        ((Strip '[1/* c */2]') -notmatch '12') 'tokens merged into 12'
    # Line numbers must survive, or a parse error points at the wrong line.
    $spanning = "{`n  /* one`n     two`n     three */`n  `"t`": 1`n}"
    Check 'block comment keeps the line count' `
        (((Strip $spanning) -split "`n").Count -eq ($spanning -split "`n").Count) 'lines lost'

    'Invalid JSON still fails to parse'
    $threw = $false
    try { $null = [Newtonsoft.Json.Linq.JObject]::Parse((Strip '{ "t": 1, }{')) } catch { $threw = $true }
    Check 'broken JSON is not silently accepted' $threw 'parsed anyway'

    'Real files'
    $langDir = Join-Path $PSScriptRoot 'WtProgram\Language'
    foreach ($f in Get-ChildItem $langDir -Filter *.json) {
        $ok = $true
        $text = Get-Content $f.FullName -Raw -Encoding UTF8
        try { $null = [Newtonsoft.Json.Linq.JObject]::Parse((Strip $text)) }
        catch { try { $null = [Newtonsoft.Json.Linq.JArray]::Parse((Strip $text)) } catch { $ok = $false } }
        Check $f.Name $ok 'no longer parses'
    }
    # The live settings file, when there is one - it is the file this protects.
    $settings = Join-Path $env:APPDATA 'WindowTabs\WindowTabsSettings.txt'
    if (Test-Path $settings) {
        $raw = Get-Content $settings -Raw -Encoding UTF8
        $out = Strip $raw
        $ok = $true
        try { $null = [Newtonsoft.Json.Linq.JObject]::Parse($out) } catch { $ok = $false }
        Check 'WindowTabsSettings.txt parses' $ok 'the live settings no longer parse'
        Check 'WindowTabsSettings.txt is untouched' ($out -eq $raw) 'the stripper altered a file with no comments'
    }
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}

''
if ($failed) { 'JSONC verification FAILED'; exit 1 } else { 'JSONC verification passed'; exit 0 }
