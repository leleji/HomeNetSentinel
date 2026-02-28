// See https://aka.ms/new-console-template for more information
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.IO;
using System.Net;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;

var settings = LoadSettings();
if (!settings.IsValid(out var settingsError))
{
    Console.Error.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Config error: {settingsError}");
    Console.Error.WriteLine("Required: broker_host, broker_port, at least one target (name+ip). Interfaces use defaults when empty.");
    return;
}

var brokerHost = settings.BrokerHost;
var brokerPort = settings.BrokerPort;
var username = settings.Username;
var password = settings.Password;

const string discoveryPrefix = "homeassistant";
const string baseTopic = "lan/presence";
var availabilityTopic = $"{baseTopic}/bridge/status";
var aggregateTopic = $"{baseTopic}/all";
const string aggregateDiscoveryId = "lan_presence_all";
var wanInterface = settings.WanInterface;
var wanStatusInterface = settings.WanStatusInterface;
var wanIpv6StatusInterface = settings.WanIpv6StatusInterface;

var targets = settings.Targets.ToArray();

var lastStates = targets.ToDictionary(t => ToTargetId(t.Ip), _ => (string?)null);
var hasPublishedAggregate = false;
var currentTargetIds = targets.Select(t => ToTargetId(t.Ip)).ToHashSet(StringComparer.Ordinal);
var targetStateFile = "/etc/homenet-sentinel/target_ids";
var wanId = ToTargetId(wanInterface);
var wanDownloadTopic = $"{baseTopic}/wan/{wanId}/download_bps";
var wanUploadTopic = $"{baseTopic}/wan/{wanId}/upload_bps";
var wanConnCountTopic = $"{baseTopic}/wan/{wanId}/conntrack_count";
var wanRxBytesTotalTopic = $"{baseTopic}/wan/{wanId}/rx_gb_total";
var wanTxBytesTotalTopic = $"{baseTopic}/wan/{wanId}/tx_gb_total";
var wanIpv4Topic = $"{baseTopic}/wan/{wanId}/ipv4";
var wanIpv6PdTopic = $"{baseTopic}/wan/{wanId}/ipv6_pd";
var wanMonitor = TryCreateWanStatsMonitor(wanInterface);
var wanStatusMonitor = new WanInterfaceStatusMonitor(wanStatusInterface);
var wanIpv6StatusMonitor = new WanInterfaceStatusMonitor(wanIpv6StatusInterface);

ulong? lastRxTotalGb = null;
ulong? lastTxTotalGb = null;
string? lastIpv4 = null;
string? lastIpv6Pd = null;
string? lastDownloadRate = null;
string? lastUploadRate = null;
DateTimeOffset lastConnPublishTime = DateTimeOffset.MinValue;

var client = new MqttClientFactory().CreateMqttClient();
var optionsBuilder = new MqttClientOptionsBuilder()
    .WithClientId($"lan_presence_{Guid.NewGuid():N}")
    .WithTcpServer(brokerHost, brokerPort)
    .WithWillTopic(availabilityTopic)
    .WithWillPayload("offline")
    .WithWillRetain(true)
    .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);

if (!string.IsNullOrWhiteSpace(username) || !string.IsNullOrWhiteSpace(password))
{
    optionsBuilder.WithCredentials(username, password);
}

var options = optionsBuilder.Build();

await EnsureConnected(client, options);
await PublishRetainedText(client, availabilityTopic, "online");

var previousTargetIds = LoadTargetIds(targetStateFile);
foreach (var removedTargetId in previousTargetIds)
{
    if (currentTargetIds.Contains(removedTargetId))
    {
        continue;
    }

    var removedTopic = $"{discoveryPrefix}/sensor/{removedTargetId}/config";
    await PublishRetainedText(client, removedTopic, "");
}

foreach (var target in targets)
{
    var targetId = ToTargetId(target.Ip);
    var legacyDiscoveryTopic = $"{discoveryPrefix}/device_tracker/{targetId}/config";
    await PublishRetainedText(client, legacyDiscoveryTopic, "");

    var legacyBinarySensorTopic = $"{discoveryPrefix}/binary_sensor/{targetId}/config";
    await PublishRetainedText(client, legacyBinarySensorTopic, "");

    var targetDiscoveryTopic = $"{discoveryPrefix}/sensor/{targetId}/config";
    await PublishRetainedText(client, targetDiscoveryTopic, "");
    var targetDiscoveryJson = BuildTargetSensorDiscoveryJson(target, aggregateTopic, availabilityTopic);
    await PublishRetainedText(client, targetDiscoveryTopic, targetDiscoveryJson);
}

