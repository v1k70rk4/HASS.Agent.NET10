using System.Text;
using System.Text.Json;
using HASS.Agent.Companion.Logging;
using MQTTnet.Diagnostics.PacketInspection;

namespace HASS.Agent.Companion.Mqtt;

/// <summary>
/// Looks at every MQTT packet as it goes on the wire, the bytes MQTTnet encoded, and keeps the
/// last ones. Now and then the broker closes the connection over a "malformed packet", and
/// Home Assistant gets a payload that is not valid JSON at the same moment; nothing on this
/// side said which packet it was. A packet that is broken before it leaves is reported at
/// once, with its bytes; when the broker closes the connection or a publish fails, the last
/// packets are written to a file next to the log, so the one the broker refused is among them.
/// </summary>
internal sealed class OutgoingPacketCheck
{
    private const int Kept = 30;
    private const int MaxDumps = 20;
    private const int FullHexUpTo = 16 * 1024;

    private readonly FileLog _log;
    private readonly string _directory;
    private readonly Queue<(DateTime Time, MqttPacketFlowDirection Direction, byte[] Bytes, string? Problem)> _recent = new();
    private readonly object _gate = new();
    private DateTime _lastDump = DateTime.MinValue;

    public OutgoingPacketCheck(FileLog log)
    {
        _log = log;
        _directory = Path.GetDirectoryName(log.FilePath) ?? AppContext.BaseDirectory;
    }

    /// <summary>The handler for MQTTnet's InspectPacketAsync.</summary>
    public Task InspectAsync(InspectMqttPacketEventArgs args)
    {
        var bytes = args.Buffer ?? [];
        var problem = args.Direction == MqttPacketFlowDirection.Outbound ? Check(bytes) : null;
        lock (_gate)
        {
            _recent.Enqueue((DateTime.Now, args.Direction, bytes, problem));
            while (_recent.Count > Kept)
            {
                _recent.Dequeue();
            }
        }

        if (problem is not null)
        {
            _log.Warning($"MQTT packet going out is broken: {problem}. Its bytes and the ones before it are in the packet file.");
            Dump("broken-packet");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// What is wrong with an encoded packet, or null when nothing: the remaining length against
    /// the bytes, and for a PUBLISH its topic, its MQTT 5 properties, and a JSON payload.
    /// </summary>
    internal static string? Check(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 2)
        {
            return "shorter than a fixed header";
        }

        var type = packet[0] >> 4;
        var position = 1;
        if (!TryReadVariableInteger(packet, ref position, out var remaining))
        {
            return "the remaining length is cut off or longer than four bytes";
        }

        if (position + remaining != packet.Length)
        {
            return $"type {type}: the remaining length says {remaining} bytes, {packet.Length - position} follow";
        }

        if (type != 3)
        {
            return null;
        }

        var qos = (packet[0] >> 1) & 3;
        if (qos == 3)
        {
            return "PUBLISH with QoS 3";
        }

        if (position + 2 > packet.Length)
        {
            return "PUBLISH: the topic length is cut off";
        }

        var topicLength = (packet[position] << 8) | packet[position + 1];
        position += 2;
        if (position + topicLength > packet.Length)
        {
            return $"PUBLISH: the topic is {topicLength} bytes, longer than the packet";
        }

        string topic;
        try
        {
            topic = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(packet.Slice(position, topicLength));
        }
        catch (DecoderFallbackException)
        {
            return "PUBLISH: the topic is not valid UTF-8";
        }

        if (topic.Length == 0 || topic.IndexOfAny(['+', '#', '\0']) >= 0)
        {
            return $"PUBLISH: unusable topic '{topic}'";
        }

        position += topicLength;
        if (qos > 0)
        {
            position += 2;
        }

        if (!TryReadVariableInteger(packet, ref position, out var propertiesLength) || position + propertiesLength > packet.Length)
        {
            return $"PUBLISH to {topic}: the properties run past the packet";
        }

        position += propertiesLength;
        var payload = packet[position..];
        if (payload.Length > 0 && (payload[0] == (byte)'{' || payload[0] == (byte)'['))
        {
            try
            {
                var reader = new Utf8JsonReader(payload);
                while (reader.Read())
                {
                }
            }
            catch (JsonException ex)
            {
                return $"PUBLISH to {topic}: the {payload.Length}-byte payload is not valid JSON ({ex.Message})";
            }
        }

        return null;
    }

    private static bool TryReadVariableInteger(ReadOnlySpan<byte> bytes, ref int position, out int value)
    {
        value = 0;
        var multiplier = 1;
        for (var count = 0; count < 4; count++)
        {
            if (position >= bytes.Length)
            {
                return false;
            }

            var b = bytes[position++];
            value += (b & 0x7F) * multiplier;
            if ((b & 0x80) == 0)
            {
                return true;
            }

            multiplier *= 128;
        }

        return false;
    }

    /// <summary>Writes the last packets, in both directions, to a file next to the log.</summary>
    public void Dump(string why)
    {
        (DateTime Time, MqttPacketFlowDirection Direction, byte[] Bytes, string? Problem)[] packets;
        lock (_gate)
        {
            // One file per incident: a failed publish and the disconnect after it are the same one.
            if (DateTime.UtcNow - _lastDump < TimeSpan.FromSeconds(5))
            {
                return;
            }

            _lastDump = DateTime.UtcNow;
            packets = [.. _recent];
        }

        try
        {
            var text = new StringBuilder();
            text.AppendLine($"MQTT packets before: {why}, {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            foreach (var (time, direction, bytes, problem) in packets)
            {
                text.AppendLine();
                text.AppendLine($"{time:HH:mm:ss.fff} {(direction == MqttPacketFlowDirection.Outbound ? "OUT" : "IN ")} {bytes.Length} B {Describe(bytes)}{(problem is null ? string.Empty : "  <<< " + problem)}");
                text.AppendLine(bytes.Length <= FullHexUpTo
                    ? Convert.ToHexString(bytes)
                    : $"{Convert.ToHexString(bytes, 0, 1024)} ... {Convert.ToHexString(bytes, bytes.Length - 256, 256)}");
            }

            var file = Path.Combine(_directory, $"mqtt-packets-{DateTime.Now:yyyyMMdd-HHmmss}-{why}.txt");
            File.WriteAllText(file, text.ToString());
            _log.Warning($"The last MQTT packets are in {Path.GetFileName(file)}.");

            foreach (var old in new DirectoryInfo(_directory).GetFiles("mqtt-packets-*.txt")
                         .OrderByDescending(info => info.LastWriteTimeUtc).Skip(MaxDumps))
            {
                old.Delete();
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Unable to write the MQTT packet file: {ex.Message}");
        }
    }

    // "PUBLISH hass.agent/sensors/.../state", for the file's headings.
    private static string Describe(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return "empty";
        }

        var type = bytes[0] >> 4;
        var name = type switch
        {
            1 => "CONNECT", 2 => "CONNACK", 3 => "PUBLISH", 4 => "PUBACK", 8 => "SUBSCRIBE", 9 => "SUBACK",
            12 => "PINGREQ", 13 => "PINGRESP", 14 => "DISCONNECT", _ => $"type {type}",
        };
        if (type != 3)
        {
            return name;
        }

        var position = 1;
        if (!TryReadVariableInteger(bytes, ref position, out _) || position + 2 > bytes.Length)
        {
            return name;
        }

        var topicLength = (bytes[position] << 8) | bytes[position + 1];
        return position + 2 + topicLength <= bytes.Length
            ? $"{name} {Encoding.UTF8.GetString(bytes, position + 2, topicLength)}"
            : name;
    }
}
