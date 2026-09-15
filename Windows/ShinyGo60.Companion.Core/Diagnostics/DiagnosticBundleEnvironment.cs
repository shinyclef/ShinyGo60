namespace ShinyGo60.Companion.Core.Diagnostics;

public sealed record DiagnosticBundleEnvironment(
    string CompanionVersion, string OperatingSystem, string RuntimeVersion, string ProtocolVersion,
    string LayoutIdentifier, string AdapterCollectionStatus, IReadOnlyList<BluetoothAdapterDiagnostic> BluetoothAdapters);
