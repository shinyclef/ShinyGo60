namespace ShinyGo60.Companion.Core.Presentation;

public sealed record CompanionDisplayState(
    CompanionDisplayConnectionState ConnectionState,
    string LayerName,
    string ConnectionLabel,
    string TransportLabel,
    CompanionBatteryDisplay LeftBattery,
    CompanionBatteryDisplay RightBattery,
    string Detail);
