<#
.SYNOPSIS
    Publishes the deployable bundle: the GUI and the CLI in one folder, self-contained.

.DESCRIPTION
    One folder containing both programs, sharing one copy of the .NET runtime. Publishing
    them separately would double a ~170 MB payload for no benefit, and NFR4 expects this to
    be run from a UNC share where that size is paid on every launch.

    Self-contained and NOT trimmed, and not NativeAOT. PRD 5.1: Microsoft.PowerShell.SDK
    does not tolerate IL trimming and resolves modules from disk. Do not spend effort
    fighting this.

    Not single-file either, despite PRD Q4's default leaning that way. Both were published
    and compared:

      folder       169 MB, 304 files
      single-file  162 MB, but still 12 entries - a 166 MB executable plus eight native
                   libraries (e_sqlite3.dll, pwrshplugin.dll, sni.dll and friends) and a
                   runtimes\ directory that cannot be embedded

    So "single file" is not one file. The operator copies a folder either way, and the
    single-file build only adds a first-launch extraction step for the privilege. PRD 5.1
    predicted this and said not to spend effort fighting it. Q4 is answered: folder.

.PARAMETER Output
    Where the bundle is written. Cleared first, so do not point it at anything else.

.PARAMETER SignToolPath
    Path to signtool.exe. When supplied together with -CertificateThumbprint, every
    executable and managed assembly in the bundle is Authenticode-signed. When omitted the
    bundle is left unsigned and the operator README says so, loudly.

.PARAMETER CertificateThumbprint
    Thumbprint of the signing certificate in the current user's or machine's store.

.PARAMETER TimestampUrl
    RFC 3161 timestamp authority. Signing without one produces a signature that stops
    validating the day the certificate expires, so this defaults to a working URL rather
    than being optional in practice.
#>

[CmdletBinding()]
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\HybridAgentDeploy'),
    [string]$Configuration = 'Release',
    [string]$SignToolPath,
    [string]$CertificateThumbprint,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$repo = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$out = [System.IO.Path]::GetFullPath($Output)

Write-Host "Repository : $repo"
Write-Host "Output     : $out"
Write-Host ''

# ---------------------------------------------------------------------------------------
# 1. Clean. A stale assembly left behind from a previous publish would ship.
# ---------------------------------------------------------------------------------------
if (Test-Path $out) {
    Write-Host 'Clearing previous output...'
    Remove-Item $out -Recurse -Force
}
New-Item -ItemType Directory -Path $out -Force | Out-Null

# ---------------------------------------------------------------------------------------
# 2. Publish. The GUI first, then the CLI into the same folder.
#
#    Order matters and the reason is not obvious: the two executables would collide if they
#    were still named HybridAgentDeploy.exe and hybridagentdeploy.exe, because Windows
#    filenames are case-insensitive. The CLI is named hadeploy.exe precisely so they do not.
#    The check at step 3 asserts both survived, because the failure mode is silent.
# ---------------------------------------------------------------------------------------
$common = @(
    '-c', $Configuration
    '-r', 'win-x64'
    '--self-contained', 'true'
    '-p:PublishSingleFile=false'
    '-p:PublishTrimmed=false'
    '-p:PublishReadyToRun=false'
    '-o', $out
    '--nologo'
)

foreach ($project in 'src\HybridAgentDeploy.Gui\HybridAgentDeploy.Gui.csproj',
                     'src\HybridAgentDeploy.Cli\HybridAgentDeploy.Cli.csproj') {
    Write-Host "Publishing $project ..."
    & dotnet publish (Join-Path $repo $project) @common
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $project" }
}

# ---------------------------------------------------------------------------------------
# 3. Assert both programs are present.
# ---------------------------------------------------------------------------------------
$gui = Join-Path $out 'HybridAgentDeploy.exe'
$cli = Join-Path $out 'hadeploy.exe'

foreach ($exe in $gui, $cli) {
    if (-not (Test-Path $exe)) {
        throw "expected executable missing from the bundle: $exe. If both projects share a " +
              "filename that differs only by case, the second publish overwrites the first."
    }
}

# Both runtime configs too — the GUI's names Microsoft.WindowsDesktop.App, and losing it is
# the same silent overwrite one level down.
foreach ($config in 'HybridAgentDeploy.runtimeconfig.json', 'hadeploy.runtimeconfig.json') {
    if (-not (Test-Path (Join-Path $out $config))) { throw "missing runtime config: $config" }
}

$desktop = Get-Content (Join-Path $out 'HybridAgentDeploy.runtimeconfig.json') -Raw
if ($desktop -notmatch 'Microsoft\.WindowsDesktop\.App') {
    throw 'HybridAgentDeploy.runtimeconfig.json does not include Microsoft.WindowsDesktop.App. ' +
          'The GUI would fail to start on a machine with no .NET installed.'
}

# The two trims of Directory.Build.props / .targets, asserted rather than assumed. Both are
# silent when they stop working — a NuGet update that renames the contentFiles path, or a
# project that overrides SatelliteResourceLanguages, would quietly add 27 MB back.
if (Test-Path (Join-Path $out 'ref')) {
    throw 'ref\ is present in the bundle. RemovePowerShellReferenceAssembliesFromPublish in ' +
          'Directory.Build.targets is no longer matching - check whether the PowerShell SDK ' +
          'package changed where it puts its contentFiles.'
}

$locales = Get-ChildItem $out -Directory |
    Where-Object { $_.Name -match '^(cs|de|es|fr|it|ja|ko|pl|pt-BR|ru|tr|zh-Hans|zh-Hant)$' }

