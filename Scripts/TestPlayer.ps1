param([switch]$CPU,[switch]$Resize,[switch]$Composition)
$ErrorActionPreference='Stop';$Root=Split-Path $PSScriptRoot -Parent;$Name=if($CPU){'CPU'}else{'GPU'}
if($Composition -and $CPU){throw 'DirectComposition requires GPU rendering'}
if($Composition){$Name='Composition'}
$Exe="$Root/Artifacts/Player$Name/WebUIDemo.exe";$Stamp=Get-Date -Format 'yyyyMMdd-HHmmss';$Report="$Root/Artifacts/Player$Name-$Stamp.json"
$Arguments=@('-batchmode','-force-d3d11','-webui-test',('-webui-report="'+$Report+'"'),'-logFile',('"'+$Root+'/Artifacts/Player'+$Name+'-'+$Stamp+'.log"'))
if($Composition){$Arguments=@($Arguments | Where-Object {$_ -ne '-batchmode'})+@('-webui-composition','-screen-fullscreen','0')}
if($Resize){$Arguments+=@('-webui-resize-test','-screen-width','1280','-screen-height','720','-screen-fullscreen','0')}
$Process=Start-Process $Exe -ArgumentList $Arguments -WindowStyle Hidden -PassThru
if(!$Process.WaitForExit(90000)){Stop-Process -Id $Process.Id;throw 'Player validation timed out'}
if($Process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $Report)){throw "Player validation failed: $Report"}
$Result=Get-Content $Report -Raw|ConvertFrom-Json;if(!$Result.pass){throw "Player checks failed: $Report"};Get-Content $Report -Raw
