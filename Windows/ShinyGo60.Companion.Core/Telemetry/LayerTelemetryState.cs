using ShinyGo60.Protocol.Manifests;
using ShinyGo60.Protocol.Messages;

namespace ShinyGo60.Companion.Core.Telemetry;

public sealed record LayerTelemetryState(
    uint SessionId,
    uint Revision,
    LayerDefinition EffectiveLayer,
    LayerDefinition? PersistentLayer,
    byte MomentaryLayerCount,
    LayerStateIndicators Indicators);
