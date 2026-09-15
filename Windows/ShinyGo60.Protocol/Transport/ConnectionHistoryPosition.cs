namespace ShinyGo60.Protocol.Transport;

public readonly record struct ConnectionHistoryPosition(uint BootId, uint CriticalSequence, uint RoutineSequence);
