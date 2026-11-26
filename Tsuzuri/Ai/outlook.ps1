# Tsuzuri and AiLink: search and read mail in the Outlook (classic) the user has open, and save drafts (SPEC 19, AiLink SPEC).
# One file for both apps (AiLink links this file). The environment tells which app runs it, as for excel.ps1:
#   Tsuzuri: TSUZURI_AI_PIPE, TSUZURI_AI_NAME; the AI writes its draft file next to this script. Messages call it 'the editor'.
#   AiLink: AILINK_PIPE, AILINK_NAME, AILINK_WORK; the AI writes its draft file in AILINK_WORK. Messages call it 'AiLink'.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File outlook.ps1 -Folders
#       the paths of the mail folders of every store (no mail). The app writes them to a file and this prints its path.
#   powershell -NoProfile -ExecutionPolicy Bypass -File outlook.ps1 -Search -Folder "<folder path>" [-From "<text>"] [-Subject "<text>"] [-Text "<text>"]
#                                                                  [-Since 2026-11-01] [-Until 2026-11-30] [-Subfolders] [-Limit 50]
#       lists the mail in the folder (newest first): received time, sender, subject, ID. The app writes the list to a file and this prints its path.
#       -Folder is the folder path as the Outlook list shows it (\\mailbox\Inbox\sub). -Limit: up to 200 (50 when left out).
#   powershell -NoProfile -ExecutionPolicy Bypass -File outlook.ps1 -Read -Folder "<folder path>" -Id "<ID from the list>"
#       one mail: headers, attachment names and the text of the body. The app writes it to a file and this prints its path.
#   powershell -NoProfile -ExecutionPolicy Bypass -File outlook.ps1 -Draft
#       saves a new draft from outlook-draft-<name>.txt in the work folder (UTF-8; removed once sent, kept when nothing was sent):
#       lines "To: ...", "Cc: ..." and "Subject: ..." first, an empty line, then the body.
#   powershell -NoProfile -ExecutionPolicy Bypass -File outlook.ps1 -Draft -ReplyTo "<ID>" -Folder "<folder path>" [-All]
#       saves a reply (-All: reply to all) to that mail as a draft, with the whole file as the text put above the quoted mail.
#   Drafts are only saved in the Drafts folder. Nothing here sends, deletes or moves mail.
#
# It goes through the pipe of the window (TSUZURI_AI_PIPE or AILINK_PIPE). Exit code 1 when it failed.
# A wrong call (two modes, an unknown parameter, a missing -Folder or -Id, a bad date) fails before sending anything.
# Keep this file ASCII only: PowerShell 5.1 reads a script without BOM as the ANSI code page.

[CmdletBinding(PositionalBinding = $false)]
param(
    [switch]$Folders,
    [switch]$Search,
    [switch]$Read,
    [switch]$Draft,
    [string]$Folder = '',
    [string]$Id = '',
    [string]$From = '',
    [string]$Subject = '',
    [string]$Text = '',
    [string]$Since = '',
    [string]$Until = '',
    [switch]$Subfolders,
    [int]$Limit = 50,
    [string]$ReplyTo = '',
    [switch]$All
)

$ErrorActionPreference = 'Stop'
# the tool that runs this waits 120 seconds (the instructions): give up before it does, counting from the start of this process
$started = (Get-Process -Id $PID).StartTime
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Fail([string]$message) { [Console]::Error.WriteLine($message); exit 1 }
function B64([string]$text) { return [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($text)) }

