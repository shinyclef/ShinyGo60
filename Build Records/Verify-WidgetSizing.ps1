$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
[xml] $markup = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Windows/ShinyGo60.Companion/TaskbarWidgetWindow.xaml') -Raw
$markup.DocumentElement.RemoveAttribute('Class', 'http://schemas.microsoft.com/winfx/2006/xaml')
$border = $markup.DocumentElement.SelectSingleNode('*[local-name()="Border"]')
$border.RemoveAttribute('MouseLeftButtonUp')
$border.SetAttribute('BorderBrush', '#374151')
$reader = [System.Xml.XmlNodeReader]::new($markup)
$window = [System.Windows.Markup.XamlReader]::Load($reader)
$content = $window.Content
$layer = $window.FindName('LayerValue')
$connection = $window.FindName('ConnectionValue')
$connection.Text = 'CURRENT · BT'
$window.FindName('LeftBatteryValue').Text = '87'
$window.FindName('LeftBatteryPercent').Text = '%'
$window.FindName('RightBatteryValue').Text = '64'
$window.FindName('RightBatteryPercent').Text = '%'
$results = foreach ($name in @('Home', 'WindowsAndSymbols', 'A much longer layer name with several words', 'Home')) {
    $layer.Text = $name
    $width = 189
    $content.Measure([System.Windows.Size]::new($width, 38))
    $content.Arrange([System.Windows.Rect]::new(0, 0, $width, 38))
    $content.UpdateLayout()
    $viewbox = $layer.Parent
    $textBounds = $layer.TransformToAncestor($viewbox).TransformBounds([System.Windows.Rect]::new($layer.RenderSize))
    if ($textBounds.Width -gt $viewbox.ActualWidth + 0.01) { throw "Layer '$name' was clipped." }
    $scale = $textBounds.Width / $layer.ActualWidth
    if ($scale -gt 1.001) { throw 'Layer font was enlarged.' }
    $connectionPosition = $connection.TransformToAncestor($content).Transform([System.Windows.Point]::new(0, 0))
    [pscustomobject]@{ Layer = $name; WidgetWidth = $width; FontSize = 13 * $scale; ConnectionTop = $connectionPosition.Y }
}
if ([Math]::Abs($results[3].FontSize - 13) -gt 0.01) { throw 'Short layer font did not return to normal.' }
if ($results[1].FontSize -ge $results[0].FontSize) { throw 'Long layer font did not shrink.' }
foreach ($result in $results) {
    if ([Math]::Abs($result.ConnectionTop - $results[0].ConnectionTop) -gt 0.01) { throw 'Connection label moved.' }
}
$window.Close()
$results | Format-Table -AutoSize
