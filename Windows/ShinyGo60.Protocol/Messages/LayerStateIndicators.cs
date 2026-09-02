namespace ShinyGo60.Protocol.Messages;

[Flags]
public enum LayerStateIndicators : byte
{
    None = 0,
    PersistentLayerActive = 1 << 0,
    MomentaryLayerActive = 1 << 1,
}