# check the arguments before touching any file: a wrong call fails here and sends nothing
$modes = @($Folders, $Search, $Read, $Draft) | Where-Object { $_ }
if (@($modes).Count -ne 1) { Fail 'give exactly one of -Folders, -Search, -Read and -Draft. Nothing was sent' }
$given = $PSBoundParameters
$searchOnly = 'From', 'Subject', 'Text', 'Since', 'Until', 'Subfolders', 'Limit'
if (-not $Search) {
    foreach ($name in $searchOnly) { if ($given.ContainsKey($name)) { Fail ('-' + $name + ' is only for -Search. Nothing was sent') } }
}
if ($Folders -and $given.ContainsKey('Folder')) { Fail '-Folders lists every folder and takes no -Folder. Nothing was sent' }
if (-not $Read -and $given.ContainsKey('Id')) { Fail '-Id is only for -Read (a reply draft takes -ReplyTo). Nothing was sent' }
if (-not $Draft) {
    foreach ($name in 'ReplyTo', 'All') { if ($given.ContainsKey($name)) { Fail ('-' + $name + ' is only for -Draft. Nothing was sent') } }
}
if ($Search -or $Read) {
    if (-not $Folder) { Fail ('-Folder is needed (the folder path as the Outlook list shows it). Nothing was sent') }
}
if ($Read -and -not $Id) { Fail '-Read needs -Id (from a -Search list). Nothing was sent' }
if ($Draft) {
    if ($All -and -not $ReplyTo) { Fail '-All is only for a reply draft (-ReplyTo). Nothing was sent' }
    if ($ReplyTo -and -not $Folder) { Fail '-ReplyTo needs -Folder (the folder of that mail). Nothing was sent' }
    if (-not $ReplyTo -and $given.ContainsKey('Folder')) { Fail 'a new draft does not take -Folder (it is saved in Drafts). Nothing was sent' }
}
if ($Search) {
    if ($Limit -lt 1 -or $Limit -gt 200) { Fail '-Limit is from 1 to 200. Nothing was sent' }
    foreach ($date in @($Since, $Until)) {
        if ($date) {
            $parsed = [DateTime]::MinValue
            if (-not [DateTime]::TryParseExact($date, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$parsed)) {
                Fail ('-Since and -Until are dates such as 2026-11-01 (not ' + $date + '). Nothing was sent')
            }
        }
    }
}

# which app: AiLink sets AILINK_PIPE, Tsuzuri sets TSUZURI_AI_PIPE (each app removes the other's variables when it opens a conversation)
if ($env:AILINK_PIPE) {
    $pipe = $env:AILINK_PIPE
    $app = 'AiLink'
    $name = $env:AILINK_NAME
    $noName = 'AILINK_NAME is not set: open a new conversation tab in AiLink'
    $work = if ($env:AILINK_WORK) { $env:AILINK_WORK } else { $PSScriptRoot }
} elseif ($env:TSUZURI_AI_PIPE) {
    $pipe = $env:TSUZURI_AI_PIPE
    $app = 'the editor'
    $name = $env:TSUZURI_AI_NAME
    $noName = 'TSUZURI_AI_NAME is not set: open a new conversation tab in the AI pane (a conversation started before this feature needs a restart)'
    $work = $PSScriptRoot
} else {
    Fail 'TSUZURI_AI_PIPE is not set and AILINK_PIPE is not set. Run this from the AI pane of Tsuzuri or AiLink (a conversation started before this feature needs a restart).'
}

$tab = [char]9
$utf8 = New-Object System.Text.UTF8Encoding($false, $true)
$unsure = ' (the draft may or may not have been saved: look in the Drafts folder before trying again)'
$File = $null
$claimed = $null

# put the draft file back when nothing was sent, so the same call can be tried again (unless a new one was written meanwhile)
function Restore {
    if ($script:claimed -and -not (Test-Path -LiteralPath $script:File)) {
        try { [System.IO.File]::Move($script:claimed, $script:File) } catch { }
    }
    $script:claimed = $null
}

