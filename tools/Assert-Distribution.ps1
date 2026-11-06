#requires -Version 7.0
[CmdletBinding(DefaultParameterSetName = 'Output')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Source')]
    [switch]$CheckSource,
    [Parameter(Mandatory, ParameterSetName = 'Output')]
    [string]$OutputDirectory,
    [Parameter(ParameterSetName = 'Output')]
    [switch]$NativeAot,
    [Parameter(ParameterSetName = 'Output')]
    [string]$ManifestPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content -LiteralPath (Join-Path $repoRoot 'LinxLauncher.WinUI.csproj') -Raw
$publicFiles = @($project.SelectNodes('//LauncherAsset | //LauncherPublicDocument') | ForEach-Object {
    $_.GetAttribute('Include').Replace('\', '/')
})
if ($publicFiles.Count -eq 0 -or ($publicFiles | Where-Object { $_ -match '[*?;]|\$|^/|(^|/)\.\.(/|$)' })) {
    throw 'The project must declare an explicit public file list.'
}

function Test-PrivatePath([string]$Path) {
    return $Path -match '(?i)do[ -]?not[ -]?submit|请勿提交|(^|/)(internal[^/]*|research|screenshots|samples|testdata|artifacts|tests|bin|obj|objc|publish|out|\.git|\.vs|\.obsidian)(/|$)|(^|/)AGENTS?\.md$|(^|/)LocalSecrets\.cs$|\.(zip|7z|nupkg|snupkg|sln|slnx|slnf|user|tmp|bak|log|binlog|litematic|schematic|schem|nbt)$'
}

if ($CheckSource) {
    $tracked = @(& git -C $repoRoot -c core.quotepath=false ls-files)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate tracked source files.' }
    $violations = @($tracked | Where-Object { Test-PrivatePath $_ })
    foreach ($path in $tracked) {
        if (($path -match '(?i)^docs/' -and $path -notin $publicFiles) -or
            ($path -match '(?i)^tools/' -and $path -ne 'tools/Assert-Distribution.ps1')) {
            $violations += $path
        }
        if ($path -match '\.(md|txt)$' -and (Test-Path -LiteralPath (Join-Path $repoRoot $path))) {
            if (Select-String -LiteralPath (Join-Path $repoRoot $path) -Pattern '(?i)DO[ -]?NOT[ -]?SUBMIT|请勿提交' -Quiet) {
                $violations += $path
            }
        }
        if ($path -match '(?i)\.(png|jpe?g|gif|webp|bmp|ico|svg)$' -and $path -notin $publicFiles) {
            $violations += $path
        }
    }
    if ($violations.Count) { throw "Private or unapproved source files are tracked:`n$($violations | Sort-Object -Unique | Out-String)" }
    Write-Host "Source isolation passed: $($tracked.Count) tracked files."
    return
}

$root = (Resolve-Path -LiteralPath $OutputDirectory).Path
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'OutputDirectory must be a directory.' }
# Do not follow links into local research or external directories.
$entries = @(Get-ChildItem -LiteralPath $root -Recurse -Force)
if ($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
    throw 'Distribution output must not contain symbolic links or junctions.'
}
$files = @($entries | Where-Object { -not $_.PSIsContainer })
if ($files.Count -eq 0) { throw 'Distribution output is empty.' }
$manifest = @($files | ForEach-Object {
    [pscustomobject]@{
        Path = [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/')
        Length = $_.Length
        SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
} | Sort-Object Path)
if ($ManifestPath) {
    $manifestFullPath = [IO.Path]::GetFullPath($ManifestPath)
    if ($manifestFullPath.StartsWith($root.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Write the inventory outside the distribution directory.'
    }
    $manifest | Export-Csv -LiteralPath $manifestFullPath -NoTypeInformation -Encoding utf8
}

$violations = @()
foreach ($file in $manifest) {
    $path = $file.Path
    if (Test-PrivatePath $path) { $violations += $path; continue }
    if ($path -in $publicFiles) {
        $sourceHash = (Get-FileHash -LiteralPath (Join-Path $repoRoot $path) -Algorithm SHA256).Hash
        if ($file.SHA256 -ne $sourceHash) { $violations += "$path (does not match the public source file)" }
        continue
    }
    # Generated binaries, WinUI locale resources and specific dependency runtime/license files.
    $runtimeFile = $path -match '^[^/]+\.(dll|exe|pdb|pri|winmd|xbf)$' -or
        $path -match '^(Controls|Styles|Views)/[^/]+\.xbf$' -or
        $path -match '^[a-z]{2,3}(?:-[a-z0-9]{2,8}){1,2}/[^/]+\.(mui|pri|dll)$' -or
        $path -match '^LinxLauncher\.WinUI\.(deps|runtimeconfig)\.json$' -or
        $path -match '^workloads(?:\.[a-z0-9]+)?\.json$' -or
        $path -in @('Microsoft.UI.Xaml/Assets/map.html', 'Microsoft.UI.Xaml/Assets/NoiseAsset_256x256_PNG.png') -or
        $path -match '^licenses/(cubiomes|minecraft-renderer|schematic-editor)/(LICENSE|SOURCE\.md|manifest\.json)$'
    if (-not $runtimeFile) { $violations += $path }
}
$required = @($publicFiles) + @('LinxLauncher.WinUI.exe', 'LinxLauncher.WinUI.pri', 'rzero_seedmap.dll', 'rzero_projection_d3d11.dll', 'licenses/cubiomes/LICENSE', 'licenses/cubiomes/SOURCE.md', 'licenses/cubiomes/manifest.json', 'licenses/minecraft-renderer/LICENSE', 'licenses/minecraft-renderer/SOURCE.md', 'licenses/schematic-editor/LICENSE', 'licenses/schematic-editor/SOURCE.md')
foreach ($path in $required) {
    if ($path -notin $manifest.Path) { $violations += "$path (missing required file)" }
}
if ($NativeAot -and ('coreclr.dll' -in $manifest.Path -or 'LinxLauncher.WinUI.runtimeconfig.json' -in $manifest.Path)) {
    $violations += 'Native AOT output unexpectedly contains the managed runtime.'
}
if ($violations.Count) { throw "Distribution isolation failed:`n$($violations | Sort-Object -Unique | Out-String)" }
Write-Host "Distribution isolation passed: $($manifest.Count) files, $($publicFiles.Count) public resources/documents verified ($root)."
