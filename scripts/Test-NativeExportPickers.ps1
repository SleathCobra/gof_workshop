[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Workspace,

    [Parameter(Mandatory)]
    [string] $AemAsset,

    [Parameter(Mandatory)]
    [string] $AeiAsset,

    [int] $TimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not ('NativeExportAutomation' -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class NativeExportAutomation
{
    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam);
}
"@
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$application = Join-Path $repositoryRoot 'src\Gof2Workshop.App\bin\Release\net10.0\Gof2Workshop.App.exe'
$workspacePath = [IO.Path]::GetFullPath($Workspace)
$aemPath = [IO.Path]::GetFullPath($AemAsset)
$aeiPath = [IO.Path]::GetFullPath($AeiAsset)

if (-not (Test-Path -LiteralPath $application -PathType Leaf)) {
    throw "Release application not found. Run 'dotnet build GalaxyOnFire2Workshop.sln -c Release' first."
}

foreach ($path in @($workspacePath, $aemPath, $aeiPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required input does not exist: $path"
    }
}

$workspaceState = Get-Content -LiteralPath $workspacePath -Raw | ConvertFrom-Json
$workspaceDirectory = Split-Path -Parent $workspacePath
$outputRoot = [IO.Path]::GetFullPath(
    (Join-Path $workspaceDirectory ([string] $workspaceState.OutputRoot)))
$gameRoot = [IO.Path]::GetFullPath([string] $workspaceState.GameAssetRoot)
$relativeToGame = [IO.Path]::GetRelativePath($gameRoot, $outputRoot)
if ($relativeToGame -ne '..' -and
    -not $relativeToGame.StartsWith("..$([IO.Path]::DirectorySeparatorChar)") -and
    -not [IO.Path]::IsPathRooted($relativeToGame)) {
    throw "The workspace output root is beneath the immutable game root: $outputRoot"
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

function Wait-Until {
    param(
        [Parameter(Mandatory)]
        [scriptblock] $Condition,
        [Parameter(Mandatory)]
        [string] $FailureMessage
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $result = & $Condition
        if ($result) {
            return $result
        }

        Start-Sleep -Milliseconds 150
    } while ([DateTime]::UtcNow -lt $deadline)

    throw $FailureMessage
}

function Wait-ForCompletedFile {
    param(
        [Parameter(Mandatory)]
        [string] $Path,
        [Parameter(Mandatory)]
        [string] $FailureMessage
    )

    Wait-Until -FailureMessage $FailureMessage -Condition {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
            return $null
        }

        try {
            $stream = [IO.File]::Open(
                $Path,
                [IO.FileMode]::Open,
                [IO.FileAccess]::Read,
                [IO.FileShare]::None)
            try {
                return $stream.Length -gt 0
            }
            finally {
                $stream.Dispose()
            }
        }
        catch [IO.IOException] {
            return $null
        }
    } | Out-Null
}

function Start-Workshop {
    param([string] $Asset)

    $arguments = @(
        '--workspace',
        "`"$workspacePath`"",
        '--open',
        "`"$Asset`"")
    $process = Start-Process -FilePath $application -ArgumentList $arguments -PassThru
    Wait-Until -FailureMessage 'Workshop main window did not appear.' -Condition {
        $process.Refresh()
        if ($process.HasExited) {
            throw "Workshop exited with code $($process.ExitCode)."
        }

        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            return $process
        }

        return $null
    }
}

function Stop-Workshop {
    param([Diagnostics.Process] $Process)

    if ($null -eq $Process -or $Process.HasExited) {
        return
    }

    $null = $Process.CloseMainWindow()
    if (-not $Process.WaitForExit(8000)) {
        Stop-Process -Id $Process.Id
    }
}

function Get-MainAutomationElement {
    param([Diagnostics.Process] $Process)

    $Process.Refresh()
    return [Windows.Automation.AutomationElement]::FromHandle($Process.MainWindowHandle)
}

function Find-NamedDescendant {
    param(
        [Windows.Automation.AutomationElement] $Root,
        [string] $Name
    )

    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    return $Root.FindFirst(
        [Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Invoke-NamedButton {
    param(
        [Windows.Automation.AutomationElement] $Root,
        [string] $Name
    )

    $button = Find-NamedDescendant -Root $Root -Name $Name
    if ($null -eq $button -or -not $button.Current.IsEnabled) {
        throw "Enabled '$Name' button was not found."
    }

    $invoke = $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
}

function Wait-ForDialog {
    param(
        [Diagnostics.Process] $Process,
        [string] $Title
    )

    return Wait-Until -FailureMessage "Native dialog '$Title' did not appear." -Condition {
        try {
            $main = Get-MainAutomationElement -Process $Process
            return Find-NamedDescendant -Root $main -Name $Title
        }
        catch [System.Runtime.InteropServices.COMException] {
            return $null
        }
    }
}

function Accept-NativeDialog {
    param([Windows.Automation.AutomationElement] $Dialog)

    $idCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty,
        '1')
    $classCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ClassNameProperty,
        'Button')
    $acceptCondition = [Windows.Automation.AndCondition]::new(
        $idCondition,
        $classCondition)
    $accept = $Dialog.FindFirst(
        [Windows.Automation.TreeScope]::Descendants,
        $acceptCondition)
    if ($null -eq $accept -or $accept.Current.NativeWindowHandle -eq 0) {
        throw "Native dialog accept button was not found."
    }

    # BM_CLICK exercises the same native Common Item Dialog acceptance path as a user click.
    $null = [NativeExportAutomation]::SendMessage(
        [IntPtr] $accept.Current.NativeWindowHandle,
        0x00F5,
        [IntPtr]::Zero,
        [IntPtr]::Zero)
}

function Set-NativeDialogFileName {
    param(
        [Windows.Automation.AutomationElement] $Dialog,
        [string] $FileName
    )

    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty,
        '1148')
    $fileNameControl = $Dialog.FindFirst(
        [Windows.Automation.TreeScope]::Descendants,
        $condition)
    if ($null -eq $fileNameControl) {
        return
    }

    $value = $fileNameControl.GetCurrentPattern(
        [Windows.Automation.ValuePattern]::Pattern)
    $value.SetValue($FileName)
}

$aemBaseName = [IO.Path]::GetFileNameWithoutExtension($aemPath)
$gltfPath = Join-Path $outputRoot "$aemBaseName.gltf"
$binPath = Join-Path $outputRoot "$aemBaseName.bin"
$aemCopyPath = Join-Path $outputRoot "$aemBaseName.aem"
$aeiBaseName = [IO.Path]::GetFileNameWithoutExtension($aeiPath)
$pngPath = Join-Path $outputRoot "$aeiBaseName.png"
$aeiCopyPath = Join-Path $outputRoot "$aeiBaseName.aei"

# These are generated smoke-test targets in the workspace output root, never source assets.
foreach ($target in @($gltfPath, $binPath, $aemCopyPath, $pngPath, $aeiCopyPath)) {
    if (Test-Path -LiteralPath $target -PathType Leaf) {
        Remove-Item -LiteralPath $target
    }
}

$aemProcess = $null
try {
    $aemProcess = Start-Workshop -Asset $aemPath
    $main = Get-MainAutomationElement -Process $aemProcess
    Wait-Until -FailureMessage 'AEM document did not become exportable.' -Condition {
        $active = Find-NamedDescendant -Root $main -Name ([IO.Path]::GetFileName($aemPath))
        if ($null -eq $active) {
            return $null
        }

        # Wait for the requested AEM document itself. The workbench becomes interactive while
        # restored documents are still opening, so the global Export Current command can briefly
        # belong to a different active tab.
        $candidate = Find-NamedDescendant -Root $main -Name 'Export glTF'
        if ($null -ne $candidate -and $candidate.Current.IsEnabled) {
            return $candidate
        }

        return $null
    } | Out-Null
    Invoke-NamedButton -Root $main -Name 'Export glTF'
    $dialog = Wait-ForDialog -Process $aemProcess -Title 'Export AEM as glTF 2.0'
    Accept-NativeDialog -Dialog $dialog
    Wait-Until -FailureMessage 'Native AEM export did not create glTF and BIN files.' -Condition {
        (Test-Path -LiteralPath $gltfPath -PathType Leaf) -and
        (Test-Path -LiteralPath $binPath -PathType Leaf)
    } | Out-Null
    $main = Get-MainAutomationElement -Process $aemProcess
    Invoke-NamedButton -Root $main -Name 'Save AEM Copy'
    $dialog = Wait-ForDialog -Process $aemProcess -Title 'Save Reconstructed AEM Copy'
    Set-NativeDialogFileName -Dialog $dialog -FileName ([IO.Path]::GetFileName($aemCopyPath))
    Accept-NativeDialog -Dialog $dialog
    Wait-ForCompletedFile -Path $aemCopyPath `
        -FailureMessage 'Native AEM save-copy did not finish writing an AEM file.'
}
finally {
    Stop-Workshop -Process $aemProcess
}

$aeiProcess = $null
try {
    $aeiProcess = Start-Workshop -Asset $aeiPath
    $main = Get-MainAutomationElement -Process $aeiProcess
    Wait-Until -FailureMessage 'AEI document did not become exportable.' -Condition {
        $active = Find-NamedDescendant -Root $main -Name ([IO.Path]::GetFileName($aeiPath))
        if ($null -eq $active) {
            return $null
        }

        $candidate = Find-NamedDescendant -Root $main -Name 'Export PNG'
        if ($null -ne $candidate -and $candidate.Current.IsEnabled) {
            return $candidate
        }

        return $null
    } | Out-Null
    Invoke-NamedButton -Root $main -Name 'Export PNG'
    $dialog = Wait-ForDialog -Process $aeiProcess -Title 'Export AEI Texture as PNG'
    Set-NativeDialogFileName -Dialog $dialog -FileName ([IO.Path]::GetFileName($pngPath))
    Accept-NativeDialog -Dialog $dialog
    Wait-Until -FailureMessage 'Native AEI export did not create a PNG file.' -Condition {
        Test-Path -LiteralPath $pngPath -PathType Leaf
    } | Out-Null
    $main = Get-MainAutomationElement -Process $aeiProcess
    Invoke-NamedButton -Root $main -Name 'Save AEI Copy'
    $dialog = Wait-ForDialog -Process $aeiProcess -Title 'Save AEI Container Copy'
    Set-NativeDialogFileName -Dialog $dialog -FileName ([IO.Path]::GetFileName($aeiCopyPath))
    Accept-NativeDialog -Dialog $dialog
    try {
        Wait-ForCompletedFile -Path $aeiCopyPath `
            -FailureMessage 'Native AEI save-copy did not finish writing an AEI file.'
    }
    catch {
        $main = Get-MainAutomationElement -Process $aeiProcess
        $elements = $main.FindAll(
            [Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.Condition]::TrueCondition)
        $visibleNames = @($elements | ForEach-Object { $_.Current.Name } | Where-Object { $_ })
        Write-Warning ("Visible UI at failure: " + ($visibleNames -join ' | '))
        throw
    }
}
finally {
    Stop-Workshop -Process $aeiProcess
}

