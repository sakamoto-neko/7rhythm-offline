# Starts the 7RHYTHM offline server (game API, 7BEAT login, asset server). Launched by launch_offline.bat.
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$gameDir = Split-Path -Parent $here
$Host.UI.RawUI.WindowTitle = "7RHYTHM Offline Server"
try {
  Add-Type -AssemblyName System.Web.Extensions
  $jsAsm = [System.Web.Script.Serialization.JavaScriptSerializer].Assembly.Location
  Add-Type -Path @((Join-Path $here "OfflineServer.cs"), (Join-Path $here "LocalAddresses.cs"), (Join-Path $here "AllCards.cs"), (Join-Path $here "AssetServer.cs")) -ReferencedAssemblies $jsAsm
  $mstage = Get-ChildItem -Path (Join-Path $gameDir "cdn\Files") -Recurse -Filter "M_stage.json" -ErrorAction SilentlyContinue | Select-Object -First 1
  $mstagePath = if ($mstage) { $mstage.FullName } else { "" }
  $tables = @("M_card","M_rarity","M_card_level","M_card_level_type","M_card_type","M_card_group","M_card_status","M_card_evolution","M_card_grow_type","M_skill","M_skill_level_up","M_skill_level_up_specific_card","M_leader_skill","M_character","M_attr","M_episode","M_episode_reward","M_episode_event_challenge","M_award","M_user_level","M_const")
  [OfflineServer]::DumpTables($here, (Join-Path $gameDir "cdn\Files"), (Join-Path $here "tables"), $tables)
  [OfflineServer]::CdnFilesDir = (Join-Path $gameDir "cdn\Files")
  [OfflineServer]::Run($here, 45558, 45559, $mstagePath, $true)
} catch {
  Write-Host $_ -ForegroundColor Red
  "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  STARTUP ERROR: $_" | Add-Content (Join-Path $here "logs\server.log")
  Read-Host "Press Enter to close"
}
