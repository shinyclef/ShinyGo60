using System.Diagnostics.CodeAnalysis;
using ShinyGo60.Protocol;
using ShinyGo60.Protocol.Manifests;
using ShinyGo60.Protocol.Messages;

namespace ShinyGo60.Companion.Core.Telemetry;

public sealed class LayerStateTracker
{
    private const LayerStateIndicators KnownIndicators =
        LayerStateIndicators.PersistentLayerActive | LayerStateIndicators.MomentaryLayerActive;

    private readonly object syncRoot = new();
    private readonly LayoutFingerprint manifestFingerprint;
    private readonly IReadOnlyList<LayerDefinition> layers;
    private uint sessionId;
    private LayerTelemetryState? currentState;

    public LayerStateTracker(LayoutManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        LayoutManifestJson.Validate(manifest);
        if (manifest.ProtocolVersion != ProtocolVersion.Current)
        {
            throw new InvalidDataException(
                $"Manifest protocol {manifest.ProtocolVersion} is unsupported; expected {ProtocolVersion.Current}.");
        }

        if (manifest.Layers.Count > ProtocolPacketCodec.NoLayer)
        {
            throw new InvalidDataException(
                $"The layout manifest has {manifest.Layers.Count} layers; protocol v1 supports at most {ProtocolPacketCodec.NoLayer}.");
        }

        this.manifestFingerprint = LayoutFingerprint.FromLayoutIdentifier(manifest.LayoutIdentifier);
        this.layers = manifest.Layers;
    }

    public event EventHandler<LayerTelemetryStateChangedEventArgs>? StateChanged;

    public LayerTelemetryState? CurrentState
    {
        get
        {
            lock (this.syncRoot)
            {
                return this.currentState;
            }
        }
    }

    public void BeginSession(ProtocolMessage.HelloResult hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        if (hello.Status != HelloStatus.Success)
        {
            throw new InvalidDataException(
                $"The firmware rejected the manifest-bound handshake: {hello.Status}; firmware layout {hello.Layout}.");
        }

        if (hello.Layout != this.manifestFingerprint)
        {
            throw new InvalidDataException(
                $"Firmware layout {hello.Layout} does not match the loaded manifest {this.manifestFingerprint}.");
        }

        if ((hello.SelectedCapabilities & ProtocolCapability.StateTelemetry) == 0)
        {
            throw new InvalidDataException("The firmware did not negotiate effective-layer telemetry.");
        }

        lock (this.syncRoot)
        {
            this.sessionId = hello.SessionId;
            this.currentState = null;
        }
    }

    public void EndSession()
    {
        lock (this.syncRoot)
        {
            this.sessionId = 0;
            this.currentState = null;
        }
    }

    public LayerTelemetryApplyResult Apply(ProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message switch
        {
            ProtocolMessage.StateSnapshot snapshot => this.ApplyState(snapshot.SessionId, snapshot.State, isSnapshot: true),
            ProtocolMessage.LayerChanged changed => this.ApplyState(changed.SessionId, changed.State, isSnapshot: false),
            _ => throw new ArgumentException($"Message type {message.Type} does not contain layer telemetry.", nameof(message)),
        };
    }

    private LayerTelemetryApplyResult ApplyState(
        uint messageSessionId,
        ProtocolMessage.LayerState state,
        bool isSnapshot)
    {
        LayerTelemetryState appliedState;
        LayerTelemetryApplyResult result;

        lock (this.syncRoot)
        {
            if (this.sessionId == 0)
            {
                return LayerTelemetryApplyResult.NoSession;
            }

            if (messageSessionId != this.sessionId)
            {
                return LayerTelemetryApplyResult.WrongSession;
            }

            if (!isSnapshot && this.currentState is null)
            {
                return LayerTelemetryApplyResult.AwaitingSnapshot;
            }

            if (!this.TryResolveState(messageSessionId, state, out LayerTelemetryState? resolvedState))
            {
                return LayerTelemetryApplyResult.InvalidState;
            }

            LayerTelemetryState? previousState = this.currentState;
            if (previousState is not null)
            {
                if (resolvedState.Revision < previousState.Revision)
                {
                    return LayerTelemetryApplyResult.StaleRevision;
                }

                if (resolvedState.Revision == previousState.Revision)
                {
                    return resolvedState == previousState
                        ? LayerTelemetryApplyResult.Duplicate
                        : LayerTelemetryApplyResult.ConflictingRevision;
                }
            }

            bool revisionGap = previousState is not null &&
                               resolvedState.Revision - previousState.Revision > 1;
            this.currentState = resolvedState;
            appliedState = resolvedState;
            result = isSnapshot && !revisionGap
                ? LayerTelemetryApplyResult.AppliedSnapshot
                : revisionGap
                    ? LayerTelemetryApplyResult.AppliedAfterGap
                    : LayerTelemetryApplyResult.Applied;
        }

        this.StateChanged?.Invoke(this, new LayerTelemetryStateChangedEventArgs(appliedState));
        return result;
    }

    private bool TryResolveState(
        uint messageSessionId,
        ProtocolMessage.LayerState state,
        [NotNullWhen(true)] out LayerTelemetryState? resolvedState)
    {
        resolvedState = null;
        bool persistentIndicator = (state.Indicators & LayerStateIndicators.PersistentLayerActive) != 0;
        bool momentaryIndicator = (state.Indicators & LayerStateIndicators.MomentaryLayerActive) != 0;
        if (state.Revision == 0 || (state.Indicators & ~KnownIndicators) != 0 ||
            persistentIndicator != state.PersistentLayerId.HasValue ||
            momentaryIndicator != (state.MomentaryLayerCount != 0) ||
            !this.TryResolveLayer(state.EffectiveLayerId, out LayerDefinition? effectiveLayer))
        {
            return false;
        }

        LayerDefinition? persistentLayer = null;
        if (state.PersistentLayerId is byte persistentLayerId &&
            !this.TryResolveLayer(persistentLayerId, out persistentLayer))
        {
            return false;
        }

        resolvedState = new LayerTelemetryState(
            messageSessionId,
            state.Revision,
            effectiveLayer,
            persistentLayer,
            state.MomentaryLayerCount,
            state.Indicators);
        return true;
    }

    private bool TryResolveLayer(byte layerId, [NotNullWhen(true)] out LayerDefinition? layer)
    {
        if (layerId >= this.layers.Count)
        {
            layer = null;
            return false;
        }

        layer = this.layers[layerId];
        return true;
    }
}
