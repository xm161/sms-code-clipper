param(
    [ValidateSet('build','window','devices','listen','selftest','window-selftest','test')][string]$Mode = 'build',
    [string]$DeviceId,
    [ValidateRange(0,120)][int]$Seconds = 45
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$publishDir = Join-Path $projectRoot 'dist'
$readerDir = Join-Path $publishDir 'ancs-reader'
$readerExe = Join-Path $readerDir 'AncsReader.exe'
$windowDir = Join-Path $publishDir 'clipper-window'
$windowExe = Join-Path $windowDir 'ClipperWindow.exe'
$backendExe = Join-Path $publishDir 'SmsCodeClipper.exe'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if ($Mode -in @('build','window','test')) {
    $bundledSdk = Join-Path $projectRoot 'build/dotnet/dotnet.exe'
    $dotnetExe = if ($env:SMS_CODE_CLIPPER_DOTNET) { $env:SMS_CODE_CLIPPER_DOTNET } elseif (Test-Path -LiteralPath $bundledSdk) { $bundledSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
    $env:DOTNET_ROOT = Split-Path $dotnetExe -Parent
    if (Test-Path -LiteralPath $bundledSdk) {
        $env:DOTNET_CLI_HOME = Join-Path $projectRoot 'build/dotnet-home'
        $env:NUGET_PACKAGES = Join-Path $projectRoot 'build/nuget'
    }
}
function Publish-Project([string]$Project, [string]$Destination) {
    $projectFile = Join-Path $projectRoot $Project
    & $dotnetExe publish $projectFile -c Release -r win-x64 --self-contained true -o $Destination --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $Project" }
    $assets = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path (Split-Path $projectFile) 'obj/project.assets.json') | ConvertFrom-Json
    $name = [IO.Path]::GetFileNameWithoutExtension($projectFile)
    $deps = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $Destination "$name.deps.json") | ConvertFrom-Json
    $roots = @($assets.packageFolders.PSObject.Properties.Name) + @(Join-Path $env:DOTNET_ROOT 'packs')
    foreach ($library in $deps.libraries.PSObject.Properties) {
        if ($library.Value.type -eq 'project') { continue }
        $package = $library.Name -replace '^runtimepack\.', ''
        $included = $false
        foreach ($root in $roots) {
            $path = Join-Path $root $package
            if (!(Test-Path -LiteralPath $path -PathType Container)) { continue }
            $target = Join-Path (Join-Path $publishDir 'licenses') $package
            $notices = @(Get-ChildItem -LiteralPath $path -File | Where-Object { $_.Name -match '^(LICENSE|LICENCE|THIRD-PARTY-NOTICES|NOTICE)(\..+)?$' })
            if ($notices.Count) {
                [IO.Directory]::CreateDirectory($target) | Out-Null
                foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination $target -Force }
                $included = $true
                break
            }
            # The Windows SDK projection package supplies its license URL in the nuspec.
            if ($package -like 'Microsoft.Windows.SDK.NET.Ref/*') {
                $spec = Join-Path $path 'Microsoft.Windows.SDK.NET.Ref.nuspec'
                if (!(Test-Path -LiteralPath $spec)) { continue }
                $metadata = ([xml](Get-Content -Raw -Encoding UTF8 -LiteralPath $spec)).package.metadata
                if (!$metadata.licenseUrl) { continue }
                [IO.Directory]::CreateDirectory($target) | Out-Null
                $text = "$package`r`n$($metadata.copyright)`r`nLicense: $($metadata.licenseUrl)`r`n"
                [IO.File]::WriteAllText((Join-Path $target 'LICENSE-URL.txt'), $text, [Text.UTF8Encoding]::new($false))
                $included = $true
                break
            }
        }
        if (!$included) { throw "No distribution notices found for $package" }
    }
}
function Test-Window {
    $process = Start-Process -FilePath $windowExe -ArgumentList '--selftest' -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw 'Window self-test failed' }
}
switch ($Mode) {
    'build' {
        Publish-Project 'src/AncsReader/AncsReader.csproj' $readerDir
        Publish-Project 'src/ClipperWindow/ClipperWindow.csproj' $windowDir
        Publish-Project 'src/SmsCodeClipper/SmsCodeClipper.csproj' $publishDir
        $launch = @"
Set fs = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("WScript.Shell")
base = fs.GetParentFolderName(WScript.ScriptFullName)
sh.CurrentDirectory = base
sh.Run Chr(34) & fs.BuildPath(base, "SmsCodeClipper.exe") & Chr(34) & " --open", 0, False
"@
        [IO.File]::WriteAllText((Join-Path $publishDir 'launch.vbs'), $launch, [Text.UTF8Encoding]::new($false))
    }
    'window' { Publish-Project 'src/ClipperWindow/ClipperWindow.csproj' $windowDir }
    'window-selftest' { Test-Window }
    'devices' { & $readerExe ancs-devices; if ($LASTEXITCODE -ne 0) { throw 'Device discovery failed' } }
    'selftest' { & $readerExe ancs-selftest; if ($LASTEXITCODE -ne 0) { throw 'Reader self-test failed' } }
    'test' {
        & $dotnetExe run --project (Join-Path $projectRoot 'tests/SmsCodeClipper.Tests.csproj') -c Release -- --backend $backendExe
        if ($LASTEXITCODE -ne 0) { throw 'Backend regression tests failed' }
        & $readerExe ancs-selftest
        if ($LASTEXITCODE -ne 0) { throw 'Reader self-test failed' }
        Test-Window
    }
    'listen' {
        if (-not $DeviceId) { throw 'Supply an exact paired BLE ID from devices.' }
        & $readerExe ancs $DeviceId $Seconds
        if ($LASTEXITCODE -ne 0) { throw 'Reader exited with an error' }
    }
}