SaveTargetIds(targetStateFile, currentTargetIds);

var aggregateDiscoveryTopic = $"{discoveryPrefix}/sensor/{aggregateDiscoveryId}/config";
await PublishRetainedText(client, aggregateDiscoveryTopic, "");

var wanDeviceId = "lan_presence_wan";
var wanDownloadDiscoveryTopic = $"{discoveryPrefix}/sensor/lan_presence_{wanId}_download/config";
var wanDownloadDiscoveryJson = BuildNumericSensorDiscoveryJson(
    name: "下载速率",
    uniqueId: $"lan_presence_{wanId}_download",
    stateTopic: wanDownloadTopic,
    availabilityTopic: availabilityTopic,
    icon: "mdi:download-network",
    unitOfMeasurement: "MB/s",
    stateClass: "measurement",
    deviceClass: "data_rate",
    deviceIdentifier: wanDeviceId,
    deviceName: "HomeNet Sentinel");
await PublishRetainedText(client, wanDownloadDiscoveryTopic, wanDownloadDiscoveryJson);

var wanUploadDiscoveryTopic = $"{discoveryPrefix}/sensor/lan_presence_{wanId}_upload/config";
var wanUploadDiscoveryJson = BuildNumericSensorDiscoveryJson(
    name: "上传速率",
    uniqueId: $"lan_presence_{wanId}_upload",
    stateTopic: wanUploadTopic,
    availabilityTopic: availabilityTopic,
    icon: "mdi:upload-network",
    unitOfMeasurement: "MB/s",
    stateClass: "measurement",
    deviceClass: "data_rate",
    deviceIdentifier: wanDeviceId,
    deviceName: "HomeNet Sentinel");
await PublishRetainedText(client, wanUploadDiscoveryTopic, wanUploadDiscoveryJson);

var wanConnDiscoveryTopic = $"{discoveryPrefix}/sensor/lan_presence_{wanId}_conntrack/config";
var wanConnDiscoveryJson = BuildNumericSensorDiscoveryJson(
    name: "连接数",
    uniqueId: $"lan_presence_{wanId}_conntrack",
    stateTopic: wanConnCountTopic,
    availabilityTopic: availabilityTopic,
    icon: "mdi:connection",
    unitOfMeasurement: "conn",
    stateClass: "measurement",
    deviceClass: null,
    deviceIdentifier: wanDeviceId,
    deviceName: "HomeNet Sentinel");
await PublishRetainedText(client, wanConnDiscoveryTopic, wanConnDiscoveryJson);

var wanUptimeDiscoveryTopic = $"{discoveryPrefix}/sensor/lan_presence_{wanId}_uptime_seconds/config";
await PublishRetainedText(client, wanUptimeDiscoveryTopic, "");
var wanUptimeHumanDiscoveryTopic = $"{discoveryPrefix}/sensor/lan_presence_{wanId}_uptime_human/config";
await PublishRetainedText(client, wanUptimeHumanDiscoveryTopic, "");

var legacyWanRxBytesDiscoveryTopic = $"{discoveryPrefix}/sensor/lan_presence_{wanId}_rx_bytes_total/config";
await PublishRetainedText(client, legacyWanRxBytesDiscoveryTopic, "");
var wanRxBytesDiscoveryTopic = $"{discoveryPrefix}/sensor/lan_presence_{wanId}_rx_gb_total/config";
var wanRxBytesDiscoveryJson = BuildNumericSensorDiscoveryJson(
    name: "接收总流量",
    uniqueId: $"lan_presence_{wanId}_rx_gb_total",
    stateTopic: wanRxBytesTotalTopic,
    availabilityTopic: availabilityTopic,
    icon: "mdi:database-arrow-down",
    unitOfMeasurement: "GB",
    stateClass: "total_increasing",
    deviceClass: null,
    deviceIdentifier: wanDeviceId,
    deviceName: "HomeNet Sentinel");
await PublishRetainedText(client, wanRxBytesDiscoveryTopic, wanRxBytesDiscoveryJson);

