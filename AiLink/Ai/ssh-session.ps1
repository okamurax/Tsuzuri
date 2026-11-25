# AiLink: talk to a network device (Yamaha RTX etc.) over ssh from the AI pane (AiLink SPEC).
# Such devices refuse "ssh host command" (exec request failed), drop what is typed before the prompt
# and close the session when the input ends. the shell tool of the AI cannot type into a running program,
# so this script opens a terminal (-tt), waits for the prompt, sends one line at a time,
# waits for the prompt again after each line, and ends with exit.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File ssh-session.ps1 -Name <name in config.txt> -Command "show status"
#   powershell -NoProfile -ExecutionPolicy Bypass -File ssh-session.ps1 -Name <name> -CommandFile <file with one command per line>
#
# The connection (host, port, user, key or password) is read from AiLink's copy of config.txt (..\ssh\config.txt).
# An entry with auth=via is reached through the entry named by via= (ssh runs on that host with its own keys).
# The password goes through ssh-askpass.ps1, never through the command line.
# Lines with passwords, pre-shared keys and the like are masked in the output.
# It stops (and says so) when the device asks for a password or (Y/N); it never answers those.
# Keep this file ASCII only: PowerShell 5.1 reads a script without BOM as the ANSI code page.

param(
    [Parameter(Mandatory = $true)][string]$Name,
    [string]$Command,
    [string]$CommandFile,
    [string]$Encoding = 'shift_jis',
    [int]$Timeout = 60
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$sshDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'ssh'
$sshExe = Join-Path $env:SystemRoot 'System32\OpenSSH\ssh.exe'

function Fail([string]$message) { [Console]::Error.WriteLine($message); exit 1 }

# --- the entry in config.txt ---
function Read-Entry([string]$entryName) {
    foreach ($line in [System.IO.File]::ReadAllLines((Join-Path $sshDir 'config.txt'))) {
        $fields = $line.Split('|')
        if ($fields.Count -lt 2 -or $fields[0].Trim() -ne $entryName.Trim()) { continue }
        $result = @{ name = $fields[0].Trim(); address = $fields[1].Trim(); port = '22' }
        $colon = $result.address.LastIndexOf(':')
        if ($colon -gt 0) { $result.port = $result.address.Substring($colon + 1); $result.address = $result.address.Substring(0, $colon) }
        foreach ($field in $fields) {
            $text = $field.Trim()
            $equal = $text.IndexOf('=')
            if ($equal -gt 0) { $result[$text.Substring(0, $equal)] = $text.Substring($equal + 1) }
        }
        if (-not $result['user']) { Fail ('no user= in the entry "' + $entryName + '"') }
        return $result
    }
    Fail ('no entry "' + $entryName + '" in config.txt')
}

# auth=via: not reachable directly. Connect to the entry named by via= and run ssh there (with the keys of that host).
$target = Read-Entry $Name
$values = $target
$inner = $null
if ($target['auth'] -eq 'via') {
    if (-not $target['via']) { Fail ('no via= in the entry "' + $Name + '"') }
    $values = Read-Entry $target['via']
    if ($values['auth'] -eq 'via') { Fail ('the entry "' + $values.name + '" is also auth=via (only one hop is supported)') }
    $inner = 'ssh -tt -o BatchMode=yes -o StrictHostKeyChecking=accept-new -p ' + $target.port + ' ' + $target['user'] + '@' + $target.address
}

# --- the commands ---
$lines = @()
if ($CommandFile) { $lines += [System.IO.File]::ReadAllLines($CommandFile) }
if ($Command) { $lines += $Command.Split("`n") }
$lines = @($lines | ForEach-Object { $_.TrimEnd([char]13) } | Where-Object { $_.Trim() -ne '' })
if ($lines.Count -eq 0) { Fail 'no command (-Command or -CommandFile)' }

# --- ssh ---
function Quote([string]$value) { if ($value -match '[\s"]') { return '"' + $value.Replace('"', '\"') + '"' } return $value }

$arguments = @('-tt', '-p', $values.port,
    '-o', ('UserKnownHostsFile=' + (Join-Path $sshDir 'known_hosts')),
    '-o', 'StrictHostKeyChecking=accept-new')
$info = New-Object System.Diagnostics.ProcessStartInfo
if ($values['auth'] -eq 'password') {
    $arguments += @('-o', 'PubkeyAuthentication=no', '-o', 'PreferredAuthentications=password,keyboard-interactive', '-o', 'NumberOfPasswordPrompts=1')
    $info.EnvironmentVariables['AILINK_SSH_HOST'] = $values.name
    $info.EnvironmentVariables['SSH_ASKPASS'] = (Join-Path $PSScriptRoot 'ssh-askpass.cmd')
    $info.EnvironmentVariables['SSH_ASKPASS_REQUIRE'] = 'force'
} elseif ($values['keyfile']) {
    $arguments += @('-i', (Join-Path $sshDir $values['keyfile']))
}
$arguments += ($values['user'] + '@' + $values.address)
if ($inner) { $arguments += $inner }

$info.FileName = $sshExe
$info.Arguments = (($arguments | ForEach-Object { Quote $_ }) -join ' ')
$info.UseShellExecute = $false
$info.RedirectStandardInput = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$info.CreateNoWindow = $true

$process = [System.Diagnostics.Process]::Start($info)
$errors = $process.StandardError.ReadToEndAsync()
$stdout = $process.StandardOutput.BaseStream
$stdin = $process.StandardInput
$decoder = [System.Text.Encoding]::GetEncoding($Encoding).GetDecoder()
$bytes = New-Object byte[] 8192
$chars = New-Object char[] 16384
$script:text = New-Object System.Text.StringBuilder
$script:readTask = $null
$script:eof = $false

# Reads what has come within $ms milliseconds. True if anything came.
function Pump([int]$ms) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $got = $false
    while ($watch.ElapsedMilliseconds -lt $ms) {
        if ($script:readTask -eq $null) { $script:readTask = $stdout.ReadAsync($bytes, 0, $bytes.Length) }
        $left = [Math]::Max(1, $ms - [int]$watch.ElapsedMilliseconds)
        if (-not $script:readTask.Wait($left)) { break }
        $count = $script:readTask.Result
        $script:readTask = $null
        if ($count -le 0) { $script:eof = $true; break }
        $length = $decoder.GetChars($bytes, 0, $count, $chars, 0)
        [void]$script:text.Append($chars, 0, $length)
        $got = $true
    }
    return $got
}

