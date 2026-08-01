# scripts/compilar_ninjascript.ps1 - compile-check de indicadores NinjaScript
# fuera de NT8, con las DLLs reales (comando validado en la sesion GhostLimit
# 2026-07-28, EXIT=0). NUNCA referenciar NinjaTrader.Client.dll (CS0433).
# Archivo en ASCII puro: PS 5.1 sin BOM lee ANSI y los acentos rompen el parser.
param(
    [Parameter(Mandatory = $true)][string]$Archivo,
    [switch]$Deploy
)

# Ruta absoluta con backslashes: csc interpreta los forward slashes de una
# ruta relativa como opciones (/nologo, /r:...) y no encuentra el archivo.
$Archivo = (Resolve-Path $Archivo -ErrorAction Stop).Path

$fw = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$nt = "C:\Program Files\NinjaTrader 8\bin"
$cu = "$env:USERPROFILE\Documents\NinjaTrader 8\bin\Custom"
$outDir = Join-Path $env:TEMP "nt8_check"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$nombre = [IO.Path]::GetFileNameWithoutExtension($Archivo)
& "$fw\csc.exe" /nologo /t:library /out:"$outDir\${nombre}_check.dll" `
    /r:"$nt\NinjaTrader.Core.dll" /r:"$nt\NinjaTrader.Gui.dll" /r:"$cu\NinjaTrader.Custom.dll" `
    /r:"$nt\SharpDX.dll" /r:"$nt\SharpDX.Direct2D1.dll" `
    /r:"$fw\WPF\PresentationCore.dll" /r:"$fw\WPF\PresentationFramework.dll" `
    /r:"$fw\WPF\WindowsBase.dll" /r:"$fw\System.Xaml.dll" `
    /r:"$fw\System.ComponentModel.DataAnnotations.dll" `
    $Archivo
$exit = $LASTEXITCODE
Write-Host "EXIT=$exit"

if ($exit -eq 0 -and $Deploy) {
    $destino = Join-Path "$cu\Indicators" ([IO.Path]::GetFileName($Archivo))
    # El hardlink se rompe en silencio: Edit/Write escriben a temporal y renombran
    # sobre el original, lo que crea un inodo NUEVO (incidente 29-jul: NT8 compilo
    # una copia vieja durante horas). Por eso se compara CONTENIDO, no existencia.
    $sano = $false
    if (Test-Path $destino) {
        $sano = -not (Compare-Object (Get-Content $Archivo) (Get-Content $destino))
    }
    if ($sano) {
        Write-Host "Deploy: $destino ya sincronizado"
    } else {
        if (Test-Path $destino) {
            Remove-Item $destino -Force
            Write-Host "Deploy: destino DESINCRONIZADO (hardlink roto) - re-enlazando"
        }
        New-Item -ItemType HardLink -Path $destino -Target $Archivo | Out-Null
        Write-Host "Deploy: hardlink creado -> $destino"
    }
}
exit $exit