var legacyWanTxBytesDiscoveryTopic = $"{discoveryPrefix}/sensor/lan_presence_{wanId}_tx_bytes_total/config";
await PublishRetainedText(client, legacyWanTxBytesDiscoveryTopic, "");
var wanTxBytesDiscoveryTopic = $"{discoveryPrefix}/sensor/lan_presence_{wanId}_tx_gb_total/config";
var wanTxBytesDiscoveryJson = BuildNumericSensorDiscoveryJson(
    name: "发送总流量",
    uniqueId: $"lan_presence_{wanId}_tx_gb_total",
    stateTopic: wanTxBytesTotalTopic,
    availabilityTopic: availabilityTopic,
    icon: "mdi:database-arrow-up",
    unitOfMeasurement: "GB",
    stateClass: "total_increasing",
    deviceClass: null,
    deviceIdentifier: wanDeviceId,
    deviceName: "HomeNet Sentinel");
await PublishRetainedText(client, wanTxBytesDiscoveryTopic, wanTxBytesDiscoveryJson);

var wanRxPacketsDiscoveryTopic = $"{discoveryPrefix}/sensor/lan_presence_{wanId}_rx_packets_total/config";
await PublishRetainedText(client, wanRxPacketsDiscoveryTopic, "");

var wanTxPacketsDiscoveryTopic = $"{discoveryPrefix}/sensor/lan_presence_{wanId}_tx_packets_total/config";
await PublishRetainedText(client, wanTxPacketsDiscoveryTopic, "");

var wanIpv4DiscoveryTopic = $"{discoveryPrefix}/sensor/lan_presence_{wanId}_ipv4/config";
var wanIpv4DiscoveryJson = BuildTextSensorDiscoveryJson(
    name: "IPv4 地址",
    uniqueId: $"lan_presence_{wanId}_ipv4",
    stateTopic: wanIpv4Topic,
    availabilityTopic: availabilityTopic,
    icon: "mdi:ip-network-outline",
    deviceIdentifier: wanDeviceId,
    deviceName: "HomeNet Sentinel");
await PublishRetainedText(client, wanIpv4DiscoveryTopic, wanIpv4DiscoveryJson);

var legacyWanIpv6DiscoveryTopic = $"{discoveryPrefix}/sensor/lan_presence_{wanId}_ipv6/config";
await PublishRetainedText(client, legacyWanIpv6DiscoveryTopic, "");

var wanIpv6DiscoveryTopic = $"{discoveryPrefix}/sensor/lan_presence_{wanId}_ipv6_pd/config";
var wanIpv6DiscoveryJson = BuildTextSensorDiscoveryJson(
    name: "IPv6-PD",
    uniqueId: $"lan_presence_{wanId}_ipv6_pd",
    stateTopic: wanIpv6PdTopic,
    availabilityTopic: availabilityTopic,
    icon: "mdi:ip-outline",
    deviceIdentifier: wanDeviceId,
    deviceName: "HomeNet Sentinel");
await PublishRetainedText(client, wanIpv6DiscoveryTopic, wanIpv6DiscoveryJson);

if (wanMonitor == null)
{
    Console.WriteLine($"{DateTime.Now:HH:mm:ss} WAN monitor disabled. Interface '{wanInterface}' not found.");
}