if ($locales) {
    throw ("Localised resource folders are present in the bundle: {0}. " -f ($locales.Name -join ', ')) +
          'SatelliteResourceLanguages in Directory.Build.props is no longer taking effect.'
}

# ---------------------------------------------------------------------------------------
# 4. Operator documentation and the portable-mode sample.
# ---------------------------------------------------------------------------------------
Copy-Item (Join-Path $repo 'docs\OPERATOR-README.md') (Join-Path $out 'README.md') -Force
Copy-Item (Join-Path $repo 'docs\hybridagentdeploy.json.sample') $out -Force

# ---------------------------------------------------------------------------------------
# 5. Signing. SEC9 requires the published binary to be Authenticode-signed before
#    distribution. PRD Q3 — whether Quest will sign a tool with unsupported status — is
#    deferred, so this stays a hook rather than a step: wired up, exercised by argument, and
#    a no-op until someone supplies a certificate.
#
#    Treat signing as a release gate, not a build step. An unsigned bundle is fine to test
#    with and is NOT fine to hand to a customer: see the warning in the operator README.
# ---------------------------------------------------------------------------------------
if ($SignToolPath -and $CertificateThumbprint) {
    if (-not (Test-Path $SignToolPath)) { throw "signtool not found at $SignToolPath" }

    # Only what is not already signed.
    #
    # 272 of the 285 binaries in this bundle already carry valid Microsoft or .NET Foundation
    # signatures. signtool REPLACES an existing signature unless told to append, so signing
    # everything would strip Microsoft's attestation off 234 .NET runtime files and put ours
    # there instead - claiming authorship of code we did not write, and discarding the
    # stronger signature in the process.
    #
    # What is left is thirteen files: our five, and eight unsigned third-party libraries that
    # ship unsigned from NuGet (SQLitePCLRaw, e_sqlite3, the JsonSchema.Net family, Markdig).
    # Those do need our signature, because AppLocker publisher rules and some EDR products
    # evaluate what gets loaded and not only what gets launched - an unsigned DLL loaded by a
    # signed executable is exactly the case a publisher rule is written to catch.
    $targets = Get-ChildItem $out -Recurse -Include *.exe, *.dll |
        Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -eq 'NotSigned' } |
        Select-Object -Expand FullName

    Write-Host "Signing $($targets.Count) unsigned file(s) with certificate $CertificateThumbprint ..."
    Write-Host "  (leaving Microsoft-signed assemblies alone)"

    # Timestamping is what keeps a signature valid after the certificate expires. Without it,
    # every copy of this tool in the field stops validating on the certificate's expiry date -
    # which for a utility that lives on a share for years is a scheduled outage.
    #
    # Allowed to be empty for an offline signing machine or a pipeline test, but never
    # quietly.
    $signArgs = @('sign', '/fd', 'SHA256')

    if ([string]::IsNullOrWhiteSpace($TimestampUrl)) {
        Write-Warning @'
SIGNING WITHOUT A TIMESTAMP.

The signature will stop validating the day the certificate expires, on every copy already
distributed. Only do this for a pipeline test or where no timestamp authority is reachable.
'@
    }
    else {
        $signArgs += @('/td', 'SHA256', '/tr', $TimestampUrl)
    }

    $signArgs += @('/sha1', $CertificateThumbprint)

    & $SignToolPath @signArgs @targets
    if ($LASTEXITCODE -ne 0) { throw 'signing failed' }

    # Verify rather than trust the exit code. /pa uses the default Authenticode policy, which
    # is what Windows itself applies when deciding whether to run the file.
    & $SignToolPath verify /pa /all @targets
    if ($LASTEXITCODE -ne 0) { throw 'signature verification failed after signing' }

    $unsigned = Get-ChildItem $out -Recurse -Include *.exe, *.dll |
        Where-Object { (Get-AuthenticodeSignature $_.FullName).Status -ne 'Valid' }

    if ($unsigned) {
        throw ("These files still do not carry a valid signature: {0}" -f
               (($unsigned | Select-Object -Expand Name) -join ', '))
    }

    Write-Host 'Signed and verified.' -ForegroundColor Green
}
else {
    Write-Warning @'
BUNDLE IS UNSIGNED.

SEC9 requires an Authenticode signature before distribution, and PRD Q3 (will Quest sign
this?) is unresolved. AppLocker and EDR may block an unsigned executable that opens WinRM
sessions to domain controllers - which is precisely the kind of environment this tool exists
to serve.

Fine for lab testing. Not fine to hand to a customer. Re-run with -SignToolPath and
-CertificateThumbprint once a certificate exists.
'@
}

# ---------------------------------------------------------------------------------------
# 6. Report what was produced.
# ---------------------------------------------------------------------------------------
$files = Get-ChildItem $out -Recurse -File
$bytes = ($files | Measure-Object -Property Length -Sum).Sum

Write-Host ''
Write-Host 'Bundle' -ForegroundColor Cyan
Write-Host ("  Path      : {0}" -f $out)
Write-Host ("  Files     : {0:N0}" -f $files.Count)
Write-Host ("  Size      : {0:N1} MB" -f ($bytes / 1MB))
Write-Host ("  GUI       : {0}  ({1})" -f (Split-Path $gui -Leaf), (Get-Item $gui).VersionInfo.FileVersion)
Write-Host ("  CLI       : {0}  ({1})" -f (Split-Path $cli -Leaf), (Get-Item $cli).VersionInfo.FileVersion)
Write-Host ("  Signed    : {0}" -f $(if ($SignToolPath -and $CertificateThumbprint) { 'yes' } else { 'NO' }))
