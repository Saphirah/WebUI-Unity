param([string]$ArchivePath='')
$ErrorActionPreference='Stop'
$Root=Split-Path $PSScriptRoot -Parent
$Lock=Get-Content (Join-Path $PSScriptRoot 'cef.lock.json') -Raw | ConvertFrom-Json
$Cache=Join-Path $Root 'Artifacts/CEF'
New-Item -ItemType Directory -Force $Cache | Out-Null
$Archive=if($ArchivePath){(Resolve-Path -LiteralPath $ArchivePath).Path}else{Join-Path $Cache $Lock.archive}
if(!(Test-Path -LiteralPath $Archive)){
    $Partial=$Archive+'.partial'
    & curl.exe --fail --location --silent --show-error --retry 3 --connect-timeout 30 --max-time 1800 --output $Partial $Lock.url
    if($LASTEXITCODE -ne 0){throw 'CEF download failed; rerun or supply -ArchivePath for an offline archive'}
    if((Get-Item -LiteralPath $Partial).Length -ne $Lock.size -or (Get-FileHash -LiteralPath $Partial -Algorithm SHA256).Hash -ne $Lock.sha256){throw 'CEF archive failed checksum verification'}
    Move-Item -LiteralPath $Partial -Destination $Archive -Force
}
if((Get-Item -LiteralPath $Archive).Length -ne $Lock.size -or (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash -ne $Lock.sha256){throw 'CEF archive does not match cef.lock.json; replace the archive and retry'}
$DirectoryName=$Lock.archive.Replace('.tar.bz2','')
$SDK=Join-Path $Cache $DirectoryName
if(!(Test-Path -LiteralPath (Join-Path $SDK '.webui-extracted'))){
    & tar.exe -xf $Archive -C $Cache
    if($LASTEXITCODE -ne 0){throw 'CEF extraction failed'}
    if(!(Test-Path -LiteralPath "$SDK/include/cef_version.h")){throw "Unexpected CEF archive layout: $SDK"}
    Set-Content -LiteralPath (Join-Path $SDK '.webui-extracted') -Value $Lock.sha1
}
Write-Output $SDK
