using System.Globalization;
using ShinyGo60.Protocol.Transport;

namespace ShinyGo60.Companion.Core.Diagnostics;

public static class ConnectionHistoryDescription
{
    public static Dictionary<string, string> Describe(ConnectionHistory.Entry entry)
    {
        string name = entry.Event switch
        {
            1 => "connected", 2 => "disconnected", 3 => "security_changed", 4 => "parameters_updated",
            5 => "parameters_requested", 6 => "interactive_lease_expired", 7 => "subscription_changed",
            8 => "response_queue_failed", 9 => "indication_failed", 10 => "indication_submit_failed", _ => "unknown",
        };
        string result = entry.Event switch
        {
            1 or 2 => entry.Result switch
            {
                0 => "success", 8 => "connection timeout", 19 => "remote user terminated connection",
                22 => "local host terminated connection", 34 => "link layer response timeout", 40 => "Instant Passed (link-control timing failure)",
                62 => "connection establishment failed", _ => "unrecognized HCI code",
            },
            3 => entry.Result switch
            {
                0 => "security established", 1 => "authentication failed", 2 => "PIN or key missing", 3 => "OOB data unavailable",
                4 => "security level not reached", 5 => "pairing unsupported", 6 => "pairing not allowed", 7 => "invalid parameters",
                8 => "key rejected", 9 => "unspecified security failure", _ => "unrecognized security code",
            },
            5 or 10 => entry.Result switch
            {
                0 => "request accepted", -11 => "try again", -12 => "out of memory", -16 => "busy",
                -105 => "no buffers", -128 => "not connected", -120 => "already in progress", _ => "unrecognized API result",
            },
            8 => "response queue could not accept the packet",
            9 => entry.Result switch
            {
                0 => "indication confirmed", 1 => "invalid ATT handle", 5 => "insufficient authentication",
                8 => "insufficient authorization", 14 => "unspecified ATT error", 15 => "insufficient encryption",
                17 => "insufficient ATT resources", _ => "unrecognized ATT indication error",
            },
            _ => entry.Result == 0 ? "success" : "unrecognized result",
        };
        Dictionary<string, string> values = new()
        {
            ["event"] = name, ["description"] = $"{name}: {result}", ["resultDescription"] = result,
            ["result"] = entry.Result.ToString(CultureInfo.InvariantCulture), ["eventCode"] = entry.Event.ToString(CultureInfo.InvariantCulture),
            ["connection"] = entry.Connection == 63 ? "unknown" : entry.Connection.ToString(CultureInfo.InvariantCulture),
            ["role"] = entry.Role switch { 0 => "central (split link)", 1 => "peripheral (host link)", _ => "unknown" },
            ["a"] = entry.A.ToString(CultureInfo.InvariantCulture), ["b"] = entry.B.ToString(CultureInfo.InvariantCulture),
            ["c"] = entry.C.ToString(CultureInfo.InvariantCulture), ["d"] = entry.D.ToString(CultureInfo.InvariantCulture),
        };
        if (entry.Event is 1 or 2 or 4)
        {
            values["intervalMs"] = (entry.A * 1.25).ToString(CultureInfo.InvariantCulture);
            values["latency"] = entry.B.ToString(CultureInfo.InvariantCulture);
            values["supervisionTimeoutMs"] = (entry.C * 10).ToString(CultureInfo.InvariantCulture);
        }
        else if (entry.Event == 5)
        {
            values["requestedMinIntervalMs"] = (entry.A * 1.25).ToString(CultureInfo.InvariantCulture);
            values["requestedMaxIntervalMs"] = (entry.B * 1.25).ToString(CultureInfo.InvariantCulture);
            values["requestedLatency"] = entry.C.ToString(CultureInfo.InvariantCulture);
            values["requestedSupervisionTimeoutMs"] = (entry.D * 10).ToString(CultureInfo.InvariantCulture);
        }
        else if (entry.Event == 3)
        {
            values["securityLevel"] = entry.A.ToString(CultureInfo.InvariantCulture);
        }

        return values;
    }
}
