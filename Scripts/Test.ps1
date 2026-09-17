param([string]$Editor='E:/Unity/Hub/Editor/6000.4.0f1/Editor/Unity.exe',[switch]$CPU)
$ErrorActionPreference='Stop';$Root=Split-Path $PSScriptRoot -Parent
$Name=if($CPU){'cpu'}else{'gpu'};$Stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
New-Item -ItemType Directory -Force "$Root/Artifacts" | Out-Null
$Report="$Root/Artifacts/$Name-$Stamp.json";$Log="$Root/Artifacts/$Name-$Stamp.log"
$Args=@('-batchmode','-force-d3d11','-projectPath',('"'+$Root+'/TestProject"'),'-executeMethod','WebUI.Editor.Validation.Run','-webui-test',('-webui-report="'+$Report+'"'),'-logFile',('"'+$Log+'"'))
if($CPU){$Args+='-webui-cpu'}
$Process=Start-Process $Editor -ArgumentList $Args -WindowStyle Hidden -PassThru
if(!$Process.WaitForExit(240000)){Stop-Process -Id $Process.Id;throw "Unity timed out: $Log"}
if($Process.ExitCode -ne 0){throw "Unity failed ($($Process.ExitCode)): $Log"}
if(!(Test-Path -LiteralPath $Report)){throw "No test report: $Log"}
$Result=Get-Content -LiteralPath $Report -Raw|ConvertFrom-Json;if(!$Result.pass){throw "Validation failed: $Report"}
Get-Content -LiteralPath $Report -Raw
