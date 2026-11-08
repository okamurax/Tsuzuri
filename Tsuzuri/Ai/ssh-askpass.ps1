# Tsuzuri: SSH_ASKPASS for the AI pane (SPEC 19).
# Prints the password of the config.txt entry named by TSUZURI_SSH_HOST, so that Claude never sees or types the password.
# config.txt is the copy the editor makes (..\ssh\config.txt next to this folder). One entry per line:
#   name | host:port | auth=password | user=xxxx | passwd=xxxx
# Keep this file ASCII only: PowerShell 5.1 reads a script without BOM as the ANSI code page.

$name = $env:TSUZURI_SSH_HOST
if (-not $name) { [Console]::Error.WriteLine('TSUZURI_SSH_HOST is not set'); exit 1 }

$config = Join-Path (Split-Path $PSScriptRoot -Parent) 'ssh\config.txt'
foreach ($line in [System.IO.File]::ReadAllLines($config)) {
    $fields = $line.Split('|')
    if ($fields.Count -lt 2 -or $fields[0].Trim() -ne $name.Trim()) { continue }
    foreach ($field in $fields) {
        $value = $field.Trim()
        if ($value.StartsWith('passwd=')) { [Console]::Out.WriteLine($value.Substring(7)); exit 0 }
    }
}

[Console]::Error.WriteLine('no passwd for "' + $name + '" in config.txt')
exit 1
