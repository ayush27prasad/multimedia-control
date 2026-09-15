using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhoneBridge.Core.Protocol;

/// <summary>
/// Control-channel models. These mirror docs/PROTOCOL.md §4.3 and the Kotlin models in
/// <c>state/Models.kt</c>. All three must change together.
///
/// Every property has a default so a snapshot from a newer peer that adds fields still
/// deserialises - the two apps are built and deployed independently.
/// </summary>
public sealed record Envelope
{
    [JsonPropertyName("t")] public string Type { get; init; } = "";
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("origin")] public string Origin { get; init; } = Origins.Pc;
    [JsonPropertyName("ts")] public long Ts { get; init; }
    [JsonPropertyName("p")] public JsonElement Payload { get; init; }
}

public static class Origins
{
    public const string Phone = "phone";
    public const string Pc = "pc";
}

public static class MessageTypes
{
    public const string Hello = "hello";
    public const string State = "state";
    public const string Cmd = "cmd";
    public const string Ack = "ack";
    public const string Ping = "ping";
    public const string Pong = "pong";
}

public sealed record HelloPayload
{
    [JsonPropertyName("role")] public string Role { get; init; } = Origins.Pc;
    [JsonPropertyName("app")] public string App { get; init; } = "1.0.0";
    [JsonPropertyName("protocol")] public int Protocol { get; init; } = Wire.Version;
    [JsonPropertyName("device")] public string Device { get; init; } = "";
    [JsonPropertyName("android")] public string Android { get; init; } = "";
    [JsonPropertyName("caps")] public List<string> Caps { get; init; } = new();
}

public sealed record StatePayload
{
    public const string ScopeDevice = "device";
    public const string ScopeWifi = "wifi";

    [JsonPropertyName("scope")] public string Scope { get; init; } = ScopeWifi;
    [JsonPropertyName("camera")] public CameraState? Camera { get; init; }
    [JsonPropertyName("mic")] public MicState? Mic { get; init; }
    [JsonPropertyName("tether")] public TetherState? Tether { get; init; }
    [JsonPropertyName("wifi")] public WifiState? Wifi { get; init; }
    [JsonPropertyName("observedWifi")] public ObservedWifi? ObservedWifi { get; init; }
}

public sealed record CmdPayload
{
    [JsonPropertyName("cmd")] public string Cmd { get; init; } = "";
    [JsonPropertyName("args")] public JsonElement Args { get; init; }
}

public sealed record AckPayload
{
    [JsonPropertyName("ackSeq")] public long AckSeq { get; init; }
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

public sealed record PingPayload
{
    [JsonPropertyName("nonce")] public string Nonce { get; init; } = "";
}

// ---- device slice (phone is the authority) ---------------------------------

public sealed record CameraState
{
    public const string FacingBack = "back";
    public const string FacingFront = "front";

    [JsonPropertyName("streaming")] public bool Streaming { get; init; }
    [JsonPropertyName("facing")] public string Facing { get; init; } = FacingBack;
    [JsonPropertyName("width")] public int Width { get; init; } = 1920;
    [JsonPropertyName("height")] public int Height { get; init; } = 1080;
    [JsonPropertyName("fps")] public int Fps { get; init; } = 30;
    [JsonPropertyName("bitrateKbps")] public int BitrateKbps { get; init; } = 25_000;
    [JsonPropertyName("available")] public List<string> Available { get; init; } = new();
    [JsonPropertyName("error")] public string? Error { get; init; }
}

public sealed record MicState
{
    public const string SourceUnprocessed = "unprocessed";
    public const string SourceVoiceRecognition = "voice_recognition";

