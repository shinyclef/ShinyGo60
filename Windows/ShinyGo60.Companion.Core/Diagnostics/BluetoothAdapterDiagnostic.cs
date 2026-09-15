namespace ShinyGo60.Companion.Core.Diagnostics;

public sealed record BluetoothAdapterDiagnostic(
    string Name, string? Manufacturer, string? DriverProvider, string? DriverVersion, string? DriverDate);