$gltf = Get-Content -LiteralPath $gltfPath -Raw | ConvertFrom-Json
$pngBytes = [IO.File]::ReadAllBytes($pngPath)
if ($pngBytes.Length -lt 8 -or
    [BitConverter]::ToString($pngBytes, 0, 8) -ne '89-50-4E-47-0D-0A-1A-0A') {
    throw 'The native AEI export did not produce a valid PNG signature.'
}

$aemSourceHash = (Get-FileHash -LiteralPath $aemPath -Algorithm SHA256).Hash
$aemCopyHash = (Get-FileHash -LiteralPath $aemCopyPath -Algorithm SHA256).Hash
if ($aemSourceHash -ne $aemCopyHash) {
    throw 'The reconstructed AEM copy differs from the unchanged source model.'
}

$aeiSourceHash = (Get-FileHash -LiteralPath $aeiPath -Algorithm SHA256).Hash
$aeiCopyHash = (Get-FileHash -LiteralPath $aeiCopyPath -Algorithm SHA256).Hash
if ($aeiSourceHash -ne $aeiCopyHash) {
    throw 'The AEI container copy differs from the source bytes.'
}

[pscustomobject]@{
    Workspace = $workspacePath
    OutputRoot = $outputRoot
    Gltf = $gltfPath
    GltfMeshes = @($gltf.meshes).Count
    GltfAnimations = if ($null -eq $gltf.animations) { 0 } else { @($gltf.animations).Count }
    BinaryBytes = (Get-Item -LiteralPath $binPath).Length
    AemCopy = $aemCopyPath
    AemCopySha256 = $aemCopyHash
    Png = $pngPath
    PngBytes = $pngBytes.Length
    AeiCopy = $aeiCopyPath
    AeiCopySha256 = $aeiCopyHash
}
