using System.Text.Json;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Http;

namespace HASS.Agent.Companion.Tests;

public class NotificationPayloadTests
{
    // The options the MQTT and HA API transports read notifications with.
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static NotificationPayload Parse(string json) =>
        JsonSerializer.Deserialize<NotificationPayload>(json, Options)!;

    [Fact]
    public void Full_notification_from_the_integration_is_read()
    {
        var notification = Parse("""
            {"message":"Somebody is at the door","title":"Doorbell",
             "data":{"image":"http://ha.local:8123/api/camera_proxy/camera.door?authSig=x",
                     "image_path":"/api/camera_proxy/camera.door?authSig=x",
                     "image_alt":"https://ha.example.com/api/camera_proxy/camera.door?authSig=x",
                     "style":"window","duration":30,
                     "inputs":[{"id":"answer","title":"Say something"}],
                     "actions":[{"action":"open","title":"Open"}]}}
            """);

        Assert.Equal("Somebody is at the door", notification.Message);
        Assert.Equal("Doorbell", notification.Title);
        Assert.Equal("/api/camera_proxy/camera.door?authSig=x", notification.Data?.ImagePath);
        Assert.Equal("https://ha.example.com/api/camera_proxy/camera.door?authSig=x", notification.Data?.ImageAlt);
        Assert.Equal(30_000, notification.TimeoutMilliseconds);
        Assert.Equal("answer", Assert.Single(notification.Inputs).Id);
        Assert.Equal("open", Assert.Single(notification.Actions).Action);
    }

    [Fact]
    public void At_most_five_buttons_and_none_without_an_action()
    {
        var actions = string.Join(",", Enumerable.Range(1, 7).Select(n => $"{{\"action\":\"a{n}\",\"title\":\"A{n}\"}}"));
        var notification = Parse("""{"message":"m","data":{"actions":[{"action":"","title":"Empty"},""" + actions + "]}}");

        Assert.Equal(["a1", "a2", "a3", "a4", "a5"], notification.Actions.Select(action => action.Action));
        Assert.True(notification.HasActions);
    }

    [Fact]
    public void Missing_or_repeated_input_ids_get_generated_ones_that_do_not_clash()
    {
        var notification = Parse("""
            {"message":"m","data":{"inputs":[
              {"title":"No id"},
              {"id":"input1","title":"Taken by the user"},
              {"id":"answer","title":"First"},
              {"id":"answer","title":"Repeated"}]}}
            """);

        var ids = notification.Inputs.Select(input => input.Id).ToList();
        Assert.Equal(4, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal("input1", ids[0]);
        Assert.Equal("answer", ids[2]);
    }

    [Fact]
    public void At_most_five_text_fields()
    {
        var inputs = string.Join(",", Enumerable.Range(1, 8).Select(n => $"{{\"id\":\"f{n}\"}}"));
        var notification = Parse("""{"message":"m","data":{"inputs":[""" + inputs + "]}}");

        Assert.Equal(NotificationPayload.MaxInputs, notification.Inputs.Count);
    }

    [Theory]
    [InlineData(0, 10_000)]   // not given: the default
    [InlineData(-5, 10_000)]
    [InlineData(1, 1_000)]
    [InlineData(300, 60_000)] // capped at a minute
    public void Duration_is_kept_within_a_second_and_a_minute(int seconds, int expected)
    {
        var notification = Parse("""{"message":"m","data":{"duration":""" + seconds + "}}");

        Assert.Equal(expected, notification.TimeoutMilliseconds);
    }

    [Fact]
    public void Plain_message_has_no_buttons_or_fields()
    {
        var notification = Parse("""{"message":"The washing machine is done."}""");

        Assert.False(notification.HasActions);
        Assert.Empty(notification.Inputs);
        Assert.Equal(10_000, notification.TimeoutMilliseconds);
    }
}

public class NotificationStyleTests
{
    [Theory]
    [InlineData("toast", "toast")]
    [InlineData("window", "window")]
    [InlineData(" Window ", "window")]
    [InlineData("TOAST", "toast")]
    [InlineData("balloon", "toast")]
    [InlineData(null, "toast")]
    public void Style_is_one_of_the_two(string? value, string expected)
    {
        Assert.Equal(expected, NotificationStyles.Normalize(value));
    }

    [Fact]
    public void Unknown_style_of_a_notification_falls_back_to_the_setting()
    {
        Assert.Equal("window", NotificationStyles.Normalize("whatever", fallback: NotificationStyles.Window));
    }
}

public class NotificationPictureAddressTests
{
    [Theory]
    [InlineData("192.168.1.10", true)]      // Home Assistant, a camera on the LAN
    [InlineData("10.0.0.5", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("fe80::1", true)]           // a .local name can resolve to one
    [InlineData("127.0.0.1", false)]        // this PC
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("169.254.169.254", false)]  // link-local
    public void Pictures_are_not_fetched_from_this_pc(string address, bool allowed)
    {
        Assert.Equal(allowed, HASS.Agent.Companion.Tray.NotificationImageCache.IsAllowedTarget(System.Net.IPAddress.Parse(address)));
    }

    [Fact]
    public void This_pcs_own_lan_address_counts_as_this_pc()
    {
        var own = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .FirstOrDefault(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(address) && !address.ToString().StartsWith("169.254."));
        if (own is null)
        {
            return;
        }

        Assert.False(HASS.Agent.Companion.Tray.NotificationImageCache.IsAllowedTarget(own));
        Assert.True(HASS.Agent.Companion.Tray.NotificationImageCache.IsAllowedTarget(own, allowThisPc: true));
    }

    [Fact]
    public void Home_assistant_on_this_pc_is_allowed()
    {
        Assert.True(HASS.Agent.Companion.Tray.NotificationImageCache.IsAllowedTarget(System.Net.IPAddress.Loopback, allowThisPc: true));
    }
}
