$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$project = Join-Path $PSScriptRoot 'ClassicLegacyUI\ClassicLegacyUI.csproj'
dotnet build $project -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
$dll = Join-Path $PSScriptRoot 'ClassicLegacyUI\bin\Release\net10.0\ClassicLegacyUI.dll'
$outputDir = Join-Path $PSScriptRoot 'dist'
[IO.Directory]::CreateDirectory($outputDir) | Out-Null
$output = Join-Path $outputDir "ClassicLegacyUI-$version-local.zip"
$temporary = "$output.tmp"
$manifest = [ordered]@{
    name = 'ClassicLegacyUI'
    author = 'markn2000'
    version_number = $version
    description = 'Brings a classic look to Legacy UI you create through Create New.'
    website_url = ''
    dependencies = @()
} | ConvertTo-Json

# 作成と検査が成功するまで、既存の完成済みZIPは置き換えない。
try {
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::Create)
    $zip = $null
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $dll, 'plugins/ClassicLegacyUI.dll') | Out-Null
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $PSScriptRoot 'README.md'), 'README.md') | Out-Null
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $PSScriptRoot 'LICENSE'), 'LICENSE') | Out-Null
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path $PSScriptRoot 'icon.png'), 'icon.png') | Out-Null
        $writer = [IO.StreamWriter]::new($zip.CreateEntry('manifest.json').Open(), [Text.UTF8Encoding]::new($false))
        try { $writer.Write($manifest) } finally { $writer.Dispose() }
    } finally { if ($zip) { $zip.Dispose() }; $stream.Dispose() }

    $zip = [IO.Compression.ZipFile]::OpenRead($temporary)
    try {
        $expected = @('README.md', 'LICENSE', 'manifest.json', 'icon.png', 'plugins/ClassicLegacyUI.dll')
        if (Compare-Object $expected @($zip.Entries.FullName)) { throw 'ZIP entries do not match the expected files.' }
        $reader = [IO.StreamReader]::new($zip.GetEntry('manifest.json').Open())
        try { $metadata = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($metadata.name -ne 'ClassicLegacyUI' -or $metadata.version_number -ne $version -or $metadata.dependencies.Count -ne 0) {
            throw 'Manifest metadata does not match the expected values.'
        }
        $entryStream = $zip.GetEntry('plugins/ClassicLegacyUI.dll').Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($entryStream)).Replace('-', '') }
        finally { $entryStream.Dispose(); $sha.Dispose() }
        if ($hash -ne (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash) { throw 'The DLL in the ZIP does not match the build output.' }
    } finally { $zip.Dispose() }
    Move-Item -LiteralPath $temporary -Destination $output -Force
    Write-Output "Package created and verified: $output"
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
