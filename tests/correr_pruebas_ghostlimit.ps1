# Compila GhostLimit.cs y corre el arnés de pruebas SIN abrir NinjaTrader.
# Uso:  powershell -File scripts\ninjascript\tests\correr_pruebas_ghostlimit.ps1
# Exit: 0 = todo verde | 1 = alguna prueba roja | 2 = error de compilación

$ErrorActionPreference = "Stop"

$fw  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$nt  = "C:\Program Files\NinjaTrader 8\bin"
$cu  = "$env:USERPROFILE\Documents\NinjaTrader 8\bin\Custom"
$src = Join-Path $PSScriptRoot "..\src"
$out = Join-Path $env:TEMP "ghostlimit_tests"

if (-not (Test-Path $out)) { New-Item -ItemType Directory -Force $out | Out-Null }

$refs = @(
    "$nt\NinjaTrader.Core.dll"
    "$nt\NinjaTrader.Gui.dll"
    "$cu\NinjaTrader.Custom.dll"
    "$nt\SharpDX.dll"
    "$nt\SharpDX.Direct2D1.dll"
    "$fw\WPF\PresentationCore.dll"
    "$fw\WPF\PresentationFramework.dll"
    "$fw\WPF\WindowsBase.dll"
    "$fw\System.Xaml.dll"
    "$fw\System.ComponentModel.DataAnnotations.dll"
) | ForEach-Object { "/r:$_" }

# NOTA: NinjaTrader.Client.dll NO se referencia — duplica tipos de Core (CS0433).

$dll = Join-Path $out "GhostLimit.dll"
& "$fw\csc.exe" /nologo /t:library /out:$dll $refs (Join-Path $src "GhostLimit.cs")
if ($LASTEXITCODE -ne 0) { Write-Host "Fallo la compilacion del indicador."; exit 2 }

# /r:GL=... crea el alias externo que usa el arnés: sin él, el GhostLimit del repo
# y el que NT8 ya compiló dentro de NinjaTrader.Custom.dll chocan (CS0433).
$exe = Join-Path $out "GhostLimitTests.exe"
& "$fw\csc.exe" /nologo /t:exe /out:$exe "/r:GL=$dll" $refs (Join-Path $PSScriptRoot "GhostLimitTests.cs")
if ($LASTEXITCODE -ne 0) { Write-Host "Fallo la compilacion del arnes."; exit 2 }

& $exe
exit $LASTEXITCODE
