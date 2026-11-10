using HASS.Agent.Companion.Runtime;

namespace HASS.Agent.Companion.Mqtt;

/// <summary>
/// Which incoming MQTT messages a role acts on. A client is handed every message its broker
/// sends, on a topic it subscribed to or not, so the subscriptions decide nothing: a broker that
/// is hostile, or one a user of the PC pointed the settings at, sends what it likes. The service
/// runs as SYSTEM and must never act on what is meant for the tray app.
/// </summary>
internal static class MqttTopicGate
{
    /// <summary>
    /// The service acts on its own command topic and on the update's Install button, nothing
    /// else; the tray app on everything but the service's command topic.
    /// </summary>
    public static bool IsForRole(CompanionRuntimeRole role, string topic, string serviceCommandTopic, string updateInstallTopic) =>
        role == CompanionRuntimeRole.Service
            ? topic == serviceCommandTopic || topic == updateInstallTopic
            : topic != serviceCommandTopic;

    /// <summary>
    /// A retained message is handed over again at every connect: a command sent once would run
    /// at each start for as long as the broker keeps it. Only the integration's own note about
    /// itself is retained on purpose.
    /// </summary>
    public static bool IsReplay(string topic, bool retained, string integrationInfoTopic) =>
        retained && topic != integrationInfoTopic;
}