while (true)
{
    var snapshots = new List<PresenceSnapshot>(targets.Length);

    foreach (var target in targets)
    {
        var nud = await GetNeighborState(target.Ip);
        var currentState = IsOnline(nud) ? "home" : "not_home";
        snapshots.Add(new PresenceSnapshot(target, currentState, nud, DateTimeOffset.UtcNow.ToString("O")));
    }

    var anyChanged = false;

    foreach (var snapshot in snapshots)
    {
        var target = snapshot.Target;
        var targetId = ToTargetId(target.Ip);
        var currentState = snapshot.State;
        var lastState = lastStates[targetId];

        if (currentState == lastState)
        {
            continue;
        }

        anyChanged = true;
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} {target.Name} ({target.Ip}) -> {currentState} (NUD={snapshot.Nud})");
        lastStates[targetId] = currentState;
    }

    if (anyChanged || !hasPublishedAggregate)
    {
        await EnsureConnected(client, options);
        var aggregateJson = BuildAggregateJson(snapshots, DateTimeOffset.UtcNow.ToString("O"));
        await PublishRetainedText(client, aggregateTopic, aggregateJson);
        hasPublishedAggregate = true;
    }

    if (wanMonitor != null && wanMonitor.TrySample(out var sample))
    {
        await EnsureConnected(client, options);
        var downloadMb = sample.DownloadBps / 1_000_000d;
        var uploadMb = sample.UploadBps / 1_000_000d;
        var rxTotalGb = sample.RxBytesTotal / 1_000_000_000UL;
        var txTotalGb = sample.TxBytesTotal / 1_000_000_000UL;
        var downloadRateText = downloadMb.ToString("F3", CultureInfo.InvariantCulture);
        var uploadRateText = uploadMb.ToString("F3", CultureInfo.InvariantCulture);

        if (lastDownloadRate != downloadRateText)
        {
            await PublishRetainedText(client, wanDownloadTopic, downloadRateText);
            lastDownloadRate = downloadRateText;
        }

        if (lastUploadRate != uploadRateText)
        {
            await PublishRetainedText(client, wanUploadTopic, uploadRateText);
            lastUploadRate = uploadRateText;
        }

        if (DateTimeOffset.UtcNow - lastConnPublishTime >= TimeSpan.FromSeconds(10))
        {
            await PublishRetainedText(client, wanConnCountTopic, sample.ConntrackCount.ToString(CultureInfo.InvariantCulture));
            lastConnPublishTime = DateTimeOffset.UtcNow;
        }

        if (lastRxTotalGb != rxTotalGb)
        {
            await PublishRetainedText(client, wanRxBytesTotalTopic, rxTotalGb.ToString(CultureInfo.InvariantCulture));
            lastRxTotalGb = rxTotalGb;
        }

        if (lastTxTotalGb != txTotalGb)
        {
            await PublishRetainedText(client, wanTxBytesTotalTopic, txTotalGb.ToString(CultureInfo.InvariantCulture));
            lastTxTotalGb = txTotalGb;
        }
    }

    var wanStatus = await wanStatusMonitor.TrySampleAsync();
    var wanIpv6Status = await wanIpv6StatusMonitor.TrySampleAsync();
    if (wanStatus != null)
    {
        await EnsureConnected(client, options);

        if (lastIpv4 != wanStatus.Ipv4)
        {
            await PublishRetainedText(client, wanIpv4Topic, wanStatus.Ipv4);
            lastIpv4 = wanStatus.Ipv4;
        }

    }

    var ipv6PdValue = NormalizeIpv6(wanIpv6Status?.Ipv6Pd);
    if (!string.IsNullOrWhiteSpace(ipv6PdValue) && lastIpv6Pd != ipv6PdValue)
    {
        await EnsureConnected(client, options);
        await PublishRetainedText(client, wanIpv6PdTopic, ipv6PdValue);
        lastIpv6Pd = ipv6PdValue;
    }

    await Task.Delay(1000);
}

static bool IsOnline(string nud) => nud is "REACHABLE" or "STALE" or "DELAY" or "PROBE" or "PERMANENT";

static HashSet<string> LoadTargetIds(string path)
{
    try
    {
        if (!File.Exists(path))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var ids = File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line));

        return new HashSet<string>(ids, StringComparer.Ordinal);
    }
    catch
    {
        return new HashSet<string>(StringComparer.Ordinal);
    }
}

static void SaveTargetIds(string path, IEnumerable<string> ids)
{
    try
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllLines(path, ids.OrderBy(id => id, StringComparer.Ordinal));
    }
    catch
    {
    }
}

static AppSettings LoadSettings()
{
    var brokerHost = (Environment.GetEnvironmentVariable("HNS_BROKER_HOST") ?? string.Empty).Trim();

    var brokerPortText = (Environment.GetEnvironmentVariable("HNS_BROKER_PORT") ?? string.Empty).Trim();
    var brokerPort = int.TryParse(brokerPortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort)
        ? parsedPort
        : 0;

    var username = Environment.GetEnvironmentVariable("HNS_MQTT_USERNAME") ?? string.Empty;
    var password = Environment.GetEnvironmentVariable("HNS_MQTT_PASSWORD") ?? string.Empty;

    var targetsText = Environment.GetEnvironmentVariable("HNS_TARGETS") ?? string.Empty;
    var targets = ParseTargets(targetsText);

    var wanInterface = ReadWithDefault("HNS_WAN_INTERFACE", "pppoe-wan");
    var wanStatusInterface = ReadWithDefault("HNS_WAN_STATUS_INTERFACE", "wan");
    var wanIpv6StatusInterface = ReadWithDefault("HNS_WAN_IPV6_STATUS_INTERFACE", "wan_6");

    return new AppSettings(
        brokerHost,
        brokerPort,
        username,
        password,
        targets,
        wanInterface,
        wanStatusInterface,
        wanIpv6StatusInterface);
}

