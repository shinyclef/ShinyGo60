using System.Globalization;
using ShinyGo60.Companion.Core.Diagnostics;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace ShinyGo60.Platform.Windows.Diagnostics;

public static class BluetoothAdapterDiagnostics
{
    private const string InstanceId = "System.Devices.DeviceInstanceId";
    private const string Manufacturer = "System.Devices.DeviceManufacturer";
    // DEVPKEY_Device_DriverDate, DriverVersion and DriverProvider from devpkey.h.
    private const string DriverDate = "{a8b865dd-2e3d-4094-ad97-e593a70c75d6} 2";
    private const string DriverVersion = "{a8b865dd-2e3d-4094-ad97-e593a70c75d6} 3";
    private const string DriverProvider = "{a8b865dd-2e3d-4094-ad97-e593a70c75d6} 9";

    public static async Task<IReadOnlyList<BluetoothAdapterDiagnostic>> ReadAsync(CancellationToken cancellationToken = default)
    {
        DeviceInformationCollection interfaces = await DeviceInformation.FindAllAsync(BluetoothAdapter.GetDeviceSelector(), [InstanceId])
            .AsTask(cancellationToken).ConfigureAwait(false);
        List<BluetoothAdapterDiagnostic> result = [];
        foreach (DeviceInformation adapter in interfaces)
        {
            if (adapter.Properties.GetValueOrDefault(InstanceId) is not string instance)
            {
                result.Add(new BluetoothAdapterDiagnostic("Bluetooth adapter (device details unavailable)", null, null, null, null));
                continue;
            }

            DeviceInformation device = await DeviceInformation.CreateFromIdAsync(instance,
                [Manufacturer, DriverDate, DriverVersion, DriverProvider], DeviceInformationKind.Device)
                .AsTask(cancellationToken).ConfigureAwait(false);
            result.Add(new BluetoothAdapterDiagnostic(device.Name, Text(device, Manufacturer), Text(device, DriverProvider),
                Text(device, DriverVersion), Text(device, DriverDate)));
        }

        return result;
    }

    private static string? Text(DeviceInformation device, string property) => device.Properties.GetValueOrDefault(property) switch
    {
        null => null,
        DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
        object value => Convert.ToString(value, CultureInfo.InvariantCulture),
    };
}