$content = $null
if ($Draft) {
    if (-not $name) { Fail $noName }
    $File = Join-Path $work ('outlook-draft-' + $name + '.txt')
    if (-not (Test-Path -LiteralPath $File)) { Fail ('file not found: ' + $File + ' (write it first)') }
    # take the draft file by moving it to a name of its own: only the call that moved it sends it, and it is never sent twice
    $claimed = Join-Path $work ('outlook-draft-' + $name + '-sending-' + [Guid]::NewGuid().ToString('N').Substring(0, 12) + '.txt')
    try {
        [System.IO.File]::Move($File, $claimed)
    } catch {
        $claimed = $null
        Fail ('could not take ' + $File + ': ' + $_.Exception.Message + '. Nothing was sent')
    }
    try {
        $content = [System.IO.File]::ReadAllText($claimed, $utf8)
    } catch {
        Restore
        Fail ('could not read ' + $File + ' as UTF-8: ' + $_.Exception.Message + '. Nothing was sent (the file is kept)')
    }
    if (-not $content.Trim()) {
        Restore
        Fail ($File + ' is empty. Nothing was sent (the file is kept)')
    }
}

$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
$sent = $false
try {
    try {
        $client.Connect(15000)
    } catch {
        Restore
        Fail ('could not reach ' + $app + ': ' + $_.Exception.Message + '. Nothing was sent' + $(if ($Draft) { ' (the file is kept)' } else { '' }))
    }

    if ($Folders) {
        $request = 'outlook-folders' + $tab + 'end'
    } elseif ($Search) {
        $request = 'outlook-search' + $tab + (B64 $Folder) + $tab + (B64 $From) + $tab + (B64 $Subject) + $tab + (B64 $Text) + $tab + (B64 $Since) + $tab + (B64 $Until) +
                   $tab + (B64 $(if ($Subfolders) { '1' } else { '0' })) + $tab + (B64 ([string]$Limit)) + $tab + 'end'
    } elseif ($Read) {
        $request = 'outlook-read' + $tab + (B64 $Folder) + $tab + (B64 $Id) + $tab + 'end'
    } else {
        $request = 'outlook-draft' + $tab + (B64 $ReplyTo) + $tab + (B64 $Folder) + $tab + (B64 $(if ($All) { '1' } else { '0' })) + $tab + (B64 $content) + $tab + 'end'
    }

    $sent = $true
    $writer = New-Object System.IO.StreamWriter($client, (New-Object System.Text.UTF8Encoding($false)))
    $writer.WriteLine($request)
    $writer.Flush()
    $reader = New-Object System.IO.StreamReader($client, [System.Text.Encoding]::UTF8)
    $limitSeconds = 90
    $left = [Math]::Max(1000, [int](($limitSeconds - ((Get-Date) - $started).TotalSeconds) * 1000))
    $pending = $reader.ReadLineAsync()
    if (-not $pending.Wait($left)) { Fail ('no answer from ' + $app + ' in ' + $limitSeconds + ' seconds' + $(if ($Draft) { $unsure } else { '' })) }
    $answer = $pending.Result
} catch {
    if ($sent) { Fail ('no answer from ' + $app + ': ' + $_.Exception.GetBaseException().Message + $(if ($Draft) { $unsure } else { '' })) }
    Restore
    Fail ('could not send to ' + $app + ': ' + $_.Exception.GetBaseException().Message + '. Nothing was sent')
} finally {
    $client.Dispose()
    if ($claimed) { Remove-Item -LiteralPath $claimed -ErrorAction SilentlyContinue }
}

if (-not $answer) { Fail ('no answer from ' + $app + $(if ($Draft) { $unsure } else { '' })) }
$parts = $answer.Split([char]9, 2)
if ($parts[0] -ne 'ok' -and $parts[0] -ne 'error') { Fail ('an answer from ' + $app + ' that could not be read' + $(if ($Draft) { $unsure } else { '' })) }
$message = if ($parts.Count -gt 1) { $parts[1] } else { '' }
if ($parts[0] -eq 'ok') {
    if (-not $message) { Fail ('an answer from ' + $app + ' without a message') }
    [Console]::Out.WriteLine($message)
    exit 0
}
Fail $message
