# Tsuzuri: read the Excel workbooks the user has open (SPEC 19). Read only: it never writes to Excel.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File excel.ps1
#       the used range of the sheet the user chose in the Excel list of the launcher
#   powershell -NoProfile -ExecutionPolicy Bypass -File excel.ps1 -Range "A1:H200"
#   powershell -NoProfile -ExecutionPolicy Bypass -File excel.ps1 -Sheet "<sheet>" -Range "B3:F20"   (another sheet of the chosen workbook)
#   powershell -NoProfile -ExecutionPolicy Bypass -File excel.ps1 -Book "<name or full path>" -Sheet "<sheet>"
# The workbook and sheet in front in Excel are never used.
#
# The editor reads Excel and writes the cells to a file (tab separated); this prints the path of that file.
# It goes through the pipe of the window (TSUZURI_AI_PIPE, set for the claude of the AI pane). Exit code 1 when it could not read.
# Keep this file ASCII only: PowerShell 5.1 reads a script without BOM as the ANSI code page.

param(
    [string]$Book = '',
    [string]$Sheet = '',
    [string]$Range = ''
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Fail([string]$message) { [Console]::Error.WriteLine($message); exit 1 }
function B64([string]$text) { return [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($text)) }

$pipe = $env:TSUZURI_AI_PIPE
if (-not $pipe) { Fail 'TSUZURI_AI_PIPE is not set. Run this from claude in the AI pane of Tsuzuri (a conversation started before this feature needs a restart).' }

$tab = [char]9
$request = 'excel-read' + $tab + (B64 $Book) + $tab + (B64 $Sheet) + $tab + (B64 $Range)

$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
try {
    $client.Connect(5000)
    $writer = New-Object System.IO.StreamWriter($client, (New-Object System.Text.UTF8Encoding($false)))
    $writer.WriteLine($request)
    $writer.Flush()
    $reader = New-Object System.IO.StreamReader($client, [System.Text.Encoding]::UTF8)
    $answer = $reader.ReadLine()
} catch {
    Fail ('could not reach the editor: ' + $_.Exception.Message)
} finally {
    $client.Dispose()
}

if (-not $answer) { Fail 'no answer from the editor' }
$parts = $answer.Split([char]9, 2)
$message = if ($parts.Count -gt 1) { $parts[1] } else { '' }
if ($parts[0] -eq 'ok') { [Console]::Out.WriteLine($message); exit 0 }
Fail $message
