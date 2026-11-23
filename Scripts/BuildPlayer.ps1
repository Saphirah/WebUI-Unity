param([string]$Editor='E:/Unity/Hub/Editor/6000.4.0f1/Editor/Unity.exe',[switch]$CPU,[switch]$Composition)
$ErrorActionPreference='Stop';$Root=Split-Path $PSScriptRoot -Parent
if($Composition -and $CPU){throw 'DirectComposition requires GPU rendering'}
$Name=if($Composition){'Composition'}elseif($CPU){'CPU'}else{'GPU'};$Output="$Root/Artifacts/Player$Name/WebUIDemo.exe"
New-Item -ItemType Directory -Force (Split-Path $Output) | Out-Null
$Arguments=@('-batchmode','-quit','-force-d3d11','-projectPath',('"'+$Root+'/TestProject"'),'-executeMethod','WebUI.Editor.Validation.BuildPlayer',('-webui-player="'+$Output+'"'),'-logFile',('"'+$Root+'/Artifacts/Player'+$Name+'-Build.log"'))
if($CPU){$Arguments+='-webui-cpu'}
if($Composition){$Arguments+='-webui-composition'}
$Process=Start-Process $Editor -ArgumentList $Arguments -WindowStyle Hidden -PassThru
if(!$Process.WaitForExit(300000)){Stop-Process -Id $Process.Id;throw 'Unity player build timed out'}
if($Process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $Output)){throw "Player build failed; see Artifacts/Player$Name-Build.log"}
Write-Output $Output