    [JsonPropertyName("streaming")] public bool Streaming { get; init; }
    [JsonPropertyName("muted")] public bool Muted { get; init; }
    [JsonPropertyName("source")] public string Source { get; init; } = SourceUnprocessed;
    [JsonPropertyName("sampleRate")] public int SampleRate { get; init; } = 48_000;
    [JsonPropertyName("channels")] public int Channels { get; init; } = 2;
    [JsonPropertyName("error")] public string? Error { get; init; }
}

public sealed record TetherState
{
    [JsonPropertyName("active")] public bool Active { get; init; }
    [JsonPropertyName("canDeepLink")] public bool CanDeepLink { get; init; } = true;
}

public sealed record DeviceState
{
    [JsonPropertyName("camera")] public CameraState Camera { get; init; } = new();
    [JsonPropertyName("mic")] public MicState Mic { get; init; } = new();
    [JsonPropertyName("tether")] public TetherState Tether { get; init; } = new();
}

// ---- wifi slice (PC is the authority) --------------------------------------

public sealed record SavedNetwork
{
    [JsonPropertyName("networkId")] public int NetworkId { get; init; } = -1;
    [JsonPropertyName("ssid")] public string Ssid { get; init; } = "";
    [JsonPropertyName("security")] public string Security { get; init; } = "";
}

public sealed record ScanResultEntry
{
    [JsonPropertyName("ssid")] public string Ssid { get; init; } = "";
    [JsonPropertyName("rssi")] public int Rssi { get; init; }
    [JsonPropertyName("security")] public string Security { get; init; } = "";
    [JsonPropertyName("frequencyMhz")] public int FrequencyMhz { get; init; }
}

/// <summary>
/// Authored here from <c>cmd wifi status</c> / <c>list-networks</c> / <c>list-scan-results</c>.
///
/// There is deliberately no Connect command: <c>cmd wifi connect-network</c> requires the
/// passphrase on every call and cannot activate a saved network by id. See DECISIONS.md D5.
/// </summary>
public sealed record WifiState
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("connected")] public bool Connected { get; init; }
    [JsonPropertyName("ssid")] public string? Ssid { get; init; }
    [JsonPropertyName("bssid")] public string? Bssid { get; init; }
    [JsonPropertyName("rssi")] public int? Rssi { get; init; }
    [JsonPropertyName("linkSpeedMbps")] public int? LinkSpeedMbps { get; init; }
    [JsonPropertyName("frequencyMhz")] public int? FrequencyMhz { get; init; }
    [JsonPropertyName("networkId")] public int? NetworkId { get; init; }
    [JsonPropertyName("savedNetworks")] public List<SavedNetwork> SavedNetworks { get; init; } = new();
    [JsonPropertyName("scanResults")] public List<ScanResultEntry> ScanResults { get; init; } = new();
    [JsonPropertyName("lastError")] public string? LastError { get; init; }
    [JsonPropertyName("lastUpdated")] public long LastUpdated { get; init; }
}

/// <summary>Phone-side telemetry. Supplementary - the ADB-derived view stays authoritative.</summary>
public sealed record ObservedWifi
{
    [JsonPropertyName("ssid")] public string? Ssid { get; init; }
    [JsonPropertyName("rssi")] public int? Rssi { get; init; }
    [JsonPropertyName("linkSpeedMbps")] public int? LinkSpeedMbps { get; init; }
    [JsonPropertyName("frequencyMhz")] public int? FrequencyMhz { get; init; }
    [JsonPropertyName("inRange")] public List<ScanResultEntry> InRange { get; init; } = new();
    [JsonPropertyName("locationPermitted")] public bool LocationPermitted { get; init; }
}

public static class Commands
{
    // Executed here (PC is the wifi authority).
    public const string WifiSetEnabled = "wifi.setEnabled";
    public const string WifiRefresh = "wifi.refresh";
    public const string WifiScan = "wifi.scan";

    // Forwarded to the phone (phone is the device authority).
    public const string CameraSetStreaming = "camera.setStreaming";
    public const string CameraSetFacing = "camera.setFacing";
    public const string CameraSetFormat = "camera.setFormat";
    public const string MicSetStreaming = "mic.setStreaming";
    public const string MicSetMuted = "mic.setMuted";
    public const string MicSetSource = "mic.setSource";
    public const string TetherOpenSettings = "tether.openSettings";
}

public static class BridgeJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // A newer peer may add fields; ignoring them is what keeps the two apps
        // independently deployable.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
