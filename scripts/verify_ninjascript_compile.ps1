$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path $projectRoot '.pytest_cache'
$sources = @(
    (Join-Path $projectRoot 'strategies\MesOrbStructureV1.cs'),
    (Join-Path $projectRoot 'strategies\MesOrbPullbackV2.cs')
)
$customRoot = Join-Path $env:USERPROFILE 'Documents\NinjaTrader 8\bin\Custom'
$ninjaBin = 'C:\Program Files\NinjaTrader 8\bin'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'

$references = @(
    (Join-Path $ninjaBin 'NinjaTrader.Core.dll'),
    (Join-Path $ninjaBin 'NinjaTrader.Gui.dll'),
    (Join-Path $customRoot 'NinjaTrader.Custom.dll'),
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.dll',
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Core.dll',
    'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.ComponentModel.DataAnnotations.dll',
    'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll',
    'C:\Windows\Microsoft.NET\assembly\GAC_64\PresentationCore\v4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll',
    'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\PresentationFramework\v4.0_4.0.0.0__31bf3856ad364e35\PresentationFramework.dll',
    'C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Xaml\v4.0_4.0.0.0__b77a5c561934e089\System.Xaml.dll'
)

$requiredPaths = @($compiler) + $sources + $references
foreach ($path in $requiredPaths) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required compile dependency not found: $path"
    }
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
foreach ($source in $sources) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($source)
    $output = Join-Path $outputDirectory "$name.syntax.dll"
    $arguments = @('/nologo', '/target:library', '/platform:x64', "/out:$output")
    $arguments += $references | ForEach-Object { "/reference:$_" }
    $arguments += $source

    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "NinjaScript API compile failed for $name with exit code $LASTEXITCODE"
    }
    Write-Output "NinjaScript API compile passed: $output"
}
