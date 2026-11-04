[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$sdk = dotnet --list-sdks |
    Where-Object { $_ -match '^8\.\d+\.\d+ \[.*\]$' } |
    Sort-Object { [version](($_ -split ' ', 2)[0]) } -Descending |
    Select-Object -First 1
if (!$sdk -or $sdk -notmatch '^(\S+) \[(.*)\]$') {
    throw 'Install the .NET 8 SDK before building.'
}
$compiler = Join-Path $Matches[2] "$($Matches[1])\Roslyn\bincore\csc.dll"
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (!(Test-Path -LiteralPath $framework)) {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$references = @(
    'mscorlib.dll', 'System.dll', 'System.Core.dll', 'WPF\PresentationFramework.dll',
    'WPF\PresentationCore.dll', 'WPF\WindowsBase.dll', 'WPF\UIAutomationTypes.dll',
    'System.Xaml.dll', 'System.Runtime.Serialization.dll', 'System.Xml.dll'
) | ForEach-Object { '/r:' + (Join-Path $framework $_) }

Push-Location $PSScriptRoot
try {
    New-Item -ItemType Directory -Path 'dist' -Force | Out-Null
    foreach ($app in @(
        @{ Name = 'VonalasFuzet'; Source = 'Fuzet.cs' },
        @{ Name = 'KockasFuzet'; Source = 'KockasFuzet.cs' }
    )) {
        $name = $app.Name
        $source = $app.Source
        dotnet exec $compiler /nologo /noconfig /nostdlib /codepage:65001 /target:winexe /optimize+ /deterministic `
            @references "/win32icon:assets\$name.ico" "/resource:$source,$name.$source" "/out:dist\$name.exe" $source
        if ($LASTEXITCODE -ne 0) { throw "Build failed for $name." }
    }
}
finally {
    Pop-Location
}
