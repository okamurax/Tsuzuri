# AiLink: SSH_ASKPASS for the AI pane (AiLink SPEC).
# Prints the password of the config.txt entry named by AILINK_SSH_HOST, so that the AI never sees or types the password.
# config.txt is the copy AiLink makes (..\ssh\config.txt next to this folder). One entry per line:
#   name | host:port | auth=password | user=xxxx | passwd=xxxx
# Keep this file ASCII only: PowerShell 5.1 reads a script without BOM as the ANSI code page.

$name = $env:AILINK_SSH_HOST
if (-not $name) { [Console]::Error.WriteLine('AILINK_SSH_HOST is not set'); exit 1 }

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