function Kind {
    $all = $script:text.ToString()
    $tail = $all.Substring([Math]::Max(0, $all.Length - 200))
    if ($tail -match '(?i)password: ?$') { return 'password' }
    if ($tail -match '(?i)\(Y/N\)\s*:?\s*$') { return 'yesno' }
    if ($tail -match '[>#] ?$') { return 'prompt' }
    return $null
}

# Waits until the output stops at a prompt (or a password / (Y/N) question).
function WaitFor([int]$seconds) {
    $deadline = [DateTime]::Now.AddSeconds($seconds)
    while ([DateTime]::Now -lt $deadline) {
        [void](Pump 500)
        if ($script:eof) { return 'eof' }
        $kind = Kind
        if ($kind -and -not (Pump 400)) { return $kind }   # quiet after something that looks like a prompt
        if ($script:eof) { return 'eof' }
    }
    return 'timeout'
}

# Prints what came, with secrets masked.
function Emit {
    $all = $script:text.ToString().Replace("`r`n", "`n").Replace("`r", '')
    [void]$script:text.Clear()
    $masked = foreach ($line in $all.Split("`n")) {
        $line -replace '(?i)\b(password|pre-shared-key|secret|community|pp auth username|pp auth myname)(\s+\S.*)$', '$1 ***'
    }
    [Console]::Out.Write(($masked -join "`n"))
}

function Send([string]$line) { $stdin.Write($line + "`r"); $stdin.Flush() }

function Stop([string]$why) {
    Emit
    [Console]::Out.WriteLine('')
    [Console]::Out.WriteLine('[ssh-session] ' + $why)
    if (-not $process.HasExited) { $process.Kill() }
    $message = $errors.Result
    if ($message) { [Console]::Error.Write($message) }
    exit 1
}

$state = WaitFor $Timeout
if ($state -ne 'prompt') { Stop ('no prompt after login (' + $state + ')') }
Emit

foreach ($line in @('console lines infinity') + $lines) {
    Send $line
    $state = WaitFor $Timeout
    if ($state -eq 'password') { Stop ('stopped: the device asks for a password after "' + $line + '". This script does not answer it.') }
    if ($state -eq 'yesno') { Stop ('stopped: the device asks (Y/N) after "' + $line + '". This script does not answer it.') }
    if ($state -ne 'prompt') { Stop ('stopped after "' + $line + '" (' + $state + ')') }
    Emit
}

Send 'exit'
[void](WaitFor 10)
Emit
[Console]::Out.WriteLine('')
if (-not $process.WaitForExit(5000)) { $process.Kill() }
$message = $errors.Result
if ($message) { [Console]::Error.Write($message) }
exit 0
