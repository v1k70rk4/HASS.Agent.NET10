using HASS.Agent.Companion.Mqtt;

namespace HASS.Agent.Companion.Tests;

public class MqttReconnectLogTests
{
    [Fact]
    public void A_session_lost_after_it_lived_is_no_warning()
    {
        // The PC slept for minutes: the session it had is gone, the next one comes in 5 s.
        var shortLived = MqttCompanionService.ShortLivedAfter(TimeSpan.FromSeconds(199), shortLivedBefore: 0);

        Assert.Equal(0, shortLived);
        Assert.False(MqttCompanionService.LossIsWorthAWarning(shortLived));
        Assert.Equal(5, MqttCompanionService.ReconnectSeconds(shortLived));
    }

    [Fact]
    public void A_session_that_dies_right_after_connecting_is_a_warning()
    {
        var shortLived = MqttCompanionService.ShortLivedAfter(TimeSpan.FromSeconds(3), shortLivedBefore: 0);

        Assert.Equal(1, shortLived);
        Assert.True(MqttCompanionService.LossIsWorthAWarning(shortLived));
        Assert.Equal(10, MqttCompanionService.ReconnectSeconds(shortLived));
    }

    [Fact]
    public void A_session_that_lived_again_ends_the_back_off()
    {
        Assert.Equal(0, MqttCompanionService.ShortLivedAfter(TimeSpan.FromMinutes(5), shortLivedBefore: 4));
        Assert.Equal(60, MqttCompanionService.ReconnectSeconds(4));
    }
}
