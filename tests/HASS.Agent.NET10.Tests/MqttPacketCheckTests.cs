using System.Text;
using HASS.Agent.Companion.Mqtt;

namespace HASS.Agent.Companion.Tests;

public class MqttPacketCheckTests
{
    // An MQTT 5 PUBLISH, QoS 0, no properties, built byte by byte.
    private static byte[] Publish(string topic, byte[] payload, int? claimedRemaining = null)
    {
        var topicBytes = Encoding.UTF8.GetBytes(topic);
        var body = new List<byte> { (byte)(topicBytes.Length >> 8), (byte)topicBytes.Length };
        body.AddRange(topicBytes);
        body.Add(0); // property length
        body.AddRange(payload);

        var packet = new List<byte> { 0x30 };
        var remaining = claimedRemaining ?? body.Count;
        do
        {
            var digit = (byte)(remaining % 128);
            remaining /= 128;
            packet.Add(remaining > 0 ? (byte)(digit | 0x80) : digit);
        }
        while (remaining > 0);

        packet.AddRange(body);
        return [.. packet];
    }

    [Fact]
    public void A_good_publish_passes()
    {
        var packet = Publish("hass.agent/sensors/abc/state", Encoding.UTF8.GetBytes("""{"cpu":12.5,"title":"Árvíztűrő"}"""));

        Assert.Null(OutgoingPacketCheck.Check(packet));
    }

    [Fact]
    public void A_long_payload_with_a_multi_byte_length_passes()
    {
        var payload = Encoding.UTF8.GetBytes("{\"x\":\"" + new string('a', 20_000) + "\"}");

        Assert.Null(OutgoingPacketCheck.Check(Publish("hass.agent/media_player/abc/state", payload)));
    }

    [Fact]
    public void A_wrong_remaining_length_is_caught()
    {
        var packet = Publish("hass.agent/sensors/abc/state", Encoding.UTF8.GetBytes("{\"a\":1}"), claimedRemaining: 10);

        Assert.Contains("remaining length", OutgoingPacketCheck.Check(packet));
    }

    [Fact]
    public void A_payload_that_is_not_json_is_caught()
    {
        var packet = Publish("hass.agent/sensors/abc/state", Encoding.UTF8.GetBytes("{\"cpu\":12.5,\"tit"));

        Assert.Contains("not valid JSON", OutgoingPacketCheck.Check(packet));
    }

    [Fact]
    public void Binary_and_plain_payloads_are_not_read_as_json()
    {
        Assert.Null(OutgoingPacketCheck.Check(Publish("hass.agent/media_player/abc/thumbnail", [0xFF, 0xD8, 0xFF, 0x00])));
        Assert.Null(OutgoingPacketCheck.Check(Publish("hass.agent/availability/abc", Encoding.UTF8.GetBytes("online"))));
    }

    [Fact]
    public void A_topic_with_a_wildcard_is_caught()
    {
        Assert.Contains("unusable topic", OutgoingPacketCheck.Check(Publish("hass.agent/+/state", Encoding.UTF8.GetBytes("{}"))));
    }

    [Fact]
    public void What_mqttnet_encodes_passes()
    {
        // The bytes MQTTnet itself puts on the wire for a PUBLISH like ours.
        var message = new MQTTnet.MqttApplicationMessageBuilder()
            .WithTopic("hass.agent/sensors/abc/state")
            .WithPayload("""{"cpu":1}""")
            .Build();
        var formatter = new MQTTnet.Formatter.MqttPacketFormatterAdapter(
            MQTTnet.Formatter.MqttProtocolVersion.V500, new MQTTnet.Formatter.MqttBufferWriter(4096, 65535));
        var buffer = formatter.Encode(MQTTnet.Formatter.MqttPublishPacketFactory.Create(message));

        Assert.Null(OutgoingPacketCheck.Check(buffer.Join().ToArray()));
    }
}
