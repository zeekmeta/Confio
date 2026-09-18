param([switch]$NativeAot, [string]$Source)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'This verification exercises Windows DPAPI.'
}

function Invoke-Checked {
    param([string]$Executable, [string[]]$Arguments)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable failed with exit code $LASTEXITCODE."
    }
}

$repository = Split-Path -Parent $PSScriptRoot
$verification = Join-Path $repository ('artifacts/package-verification/' + [Guid]::NewGuid().ToString('N'))
$feed = if ($Source) { $Source } else { Join-Path $verification 'feed' }
if ($feed -notmatch '^https?://') { $feed = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($feed) }
$cache = Join-Path $verification 'packages'
New-Item -ItemType Directory -Path $verification -Force | Out-Null
Write-Output "Verification artifacts: $verification"

[xml]$build = Get-Content -LiteralPath (Join-Path $repository 'Directory.Build.props') -Raw
$version = [string]$build.Project.PropertyGroup.Version
if (-not $Source) {
    New-Item -ItemType Directory -Path $feed -Force | Out-Null
    Invoke-Checked 'dotnet' @('pack', (Join-Path $repository 'src/Confio/Confio.csproj'), '-c', 'Release', '-o', $feed, '--nologo')
}

Copy-Item -LiteralPath (Join-Path $repository 'Directory.Build.props') -Destination $verification
[xml]$versions = Get-Content -LiteralPath (Join-Path $repository 'Directory.Packages.props') -Raw
$candidate = $versions.CreateElement('PackageVersion')
$candidate.SetAttribute('Include', 'Confio')
$candidate.SetAttribute('Version', $version)
$versions.Project.SelectSingleNode('ItemGroup[not(@Condition)]').AppendChild($candidate) | Out-Null
$versions.Save((Join-Path $verification 'Directory.Packages.props'))

$config = [xml]'<configuration><packageSources><clear /></packageSources><packageSourceMapping /><config /></configuration>'
$nugetSource = 'https://api.nuget.org/v3/index.json'
$sourceEntries = @(@('candidate', $feed, 'Confio'), @('nuget.org', $nugetSource, '*'))
# 同一服务地址只登记一次，避免 NuGet 去重后丢失依赖的源映射。
if ($feed.TrimEnd('/') -eq $nugetSource) {
    $sourceEntries = ,@('candidate', $feed, '*')
}
foreach ($sourceEntry in $sourceEntries) {
    $entry = $config.CreateElement('add')
    $entry.SetAttribute('key', $sourceEntry[0])
    $entry.SetAttribute('value', $sourceEntry[1])
    $config.configuration.packageSources.AppendChild($entry) | Out-Null
    $mapping = $config.CreateElement('packageSource')
    $mapping.SetAttribute('key', $sourceEntry[0])
    $pattern = $config.CreateElement('package')
    $pattern.SetAttribute('pattern', $sourceEntry[2])
    $mapping.AppendChild($pattern) | Out-Null
    $config.configuration.SelectSingleNode('packageSourceMapping').AppendChild($mapping) | Out-Null
}
$folder = $config.CreateElement('add')
$folder.SetAttribute('key', 'globalPackagesFolder')
$folder.SetAttribute('value', $cache)
$config.configuration.SelectSingleNode('config').AppendChild($folder) | Out-Null
$configPath = Join-Path $verification 'NuGet.Config'
$config.Save($configPath)

