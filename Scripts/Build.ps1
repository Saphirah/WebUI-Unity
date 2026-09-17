param([string]$ArchivePath='',[switch]$HostOnly)
$ErrorActionPreference='Stop'
$Root=Split-Path $PSScriptRoot -Parent
$Package=Join-Path $Root 'Packages/com.webui.chromium'
$CEF=& "$PSScriptRoot/FetchCEF.ps1" -ArchivePath $ArchivePath
$Runtime=Join-Path $CEF 'Release'
$VsWhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$VS=& $VsWhere -latest -version '[17.0,18.0)' -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$VS){throw 'VS 2022 C++ tools are required'}
$DevCmd=Join-Path $VS 'Common7/Tools/VsDevCmd.bat'
$Lines=& cmd.exe /d /s /c "`"$DevCmd`" -no_logo -arch=x64 -host_arch=x64 >nul && set"
foreach($Line in $Lines){if($Line -match '^([^=]+)=(.*)$'){[Environment]::SetEnvironmentVariable($Matches[1],$Matches[2],'Process')}}
$Data=Join-Path $Package 'RuntimeData~'
$Out=Join-Path $Root 'Artifacts/Native'
New-Item -ItemType Directory -Force $Data,$Out,"$Data/BrowserResources","$Package/Plugins/x86_64" | Out-Null
$CMake=(Get-Command cmake.exe -ErrorAction SilentlyContinue).Source
if(!$CMake){$CMake=Join-Path $VS 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'}
if(!(Test-Path -LiteralPath $CMake)){throw 'Install CMake 3.21+ or the Visual Studio CMake tools'}
$WrapperBuild=Join-Path $Out 'CEFWrapper'
& $CMake -S $CEF -B $WrapperBuild -G 'Visual Studio 17 2022' -A x64 '-DUSE_SANDBOX=OFF' '-DCEF_RUNTIME_LIBRARY_FLAG=/MD' '-DCMAKE_POLICY_VERSION_MINIMUM=3.5'
if($LASTEXITCODE -ne 0){throw 'CEF wrapper configuration failed'}
& $CMake --build $WrapperBuild --target libcef_dll_wrapper --config Release --parallel 8
if($LASTEXITCODE -ne 0){throw 'CEF wrapper build failed'}
if(!$HostOnly){
    & cl.exe /nologo /std:c++20 /EHsc /MD /O2 /LD "$Root/Native/Plugin/UnityWebUI.cpp" "/Fo$Out/UnityWebUI.obj" "/Fe$Package/Plugins/x86_64/UnityWebUINative.dll" d3d11.lib d3d12.lib dxgi.lib advapi32.lib d3dcompiler.lib /link "/IMPLIB:$Out/UnityWebUINative.lib"
    if($LASTEXITCODE -ne 0){throw 'Native Unity adapter build failed'}
}
$Arguments=@('/nologo','/std:c++20','/EHsc','/MD','/O2','/DUNICODE','/D_UNICODE',"/I$CEF","/Fo$Out/WebUIHost.obj","/Fe$Data/WebUIHost.exe","$Root/Native/Host/WebUIHost.cpp",
 "$CEF/Release/libcef.lib","$WrapperBuild/libcef_dll_wrapper/Release/libcef_dll_wrapper.lib",'d3d11.lib','dxgi.lib','user32.lib','shell32.lib','shlwapi.lib','advapi32.lib','ole32.lib','delayimp.lib','/link','/SUBSYSTEM:WINDOWS','/DELAYLOAD:libcef.dll')
& cl.exe @Arguments
if($LASTEXITCODE -ne 0){throw 'CEF host build failed'}
New-Item -ItemType Directory -Force "$Data/CEF" | Out-Null
Get-ChildItem -LiteralPath $Runtime -File | Where-Object Extension -NotIn @('.lib','.pdb') | Copy-Item -Destination "$Data/CEF" -Force
Copy-Item -Path "$CEF/Resources/*" -Destination "$Data/CEF" -Recurse -Force
Copy-Item -Path "$Package/BrowserResources/*.js" -Destination "$Data/BrowserResources" -Force
Get-ChildItem -LiteralPath $CEF -Filter '*LICENSE*' -File | Copy-Item -Destination "$Data/CEF" -Force
$Bundle=Join-Path $Package 'Binaries~'
New-Item -ItemType Directory -Force $Bundle | Out-Null
Copy-Item -LiteralPath "$Data/WebUIHost.exe" -Destination "$Bundle/WebUIHost.exe" -Force
$Manifest=Get-Content "$PSScriptRoot/cef.lock.json" -Raw | ConvertFrom-Json
$Manifest | Add-Member -NotePropertyName hostSha256 -NotePropertyValue (Get-FileHash "$Bundle/WebUIHost.exe" -Algorithm SHA256).Hash.ToLowerInvariant() -Force
$Manifest | ConvertTo-Json | Set-Content "$Bundle/runtime.json"
@{cef=$CEF;runtime=$Runtime;built=(Get-Date).ToString('o');gpu='D3D11 keyed ring -> Unity texture (GPU copy)';cpu='BGRA shared memory -> Unity upload'}|ConvertTo-Json|Set-Content "$Out/Build.json"
Write-Output "Built Unity adapter and standalone CEF host: $Data"