static string ReadWithDefault(string envName, string fallback)
{
    var value = Environment.GetEnvironmentVariable(envName);
    return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

static List<PresenceTarget> ParseTargets(string raw)
{
    var result = new List<PresenceTarget>();
    if (string.IsNullOrWhiteSpace(raw))
    {
        return result;
    }

    var pairs = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var pair in pairs)
    {
        var fields = pair.Split('|', StringSplitOptions.None);
        if (fields.Length != 2)
        {
            continue;
        }

        var name = fields[0].Trim();
        var ip = fields[1].Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(ip))
        {
            continue;
        }

        if (!IPAddress.TryParse(ip, out _))
        {
            continue;
        }

        result.Add(new PresenceTarget(name, ip));
    }

    return result;
}

static string ToTargetId(string ip) => ip.Replace('.', '_');

static string? NormalizeIpv6(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return null;
    }

    if (string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase))
    {
        return null;
    }

    return value;
}

static async Task EnsureConnected(IMqttClient client, MqttClientOptions options)
{
    if (client.IsConnected)
    {
        return;
    }

    await client.ConnectAsync(options);
}

static async Task PublishRetainedText(IMqttClient client, string topic, string payload)
{
    var message = new MqttApplicationMessageBuilder()
        .WithTopic(topic)
        .WithPayload(Encoding.UTF8.GetBytes(payload))
        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
        .WithRetainFlag(true)
        .Build();

    await client.PublishAsync(message);
}

static string BuildTargetSensorDiscoveryJson(PresenceTarget target, string aggregateTopic, string availabilityTopic)
{
    var targetId = ToTargetId(target.Ip);

    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream))
    {
        writer.WriteStartObject();
        writer.WriteString("name", target.Name);
        writer.WriteString("unique_id", $"lan_presence_{targetId}");
        writer.WriteString("state_topic", aggregateTopic);
        writer.WriteString("value_template", $"{{{{ value_json.devices['{targetId}'].status if value_json.devices is defined and value_json.devices['{targetId}'] is defined else 'unknown' }}}}");
        writer.WriteString("json_attributes_topic", aggregateTopic);
        writer.WriteString("json_attributes_template", $"{{{{ value_json.devices['{targetId}'] | tojson if value_json.devices is defined and value_json.devices['{targetId}'] is defined else '{{}}' }}}}");
        writer.WriteString("availability_topic", availabilityTopic);
        writer.WriteString("payload_available", "online");
        writer.WriteString("payload_not_available", "offline");
        writer.WriteString("icon", "mdi:cellphone");

        writer.WritePropertyName("device");
        writer.WriteStartObject();
        writer.WritePropertyName("identifiers");
        writer.WriteStartArray();
        writer.WriteStringValue("lan_presence_wan");
        writer.WriteEndArray();
        writer.WriteString("name", "HomeNet Sentinel");
        writer.WriteString("manufacturer", "HomeNet Sentinel");
        writer.WriteString("model", "HomeNet Sentinel");
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    return Encoding.UTF8.GetString(stream.ToArray());
}

static string BuildNumericSensorDiscoveryJson(
    string name,
    string uniqueId,
    string stateTopic,
    string availabilityTopic,
    string icon,
    string unitOfMeasurement,
    string stateClass,
    string? deviceClass,
    string deviceIdentifier,
    string deviceName)
{
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream))
    {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString("unique_id", uniqueId);
        writer.WriteString("state_topic", stateTopic);
        writer.WriteString("availability_topic", availabilityTopic);
        writer.WriteString("payload_available", "online");
        writer.WriteString("payload_not_available", "offline");
        writer.WriteString("icon", icon);
        writer.WriteString("unit_of_measurement", unitOfMeasurement);
        writer.WriteString("state_class", stateClass);

        if (!string.IsNullOrWhiteSpace(deviceClass))
        {
            writer.WriteString("device_class", deviceClass);
        }

        writer.WritePropertyName("device");
        writer.WriteStartObject();
        writer.WritePropertyName("identifiers");
        writer.WriteStartArray();
        writer.WriteStringValue(deviceIdentifier);
        writer.WriteEndArray();
        writer.WriteString("name", deviceName);
        writer.WriteString("manufacturer", "HomeNet Sentinel");
        writer.WriteString("model", "HomeNet Sentinel");
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    return Encoding.UTF8.GetString(stream.ToArray());
}

