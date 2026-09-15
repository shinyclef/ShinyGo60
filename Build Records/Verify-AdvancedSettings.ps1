$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$binaryRoot = Join-Path $root 'Windows/ShinyGo60.Companion/bin/Release/net10.0-windows10.0.26100.0'
Add-Type -AssemblyName PresentationFramework
foreach ($name in @('ShinyGo60.Diagnostics', 'ShinyGo60.Protocol', 'ShinyGo60.Companion.Core', 'ShinyGo60.Platform.Windows', 'ShinyGo60.Companion')) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $binaryRoot "$name.dll"))
}
$app = [ShinyGo60.Companion.App]::new()
$app.InitializeComponent()
$manifest = [ShinyGo60.Protocol.Manifests.LayoutManifestJson]::ReadAsync(
    (Join-Path $root 'artifacts/ShinyGo60 Companion/layout-manifest.json'), [Threading.CancellationToken]::None).AsTask().GetAwaiter().GetResult()
$configuration = [ShinyGo60.Companion.Core.Configuration.CompanionConfigurationJson]::ReadAndResolveAsync(
    (Join-Path $root 'artifacts/ShinyGo60 Companion/companion-settings.json'), $manifest,
    [Threading.CancellationToken]::None).AsTask().GetAwaiter().GetResult()
$window = [ShinyGo60.Companion.MainWindow]::new($manifest, $configuration, $true, (Join-Path $root 'Build Records/sample.log'))
$content = $window.Content
$settingsBorder = $content.Children[2]
$scroll = $settingsBorder.Child
$latest = $window.FindName('LatestConnectionIssueValue')
$advanced = $latest.Parent.Children[1]
if ($advanced.Header -ne 'Advanced Settings' -or $advanced.IsExpanded) { throw 'Advanced Settings must start collapsed.' }
$headers = @($advanced.Content.Children | ForEach-Object Header)
if (($headers -join '|') -ne 'Connection Issue History|Adaptive Bluetooth Latency|Connection Details') { throw 'Unexpected advanced sections.' }
$issue = [ShinyGo60.Companion.Core.Diagnostics.ConnectionIssue]::new([DateTimeOffset]::Parse('2026-09-12T04:07:00Z'), $false)
$report = [ShinyGo60.Companion.Core.Diagnostics.ConnectionIssueReport]::new(
    [DateTimeOffset]::UtcNow, [ShinyGo60.Companion.Core.Diagnostics.ConnectionIssue[]]@($issue), 'History updates automatically.',
    [ShinyGo60.Companion.Core.Diagnostics.BluetoothSystemEvent[]]@())
$window.UpdateConnectionIssues($report)
$expected = 'Last connection issue: ' + $issue.StartedUtc.ToLocalTime().ToString('g', [Globalization.CultureInfo]::CurrentCulture)
if ($latest.Text -ne $expected) { throw 'Latest issue must show its local date and time.' }
if ($window.FindName('IssueHistoryExpander').Header -ne 'Connection Issue History') { throw 'History header must stay static.' }
foreach ($size in @(@(864, 726), @(784, 581))) {
    foreach ($expanded in @($false, $true)) {
        $advanced.IsExpanded = $expanded
        foreach ($section in $advanced.Content.Children) { $section.IsExpanded = $expanded }
        $content.Measure([Windows.Size]::new($size[0], $size[1]))
        $content.Arrange([Windows.Rect]::new(0, 0, $size[0], $size[1]))
        $content.UpdateLayout()
        if ($expanded) {
            $scroll.ScrollToVerticalOffset($advanced.TranslatePoint([Windows.Point]::new(0, 0), $scroll.Content).Y)
        } else {
            $scroll.ScrollToTop()
        }
        $content.UpdateLayout()
        $right = $scroll.Content.TransformToAncestor($scroll).Transform([Windows.Point]::new($scroll.Content.ActualWidth, 0)).X
        if ($scroll.ActualWidth - $right -lt 19) { throw 'Settings content overlaps the scrollbar gutter.' }
        $image = [Windows.Media.Imaging.RenderTargetBitmap]::new($size[0], $size[1], 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
        $image.Render($content)
        $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
        $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($image))
        $path = Join-Path $PSScriptRoot "advanced-settings-$($size[0])-$expanded.png"
        $stream = [IO.File]::Create($path)
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
        [pscustomobject]@{ Width = $size[0]; Expanded = $expanded; ScrollbarGutter = $scroll.ActualWidth - $right; Image = $path }
    }
}
$estimatedIssue = [ShinyGo60.Companion.Core.Diagnostics.ConnectionIssue]::new($issue.StartedUtc, $true)
$estimatedReport = [ShinyGo60.Companion.Core.Diagnostics.ConnectionIssueReport]::new(
    [DateTimeOffset]::UtcNow, [ShinyGo60.Companion.Core.Diagnostics.ConnectionIssue[]]@($estimatedIssue), 'History updates automatically.',
    [ShinyGo60.Companion.Core.Diagnostics.BluetoothSystemEvent[]]@())
$window.UpdateConnectionIssues($estimatedReport)
if ($latest.Text -ne "$expected (estimated)") { throw 'Estimated issue times must be labeled.' }
$emptyReport = [ShinyGo60.Companion.Core.Diagnostics.ConnectionIssueReport]::new(
    [DateTimeOffset]::UtcNow, [ShinyGo60.Companion.Core.Diagnostics.ConnectionIssue[]]@(), 'History updates automatically.',
    [ShinyGo60.Companion.Core.Diagnostics.BluetoothSystemEvent[]]@())
$window.UpdateConnectionIssues($emptyReport)
if ($latest.Text -ne 'No connection issues found in the available logs.') { throw 'Empty history must clear the latest issue.' }
$window.PrepareForExit()
$window.Close()
