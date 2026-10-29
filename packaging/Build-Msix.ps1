param(
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0.0',
    [string]$Publisher = 'CN=TzachyFridman'
)

$ErrorActionPreference = 'Stop'
if (($Version.Split('.') | Where-Object { [long]$_ -gt 65535 }).Count -gt 0) {
    throw 'Each MSIX version component must be between 0 and 65535.'
}
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'artifacts\msix'
$stage = Join-Path $output "stage-$([guid]::NewGuid().ToString('N'))"
$sdkBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
$makeappx = Get-ChildItem -LiteralPath $sdkBin -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $makeappx) { throw 'Install the Windows SDK with MakeAppx to build the MSIX.' }

New-Item -ItemType Directory -Force -Path $stage | Out-Null
try {
    & dotnet publish (Join-Path $root 'src\LightCraftMidi\LightCraftMidi.csproj') `
        -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false `
        -p:DebugType=None -p:DebugSymbols=false -o $stage --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

    [xml]$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
    $manifest.Package.Identity.Version = $Version
    $manifest.Package.Identity.Publisher = $Publisher
    $manifest.Save((Join-Path $stage 'AppxManifest.xml'))

    Add-Type -AssemblyName System.Drawing
    $assets = New-Item -ItemType Directory -Path (Join-Path $stage 'Assets')
    foreach ($asset in @(
        @{ Name = 'StoreLogo'; Size = 50 },
        @{ Name = 'Square44x44Logo'; Size = 44 },
        @{ Name = 'Square150x150Logo'; Size = 150 }
    )) {
        $bitmap = [System.Drawing.Bitmap]::new($asset.Size, $asset.Size)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $track = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(110, 116, 128), 2)
        $knob = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 167, 38))
        try {
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.Clear([System.Drawing.Color]::FromArgb(32, 36, 44))
            $scale = $asset.Size / 32.0
            $graphics.ScaleTransform($scale, $scale)
            $xs = @(9, 16, 23)
            $ys = @(19, 10, 15)
            for ($i = 0; $i -lt 3; $i++) {
                $graphics.DrawLine($track, $xs[$i], 6, $xs[$i], 26)
                $graphics.FillRectangle($knob, [single]($xs[$i] - 3.5), [single]($ys[$i] - 2.5), [single]7, [single]5)
            }
            $bitmap.Save((Join-Path $assets.FullName ($asset.Name + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $knob.Dispose()
            $track.Dispose()
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }

    $package = Join-Path $output "LightCraftMidi_${Version}_x64.msix"
    $packOutput = & $makeappx pack /d $stage /p $package /o 2>&1
    if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed ($LASTEXITCODE).`n$($packOutput -join "`n")" }
    Get-FileHash -LiteralPath $package -Algorithm SHA256 |
        ForEach-Object { "$($_.Hash)  $([IO.Path]::GetFileName($package))" } |
        Set-Content -LiteralPath ($package + '.sha256') -Encoding ascii
    Write-Host "Created unsigned package: $package"
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