static string BuildTextSensorDiscoveryJson(
    string name,
    string uniqueId,
    string stateTopic,
    string availabilityTopic,
    string icon,
    string deviceIdentifier,
    string deviceName)
{
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream))
    {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString("unique_id", uniqueId);
        writer.WriteString("state_topic", stateTopic);
        writer.WriteString("availability_topic", availabilityTopic);
        writer.WriteString("payload_available", "online");
        writer.WriteString("payload_not_available", "offline");
        writer.WriteString("icon", icon);

        writer.WritePropertyName("device");
        writer.WriteStartObject();
        writer.WritePropertyName("identifiers");
        writer.WriteStartArray();
        writer.WriteStringValue(deviceIdentifier);
        writer.WriteEndArray();
        writer.WriteString("name", deviceName);
        writer.WriteString("manufacturer", "HomeNet Sentinel");
        writer.WriteString("model", "HomeNet Sentinel");
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    return Encoding.UTF8.GetString(stream.ToArray());
}

static string BuildAggregateJson(List<PresenceSnapshot> snapshots, string timestamp)
{
    var onlineCount = snapshots.Count(s => s.State == "home");

    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream))
    {
        writer.WriteStartObject();
        writer.WriteString("timestamp", timestamp);
        writer.WriteNumber("online_count", onlineCount);
        writer.WriteNumber("total_count", snapshots.Count);
        writer.WritePropertyName("devices");
        writer.WriteStartObject();

        foreach (var snapshot in snapshots)
        {
            writer.WritePropertyName(ToTargetId(snapshot.Target.Ip));
            writer.WriteStartObject();
            writer.WriteString("name", snapshot.Target.Name);
            writer.WriteString("ip", snapshot.Target.Ip);
            writer.WriteString("status", snapshot.State);
            writer.WriteString("nud", snapshot.Nud);
            writer.WriteBoolean("online", snapshot.State == "home");
            writer.WriteString("updated_at", snapshot.Timestamp);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    return Encoding.UTF8.GetString(stream.ToArray());
}

static async Task<string> GetNeighborState(string ip)
{
    var psi = new ProcessStartInfo
    {
        FileName = "/sbin/ip",
        Arguments = $"neigh show {ip}",
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };

    try
    {
        using var process = Process.Start(psi);
        if (process == null)
        {
            return "UNKNOWN";
        }

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (string.IsNullOrWhiteSpace(output))
        {
            return "NONE";
        }

        var parts = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts[^1];
    }
    catch
    {
        return "ERROR";
    }
}

static WanStatsMonitor? TryCreateWanStatsMonitor(string interfaceName)
{
    return WanStatsMonitor.TryCreate(interfaceName);
}

sealed record WanInterfaceStatus(
    string Ipv4,
    string Ipv6Pd);

sealed record AppSettings(
    string BrokerHost,
    int BrokerPort,
    string Username,
    string Password,
    IReadOnlyList<PresenceTarget> Targets,
    string WanInterface,
    string WanStatusInterface,
    string WanIpv6StatusInterface)
{
    public bool IsValid(out string error)
    {
        if (string.IsNullOrWhiteSpace(BrokerHost))
        {
            error = "broker_host is empty";
            return false;
        }

        if (BrokerPort <= 0 || BrokerPort > 65535)
        {
            error = "broker_port must be a valid TCP port";
            return false;
        }

        if (Targets.Count == 0)
        {
            error = "targets is empty";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

sealed record PresenceTarget(string Name, string Ip);
sealed record PresenceSnapshot(PresenceTarget Target, string State, string Nud, string Timestamp);
sealed record WanSample(
    double DownloadBps,
    double UploadBps,
    long ConntrackCount,
    ulong RxBytesTotal,
    ulong TxBytesTotal);

sealed class WanInterfaceStatusMonitor(string interfaceName)
{
    private readonly string _interfaceName = interfaceName;

    public async Task<WanInterfaceStatus?> TrySampleAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/ubus",
            Arguments = $"call network.interface.{_interfaceName} status",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                return null;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            return Parse(output);
        }
        catch
        {
            return null;
        }
    }

    private static WanInterfaceStatus? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var ipv4 = "unknown";
            if (root.TryGetProperty("ipv4-address", out var ipv4Arr) && ipv4Arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in ipv4Arr.EnumerateArray())
                {
                    if (item.TryGetProperty("address", out var addr) && addr.ValueKind == JsonValueKind.String)
                    {
                        ipv4 = addr.GetString() ?? "unknown";
                        break;
                    }
                }
            }

            var ipv6Pd = "unknown";
            if (root.TryGetProperty("ipv6-prefix", out var prefixArr) && prefixArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in prefixArr.EnumerateArray())
                {
                    if (!item.TryGetProperty("address", out var addr) || addr.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var value = addr.GetString();
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    var mask = 0;
                    if (item.TryGetProperty("mask", out var maskEl) && maskEl.ValueKind == JsonValueKind.Number)
                    {
                        maskEl.TryGetInt32(out mask);
                    }

                    ipv6Pd = mask > 0 ? $"{value}/{mask}" : value;
                    break;
                }
            }

            return new WanInterfaceStatus(ipv4, ipv6Pd);
        }
        catch
        {
            return null;
        }
    }

    private static ulong TryGetULong(JsonElement obj, string property, ulong fallback)
    {
        if (!obj.TryGetProperty(property, out var element))
        {
            return fallback;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetUInt64(out var number))
        {
            return number;
        }

        return fallback;
    }
}

