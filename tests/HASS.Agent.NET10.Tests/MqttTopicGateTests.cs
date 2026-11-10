using HASS.Agent.Companion.Mqtt;
using HASS.Agent.Companion.Runtime;

namespace HASS.Agent.Companion.Tests;

public class MqttTopicGateTests
{
    private const string ServiceCommands = "hass.agent/system/abc/cmd";
    private const string UpdateInstall = "hass.agent/update/abc/install";
    private const string IntegrationInfo = "hass.agent/integration/abc";

    [Theory]
    [InlineData(ServiceCommands, true)]
    [InlineData(UpdateInstall, true)]
    [InlineData("hass.agent/buttons/abc/cmd", false)]          // the tray app's commands
    [InlineData("hass.agent/notifications/abc", false)]
    [InlineData("hass.agent/media_player/abc/cmd", false)]
    [InlineData(IntegrationInfo, false)]
    [InlineData("hass.agent/system/other/cmd", false)]         // another PC's service
    public void The_service_acts_on_its_own_two_topics_whatever_the_broker_sends(string topic, bool acted)
    {
        Assert.Equal(acted, MqttTopicGate.IsForRole(CompanionRuntimeRole.Service, topic, ServiceCommands, UpdateInstall));
    }

    [Theory]
    [InlineData("hass.agent/buttons/abc/cmd", true)]
    [InlineData("hass.agent/notifications/abc", true)]
    [InlineData(UpdateInstall, true)]
    [InlineData(IntegrationInfo, true)]
    [InlineData(ServiceCommands, false)]                       // run as the service's, they would skip the tray app's switches
    public void The_tray_app_does_not_act_on_the_services_commands(string topic, bool acted)
    {
        Assert.Equal(acted, MqttTopicGate.IsForRole(CompanionRuntimeRole.App, topic, ServiceCommands, UpdateInstall));
    }

    [Theory]
    [InlineData(ServiceCommands, true, true)]                  // a shutdown kept by the broker, at every start
    [InlineData("hass.agent/buttons/abc/cmd", true, true)]
    [InlineData(UpdateInstall, true, true)]
    [InlineData(ServiceCommands, false, false)]
    [InlineData(IntegrationInfo, true, false)]                 // retained on purpose
    public void A_message_the_broker_kept_is_not_acted_on_again(string topic, bool retained, bool ignored)
    {
        Assert.Equal(ignored, MqttTopicGate.IsReplay(topic, retained, IntegrationInfo));
    }
}
