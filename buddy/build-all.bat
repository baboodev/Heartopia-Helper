@echo off
setlocal
cd /d "%~dp0"

echo Building MelonLoader...
dotnet build buddy.csproj -c Release -p:Loader=MelonLoader
if errorlevel 1 exit /b 1

echo.
echo Building BepInEx...
dotnet build buddy.csproj -c Release -p:Loader=BepInEx
if errorlevel 1 exit /b 1

echo.
echo Done.
echo   MelonLoader: bin\MelonLoader\Release\buddy.dll  -^> copy to Mods\
echo   BepInEx:     bin\BepInEx\Release\buddy.dll      -^> copy to BepInEx\plugins\