# 复用真实消费场景，仅替换工程引用；隔离工程不引用运行库或生成器源码。
foreach ($name in @('ConfioConsumerModels', 'ConfioConsumer')) {
    $sourceDirectory = Join-Path $repository ('tests/' + $name)
    $destination = Join-Path $verification $name
    New-Item -ItemType Directory -Path $destination | Out-Null
    Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' -File | Copy-Item -Destination $destination
    if ($name -eq 'ConfioConsumer') {
        Copy-Item -LiteralPath (Join-Path $sourceDirectory 'App.config') -Destination $destination
    }
    [xml]$project = Get-Content -LiteralPath (Join-Path $sourceDirectory ($name + '.csproj')) -Raw
    foreach ($reference in @($project.SelectNodes('//ProjectReference'))) {
        $reference.ParentNode.RemoveChild($reference) | Out-Null
    }
    $group = $project.CreateElement('ItemGroup')
    $reference = $project.CreateElement('PackageReference')
    $reference.SetAttribute('Include', 'Confio')
    $group.AppendChild($reference) | Out-Null
    if ($name -eq 'ConfioConsumer') {
        $shared = $project.CreateElement('ProjectReference')
        $shared.SetAttribute('Include', '../ConfioConsumerModels/ConfioConsumerModels.csproj')
        $group.AppendChild($shared) | Out-Null
    }
    $project.Project.AppendChild($group) | Out-Null
    $project.Save((Join-Path $destination ($name + '.csproj')))
}

$consumer = Join-Path $verification 'ConfioConsumer/ConfioConsumer.csproj'
$frameworkTargets = @('net462', 'net47', 'net471', 'net472', 'net48', 'net481')
$modernTargets = @('net8.0', 'net9.0', 'net10.0')
$frameworks = $frameworkTargets + $modernTargets
Invoke-Checked 'dotnet' @('restore', $consumer, '--configfile', $configPath, '--packages', $cache, '--nologo')
Invoke-Checked 'dotnet' @('build', $consumer, '-c', 'Release', '--no-restore', '--nologo')

foreach ($projectName in @('ConfioConsumer', 'ConfioConsumerModels')) {
    $assets = Get-Content -LiteralPath (Join-Path $verification ($projectName + '/obj/project.assets.json')) -Raw | ConvertFrom-Json
    foreach ($target in $assets.targets.PSObject.Properties) {
        $library = $target.Value.PSObject.Properties['Confio/' + $version].Value
        $asset = switch ($target.Name.Split('/')[0]) {
            { $_ -in $frameworkTargets } { $_ }
            'net8.0' { 'net8.0' }
            'net9.0' { 'net8.0' }
            'net10.0' { 'net10.0' }
            default { throw "Unexpected consumer target $($target.Name)." }
        }
        $expected = "lib/$asset/Confio.dll"
        if ($library.compile.PSObject.Properties.Name -notcontains $expected -or
            $library.runtime.PSObject.Properties.Name -notcontains $expected) {
            throw "Incorrect Confio asset selection for $($target.Name)."
        }
    }
}

