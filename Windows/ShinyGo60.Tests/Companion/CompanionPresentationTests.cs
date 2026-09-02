using ShinyGo60.Companion.Core.Presentation;
using ShinyGo60.Companion.Core.Sessions;
using ShinyGo60.Companion.Core.Telemetry;
using ShinyGo60.Protocol.Manifests;
using ShinyGo60.Protocol.Messages;
using ShinyGo60.Protocol.Transport;
using ShinyGo60.Tests.Testing;

namespace ShinyGo60.Tests.Companion;

internal static class CompanionPresentationTests
{
    public static ValueTask RunAsync()
    {
        VerifyCurrentPresentation();
        VerifyStalePresentation();
        VerifyDisconnectedPresentation();
        VerifyBottomTaskbarPlacement();
        VerifyScaledSecondaryMonitorPlacement();
        VerifyVerticalTaskbarPlacement();
        VerifyUnavailableTaskbarPlacement();
        return ValueTask.CompletedTask;
    }

    private static void VerifyCurrentPresentation()
    {
        CompanionStatus status = new(
            CompanionConnectionState.Connected,
            TransportKind.Bluetooth,
            CreateLayerState(),
            CreateBatteryState(BatteryReadingStatus.Fresh),
            "Bluetooth session is healthy",
            0);

        CompanionDisplayState display = CompanionStatusPresenter.Present(status);

        AssertEx.Equal(CompanionDisplayConnectionState.Current, display.ConnectionState);
        AssertEx.Equal("Navigation", display.LayerName);
        AssertEx.Equal("CURRENT", display.ConnectionLabel);
        AssertEx.Equal("Bluetooth", display.TransportLabel);
        AssertEx.Equal("63%", display.LeftBattery.Text);
        AssertEx.Equal(false, display.LeftBattery.IsStale);
        AssertEx.Equal("81%", display.RightBattery.Text);
    }

    private static void VerifyStalePresentation()
    {
        CompanionStatus status = new(
            CompanionConnectionState.Connecting,
            TransportKind.Usb,
            CreateLayerState(),
            CreateBatteryState(BatteryReadingStatus.Fresh),
            "Reconnecting",
            2);

        CompanionDisplayState display = CompanionStatusPresenter.Present(status);

        AssertEx.Equal(CompanionDisplayConnectionState.Stale, display.ConnectionState);
        AssertEx.Equal("STALE", display.ConnectionLabel);
        AssertEx.Equal(string.Empty, display.TransportLabel);
        AssertEx.Equal(true, display.LeftBattery.IsStale);
        AssertEx.Equal(true, display.RightBattery.IsStale);
    }

    private static void VerifyDisconnectedPresentation()
    {
        CompanionStatus status = new(
            CompanionConnectionState.Connecting,
            TransportKind.Usb,
            null,
            null,
            "Looking for USB",
            1);

        CompanionDisplayState display = CompanionStatusPresenter.Present(status);

        AssertEx.Equal(CompanionDisplayConnectionState.Disconnected, display.ConnectionState);
        AssertEx.Equal("SEARCHING", display.ConnectionLabel);
        AssertEx.Equal("No keyboard", display.LayerName);
        AssertEx.Equal("—", display.LeftBattery.Text);
        AssertEx.Equal(false, display.LeftBattery.IsAvailable);
    }

    private static void VerifyBottomTaskbarPlacement()
    {
        TaskbarGeometry geometry = new(
            new PixelRectangle(0, 0, 3840, 48),
            TaskbarEdge.Bottom,
            96);

        TaskbarWidgetPlacement placement = TaskbarWidgetPlacementCalculator.Calculate(geometry);

        AssertEx.Equal(true, placement.IsVisible);
        AssertEx.Equal(new PixelRectangle(5, 5, 257, 43), placement.Bounds);
    }

    private static void VerifyScaledSecondaryMonitorPlacement()
    {
        TaskbarGeometry geometry = new(
            new PixelRectangle(0, 0, 2560, 72),
            TaskbarEdge.Bottom,
            144);

        TaskbarWidgetPlacement placement = TaskbarWidgetPlacementCalculator.Calculate(geometry);

        AssertEx.Equal(true, placement.IsVisible);
        AssertEx.Equal(new PixelRectangle(8, 8, 386, 64), placement.Bounds);
    }

    private static void VerifyVerticalTaskbarPlacement()
    {
        TaskbarGeometry geometry = new(
            new PixelRectangle(0, 0, 48, 1080),
            TaskbarEdge.Left,
            96);

        TaskbarWidgetPlacement placement = TaskbarWidgetPlacementCalculator.Calculate(geometry);

        AssertEx.Equal(true, placement.IsVisible);
        AssertEx.Equal(new PixelRectangle(5, 5, 43, 121), placement.Bounds);
    }

    private static void VerifyUnavailableTaskbarPlacement()
    {
        TaskbarGeometry geometry = new(
            new PixelRectangle(0, 0, 0, 0),
            TaskbarEdge.Bottom,
            96);

        TaskbarWidgetPlacement placement = TaskbarWidgetPlacementCalculator.Calculate(geometry);

        AssertEx.Equal(false, placement.IsVisible);
    }

    private static LayerTelemetryState CreateLayerState()
    {
        return new LayerTelemetryState(
            42,
            7,
            new LayerDefinition(3, "Navigation"),
            null,
            1,
            LayerStateIndicators.None);
    }

    private static BatteryTelemetryState CreateBatteryState(BatteryReadingStatus status)
    {
        return new BatteryTelemetryState(
            42,
            9,
            new BatteryReading(63, status),
            new BatteryReading(81, status));
    }
}
