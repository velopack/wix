@echo off
setlocal

set _RID=win-x86
set _VCXPLATFORM=Win32
set _C=Release
if /i "%1"=="debug" set _C=Debug

set _SCRIPT_DIR=%~dp0
set _REPO_ROOT=%_SCRIPT_DIR%..
set _PUBLISH_DIR=%_SCRIPT_DIR%publish

set "_VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%_VSWHERE%" (
    echo ERROR: vswhere.exe not found. Install Visual Studio 2022.
    exit /b 1
)

set _MSBUILD=
for /f "usebackq tokens=*" %%i in (`"%_VSWHERE%" -latest -prerelease -products * -requires Microsoft.Component.MSBuild -requires Microsoft.VisualStudio.Workload.NativeDesktop -find MSBuild\**\Bin\MSBuild.exe`) do set "_MSBUILD=%%i"

if not defined _MSBUILD (
    echo ERROR: MSBuild + C++ workload not located by vswhere.
    echo Install "Desktop development with C++" via the Visual Studio Installer.
    exit /b 1
)
echo Using MSBuild: %_MSBUILD%

pushd "%_REPO_ROOT%"

if not exist Directory.Packages.props (
    echo === Bootstrapping package versions ===
    dotnet msbuild -Restore src\internal\SetBuildNumber\SomeVerInit.verproj -nologo
    if errorlevel 1 goto :err

    powershell -NoProfile -Command "(Get-Content global.json) -replace '\"latestFeature\"', '\"latestMajor\"' | Set-Content global.json"
)

if not exist build\artifacts md build\artifacts

call :pack_if_needed WixToolset.Data        src\api\wix\WixToolset.Data\WixToolset.Data.csproj
if errorlevel 1 goto :err
call :pack_if_needed WixToolset.Extensibility src\api\wix\WixToolset.Extensibility\WixToolset.Extensibility.csproj
if errorlevel 1 goto :err
call :pack_if_needed WixToolset.Versioning  src\libs\WixToolset.Versioning\WixToolset.Versioning.csproj
if errorlevel 1 goto :err

call :build_dutil_if_needed
if errorlevel 1 goto :err

if not exist "build\wix\%_C%\x86\wixnative.exe" (
    echo === Building wixnative ^(C++^) ===
    "%_MSBUILD%" src\wix\wixnative\wixnative.vcxproj -t:Build -p:Configuration=%_C% -p:Platform=%_VCXPLATFORM% -restore -nologo -v:m
    if errorlevel 1 goto :err
)

echo === Publishing wixc7.exe ===
dotnet publish wixc\wixc.csproj -c %_C% -r %_RID% --self-contained true -o "%_PUBLISH_DIR%" -p:NCrunch=1 --nologo
if errorlevel 1 goto :err

copy /y "build\wix\%_C%\x86\wixnative.exe" "%_PUBLISH_DIR%\wixnative.exe" >nul
if errorlevel 1 (
    echo ERROR: Failed to copy wixnative.exe
    goto :err
)
copy /y "src\wix\wixnative\%_VCXPLATFORM%\mergemod.dll" "%_PUBLISH_DIR%\mergemod.dll" >nul
if errorlevel 1 (
    echo ERROR: Failed to copy mergemod.dll
    goto :err
)

popd
echo.
echo Done. Output in %_PUBLISH_DIR%
endlocal
exit /b 0

:build_dutil_if_needed
if exist "build\artifacts\WixToolset.DUtil.*.nupkg" exit /b 0

echo === Building DUtil ^(C++ static lib^) ===
"%_MSBUILD%" src\libs\dutil\WixToolset.DUtil\dutil.vcxproj -t:Build -p:Configuration=%_C% -p:Platform=Win32 -restore -nologo -v:m
if errorlevel 1 exit /b 1
"%_MSBUILD%" src\libs\dutil\WixToolset.DUtil\dutil.vcxproj -t:Build -p:Configuration=%_C% -p:Platform=x64 -restore -nologo -v:m
if errorlevel 1 exit /b 1

rem ARM64 build tools may not be installed; create a placeholder so the nuspec pack succeeds.
rem Only x86 is used by wixnative in our build.
"%_MSBUILD%" src\libs\dutil\WixToolset.DUtil\dutil.vcxproj -t:Build -p:Configuration=%_C% -p:Platform=ARM64 -restore -nologo -v:m 2>nul
if errorlevel 1 (
    echo    ARM64 build tools not available, using x86 lib as placeholder.
    if not exist "build\libs\%_C%\v143\ARM64" md "build\libs\%_C%\v143\ARM64"
    copy /y "build\libs\%_C%\v143\x86\dutil.lib" "build\libs\%_C%\v143\ARM64\dutil.lib" >nul
)

echo === Packing WixToolset.DUtil ===
"%_MSBUILD%" src\libs\dutil\WixToolset.DUtil\dutil.vcxproj -t:PackNative -p:Configuration=%_C% -p:Platform=Win32 -nologo -v:m
exit /b %errorlevel%

:pack_if_needed
if not exist "build\artifacts\%~1.*.nupkg" (
    echo === Packing %~1 ===
    dotnet pack %~2 -c %_C% --nologo -v:q
)
exit /b %errorlevel%

:err
popd
endlocal
exit /b 1