$metadata = Get-Content -LiteralPath (Join-Path $cache ('confio/' + $version + '/.nupkg.metadata')) -Raw | ConvertFrom-Json
$restoredSource = [string]$metadata.source
if ($restoredSource -notmatch '^https?://') { $restoredSource = [IO.Path]::GetFullPath($restoredSource) }
if ($restoredSource.TrimEnd('/', '\') -ne $feed.TrimEnd('/', '\')) {
    throw 'Confio was not restored from the selected package source.'
}

$runtimeAssets = @(Get-ChildItem -LiteralPath (Join-Path $cache ('confio/' + $version + '/lib')) -Filter 'Confio.dll' -File -Recurse)
$expectedAssets = $frameworkTargets + @('net8.0', 'net10.0')
if ((@($runtimeAssets | ForEach-Object { $_.Directory.Name } | Sort-Object) -join ',') -ne
    (@($expectedAssets | Sort-Object) -join ',')) {
    throw 'The package must contain six Framework assets plus net8.0 and net10.0.'
}

[xml]$manifest = Get-Content -LiteralPath (Join-Path $cache ('confio/' + $version + '/confio.nuspec')) -Raw
$expectedGroups = @('.NETFramework4.6.2', '.NETFramework4.7', '.NETFramework4.7.1', '.NETFramework4.7.2',
    '.NETFramework4.8', '.NETFramework4.8.1', 'net8.0', 'net10.0')
if ((@($manifest.package.metadata.dependencies.group.targetFramework | Sort-Object) -join ',') -ne
    (@($expectedGroups | Sort-Object) -join ',')) {
    throw 'The package must contain a dependency group for each runtime asset.'
}
foreach ($group in $manifest.package.metadata.dependencies.group) {
    $dependencies = @($group.dependency | ForEach-Object { $_.id })
    $framework = $group.targetFramework.StartsWith('.NETFramework')
    foreach ($dependency in @('Microsoft.Bcl.Cryptography', 'PolySharp', 'System.Text.Encoding.CodePages')) {
        if (($dependencies -contains $dependency) -ne $framework) {
            throw "Incorrect $dependency dependency group: $($group.targetFramework)."
        }
    }
    if (($dependencies -contains 'System.Text.Json') -ne ($group.targetFramework -ne 'net10.0')) {
        throw "Incorrect System.Text.Json dependency group: $($group.targetFramework)."
    }
    if ($dependencies -contains 'Microsoft.Extensions.Hosting' -or $dependencies -contains 'System.Threading.AccessControl') {
        throw "Application Host dependencies must not enter the Confio package: $($group.targetFramework)."
    }
    foreach ($dependency in @('Microsoft.Extensions.Hosting.Abstractions', 'Microsoft.Extensions.Options')) {
        if ($dependencies -notcontains $dependency) {
            throw "Missing $dependency dependency: $($group.targetFramework)."
        }
    }
}

foreach ($framework in $frameworks) {
    Write-Output "Verifying managed consumer: $framework"
    $output = Join-Path $verification "ConfioConsumer/bin/Release/$framework"
    $unwanted = Get-ChildItem -LiteralPath $output -Filter '*.dll' -File |
        Where-Object { $_.Name -match '^(ConfioGenerator|Microsoft\.CodeAnalysis.*|System\.Text\.Json\.SourceGeneration|PolySharp.*)\.dll$' }
    if ($unwanted) { throw "Compiler dependencies entered the $framework runtime output." }
    if ($framework -in $frameworkTargets) {
        Invoke-Checked (Join-Path $output 'ConfioConsumer.exe') @('--directory', (Join-Path $verification 'files'))
    } else {
        Invoke-Checked 'dotnet' @((Join-Path $output 'ConfioConsumer.dll'), '--directory', (Join-Path $verification 'files'))
    }
}

# 双向跨运行时读同一份真实 AES 密文；调用者不转换密文或配置格式。
foreach ($writer in @('net462', 'net10.0')) {
    foreach ($extension in @('json', 'yaml')) {
        $fixture = Join-Path $verification "portable/$writer/settings.$extension"
        $output = Join-Path $verification "ConfioConsumer/bin/Release/$writer"
        if ($writer -eq 'net462') {
            Invoke-Checked (Join-Path $output 'ConfioConsumer.exe') @('encryption', 'write', $fixture)
        } else {
            Invoke-Checked 'dotnet' @((Join-Path $output 'ConfioConsumer.dll'), 'encryption', 'write', $fixture)
        }
        foreach ($reader in $frameworks) {
            $output = Join-Path $verification "ConfioConsumer/bin/Release/$reader"
            if ($reader -in $frameworkTargets) {
                Invoke-Checked (Join-Path $output 'ConfioConsumer.exe') @('encryption', 'read', $fixture)
            } else {
                Invoke-Checked 'dotnet' @((Join-Path $output 'ConfioConsumer.dll'), 'encryption', 'read', $fixture)
            }
        }
    }
}

if ($NativeAot) {
    foreach ($framework in $modernTargets) {
        Write-Output "Verifying Native AOT consumer: $framework"
        $output = Join-Path $verification "native/$framework"
        Invoke-Checked 'dotnet' @('publish', $consumer, '-c', 'Release', '-f', $framework, '-r', 'win-x64', '-o', $output, '--nologo')
        Invoke-Checked (Join-Path $output 'ConfioConsumer.exe') @('--directory', (Join-Path $verification 'files'))
        foreach ($writer in @('net462', 'net10.0')) {
            foreach ($extension in @('json', 'yaml')) {
                Invoke-Checked (Join-Path $output 'ConfioConsumer.exe') @('encryption', 'read', (Join-Path $verification "portable/$writer/settings.$extension"))
            }
        }
    }
}

Write-Output "PASS: isolated Confio $version package consumption. Artifacts: $verification"
