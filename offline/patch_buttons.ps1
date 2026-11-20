# Turns off the buttons that would open the official website or the support page (they cannot work offline):
#   title screen menu: 公式ページ, お問い合わせ / in-game メニュー: 公式ページ, お問い合わせ / window title bar: 公式ページ
# Each button is switched to "not interactive" in 7rhythm_Data\resources.assets (one byte per button).
# Run with -Undo to turn them back on. The first patch saves the original file as resources.assets.orig.
param([string]$GameDir = (Split-Path -Parent $PSScriptRoot), [switch]$Undo)

$f = Join-Path $GameDir '7rhythm_Data\resources.assets'
$backup = $f + '.orig'
$offsets = @(50782696, 50665288, 49972296, 49698584, 50342664)

if (-not (Test-Path $f)) { Write-Host 'BUTTONS resources.assets not found - buttons not patched'; exit 1 }
if ((Get-Item $f).Length -ne 51417876) { Write-Host 'BUTTONS resources.assets is from a different game version - buttons not patched'; exit 1 }

$want = if ($Undo) { 1 } else { 0 }
$s = [IO.File]::Open($f, 'Open', 'Read', 'ReadWrite')
$values = foreach ($o in $offsets) { $s.Position = $o; $s.ReadByte() }
$s.Close()
foreach ($v in $values) { if ($v -ne 0 -and $v -ne 1) { Write-Host 'BUTTONS resources.assets does not match the expected game version - buttons not patched'; exit 1 } }
if (-not ($values | Where-Object { $_ -ne $want })) {
    if ($Undo) { Write-Host 'BUTTONS website buttons are already enabled' } else { Write-Host 'BUTTONS website buttons already disabled' }
    exit 0
}

try {
    if (-not $Undo -and -not (Test-Path $backup)) { Copy-Item $f $backup; Write-Host 'BUTTONS original saved as resources.assets.orig' }
    $s = [IO.File]::Open($f, 'Open', 'ReadWrite')
    foreach ($o in $offsets) { $s.Position = $o; $s.WriteByte($want) }
    $s.Close()
    if ($Undo) { Write-Host 'BUTTONS website buttons enabled again (original state)' }
    else { Write-Host 'BUTTONS website buttons disabled (title menu, in-game menu, window title bar)' }
}
catch { Write-Host ('BUTTONS could not patch resources.assets: ' + $_.Exception.Message + ' (is the game still running?)'); exit 1 }