sealed class WanStatsMonitor(
    string rxFile,
    string txFile,
    string rxPacketsFile,
    string txPacketsFile,
    ulong initialRx,
    ulong initialTx,
    DateTimeOffset initialSampleTime)
{
    private ulong _lastRx = initialRx;
    private ulong _lastTx = initialTx;
    private DateTimeOffset _lastSampleTime = initialSampleTime;

    private readonly string _rxFile = rxFile;
    private readonly string _txFile = txFile;
    private readonly string _rxPacketsFile = rxPacketsFile;
    private readonly string _txPacketsFile = txPacketsFile;

    public static WanStatsMonitor? TryCreate(string interfaceName)
    {
        var rxFile = $"/sys/class/net/{interfaceName}/statistics/rx_bytes";
        var txFile = $"/sys/class/net/{interfaceName}/statistics/tx_bytes";
        var rxPacketsFile = $"/sys/class/net/{interfaceName}/statistics/rx_packets";
        var txPacketsFile = $"/sys/class/net/{interfaceName}/statistics/tx_packets";

        if (!File.Exists(rxFile) || !File.Exists(txFile) || !File.Exists(rxPacketsFile) || !File.Exists(txPacketsFile))
        {
            return null;
        }

        if (!TryReadULong(rxFile, out var initialRx) ||
            !TryReadULong(txFile, out var initialTx))
        {
            return null;
        }

        return new WanStatsMonitor(
            rxFile,
            txFile,
            rxPacketsFile,
            txPacketsFile,
            initialRx,
            initialTx,
            DateTimeOffset.UtcNow);
    }

    public bool TrySample(out WanSample sample)
    {
        sample = new WanSample(0, 0, 0, 0, 0);

        if (!TryReadULong(_rxFile, out var rxNow) || !TryReadULong(_txFile, out var txNow))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var elapsed = (now - _lastSampleTime).TotalSeconds;
        if (elapsed <= 0)
        {
            elapsed = 1;
        }

        var rxDelta = rxNow >= _lastRx ? rxNow - _lastRx : 0;
        var txDelta = txNow >= _lastTx ? txNow - _lastTx : 0;
        _lastRx = rxNow;
        _lastTx = txNow;
        _lastSampleTime = now;

        var conntrackCount = ReadLongOrDefault("/proc/sys/net/netfilter/nf_conntrack_count", 0);
        sample = new WanSample(rxDelta / elapsed, txDelta / elapsed, conntrackCount, rxNow, txNow);
        return true;
    }

    private static bool TryReadULong(string path, out ulong value)
    {
        value = 0;

        try
        {
            var text = File.ReadAllText(path).Trim();
            return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
        catch
        {
            return false;
        }
    }

    private static long ReadLongOrDefault(string path, long fallback)
    {
        try
        {
            var text = File.ReadAllText(path).Trim();
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
