using System.Diagnostics.CodeAnalysis;
using ShinyGo60.Protocol;
using ShinyGo60.Protocol.Manifests;
using ShinyGo60.Protocol.Messages;

namespace ShinyGo60.Companion.Core.Telemetry;

public sealed class BatteryStateTracker
{
    private const BatteryStateIndicators KnownIndicators =
        BatteryStateIndicators.LeftAvailable |
        BatteryStateIndicators.LeftStale |
        BatteryStateIndicators.RightAvailable |
        BatteryStateIndicators.RightStale;

    private readonly object syncRoot = new();
    private readonly LayoutFingerprint manifestFingerprint;
    private uint sessionId;
    private BatteryTelemetryState? currentState;

    public BatteryStateTracker(LayoutManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        LayoutManifestJson.Validate(manifest);
        if (manifest.ProtocolVersion != ProtocolVersion.Current)
        {
            throw new InvalidDataException(
                $"Manifest protocol {manifest.ProtocolVersion} is unsupported; expected {ProtocolVersion.Current}.");
        }

        this.manifestFingerprint = LayoutFingerprint.FromLayoutIdentifier(manifest.LayoutIdentifier);
    }

    public event EventHandler<BatteryTelemetryStateChangedEventArgs>? StateChanged;

    public BatteryTelemetryState? CurrentState
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

        if ((hello.SelectedCapabilities & ProtocolCapability.BatteryTelemetry) == 0)
        {
            throw new InvalidDataException("The firmware did not negotiate battery telemetry.");
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

    public BatteryTelemetryApplyResult Apply(ProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message switch
        {
            ProtocolMessage.BatterySnapshot snapshot => this.ApplyState(snapshot.SessionId, snapshot.State, isSnapshot: true),
            ProtocolMessage.BatteryChanged changed => this.ApplyState(changed.SessionId, changed.State, isSnapshot: false),
            _ => throw new ArgumentException($"Message type {message.Type} does not contain battery telemetry.", nameof(message)),
        };
    }

    private BatteryTelemetryApplyResult ApplyState(
        uint messageSessionId,
        ProtocolMessage.BatteryState state,
        bool isSnapshot)
    {
        BatteryTelemetryState appliedState;
        BatteryTelemetryApplyResult result;

        lock (this.syncRoot)
        {
            if (this.sessionId == 0)
            {
                return BatteryTelemetryApplyResult.NoSession;
            }

            if (messageSessionId != this.sessionId)
            {
                return BatteryTelemetryApplyResult.WrongSession;
            }

            if (!isSnapshot && this.currentState is null)
            {
                return BatteryTelemetryApplyResult.AwaitingSnapshot;
            }

            if (!TryResolveState(messageSessionId, state, out BatteryTelemetryState? resolvedState))
            {
                return BatteryTelemetryApplyResult.InvalidState;
            }

            BatteryTelemetryState? previousState = this.currentState;
            if (previousState is not null)
            {
                if (resolvedState.Revision < previousState.Revision)
                {
                    return BatteryTelemetryApplyResult.StaleRevision;
                }

                if (resolvedState.Revision == previousState.Revision)
                {
                    return resolvedState == previousState
                        ? BatteryTelemetryApplyResult.Duplicate
                        : BatteryTelemetryApplyResult.ConflictingRevision;
                }
            }

            bool revisionGap = previousState is not null && resolvedState.Revision - previousState.Revision > 1;
            this.currentState = resolvedState;
            appliedState = resolvedState;
            result = isSnapshot && !revisionGap
                ? BatteryTelemetryApplyResult.AppliedSnapshot
                : revisionGap
                    ? BatteryTelemetryApplyResult.AppliedAfterGap
                    : BatteryTelemetryApplyResult.Applied;
        }

        this.StateChanged?.Invoke(this, new BatteryTelemetryStateChangedEventArgs(appliedState));
        return result;
    }

    private static bool TryResolveState(
        uint messageSessionId,
        ProtocolMessage.BatteryState state,
        [NotNullWhen(true)] out BatteryTelemetryState? resolvedState)
    {
        resolvedState = null;
        if (state.Revision == 0 || (state.Indicators & ~KnownIndicators) != 0 ||
            !TryResolveReading(
                state.LeftLevel,
                state.Indicators.HasFlag(BatteryStateIndicators.LeftAvailable),
                state.Indicators.HasFlag(BatteryStateIndicators.LeftStale),
                out BatteryReading? left) ||
            !TryResolveReading(
                state.RightLevel,
                state.Indicators.HasFlag(BatteryStateIndicators.RightAvailable),
                state.Indicators.HasFlag(BatteryStateIndicators.RightStale),
                out BatteryReading? right))
        {
            return false;
        }

        resolvedState = new BatteryTelemetryState(messageSessionId, state.Revision, left, right);
        return true;
    }

    private static bool TryResolveReading(
        byte level,
        bool available,
        bool stale,
        [NotNullWhen(true)] out BatteryReading? reading)
    {
        if (!available)
        {
            reading = level == 0 && !stale
                ? new BatteryReading(null, BatteryReadingStatus.Unavailable)
                : null;
            return reading is not null;
        }

        if (level > 100)
        {
            reading = null;
            return false;
        }

        reading = new BatteryReading(level, stale ? BatteryReadingStatus.Stale : BatteryReadingStatus.Fresh);
        return true;
    }
}
