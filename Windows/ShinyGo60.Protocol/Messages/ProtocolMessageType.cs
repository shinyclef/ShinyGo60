namespace ShinyGo60.Protocol.Messages;

public enum ProtocolMessageType : byte
{
    Hello = 0x01,
    HelloResult = 0x02,
    GetState = 0x03,
    StateSnapshot = 0x04,
    LayerChanged = 0x05,
    GetBattery = 0x06,
    BatterySnapshot = 0x07,
    BatteryChanged = 0x08,
    SetPersistentLayer = 0x10,
    PressMomentaryLayer = 0x11,
    RenewMomentaryLayer = 0x12,
    ReleaseMomentaryLayer = 0x13,
    SetBluetoothConnectionMode = 0x14,
    CommandResult = 0x20,
    Error = 0x7F,
}
