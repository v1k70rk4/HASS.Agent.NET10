using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.SystemStatus;

namespace HASS.Agent.Companion.Tests;

public class LibreHardwareMonitorTests
{
    [Theory]
    [InlineData("45,0 °C", 45.0, "°C")]       // a comma as the decimal separator
    [InlineData("3.3 V", 3.3, "V")]
    [InlineData("1200 RPM", 1200.0, "RPM")]
    [InlineData("-5.5 °C", -5.5, "°C")]
    public void Value_and_unit_are_split(string text, double number, string unit)
    {
        var (value, parsedUnit) = LibreHardwareMonitorClient.SplitValue(text);

        Assert.Equal(number, value!.Value, precision: 3);
        Assert.Equal(unit, parsedUnit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("n/a")]
    public void Not_a_number(string text)
    {
        Assert.Null(LibreHardwareMonitorClient.SplitValue(text).Number);
    }

    [Theory]
    [InlineData("CPU / Core #1 = 45,0 °C | /intelcpu/0/temperature/0", "/intelcpu/0/temperature/0")]
    [InlineData("/gpu-nvidia/0/load/0", "/gpu-nvidia/0/load/0")]
    [InlineData("  /lpc/nct6798d/fan/1  ", "/lpc/nct6798d/fan/1")]
    public void Sensor_id_is_taken_from_the_editor_line(string parameter, string id)
    {
        Assert.Equal(id, LibreHardwareMonitorClient.ParseSensorId(parameter));
    }

    [Theory]
    [InlineData("", "http://localhost:8085/data.json")]
    [InlineData("http://pc:8085", "http://pc:8085/data.json")]
    [InlineData("http://pc:8085/", "http://pc:8085/data.json")]
    [InlineData("http://pc:8085/data.json", "http://pc:8085/data.json")]
    public void Data_address(string baseUrl, string expected)
    {
        Assert.Equal(expected, LibreHardwareMonitorClient.BuildDataUrl(baseUrl));
    }

    [Fact]
    public void Tree_of_the_remote_web_server_becomes_readings()
    {
        const string json = """
            {"Text":"Sensor","Children":[
              {"Text":"MY-PC","Children":[
                {"Text":"Intel Core i7","Children":[
                  {"Text":"Temperatures","Children":[
                    {"Text":"Core #1","Value":"45,0 °C","SensorId":"/intelcpu/0/temperature/0","Children":[]},
                    {"Text":"Core #2","Value":"47,5 °C","SensorId":"/intelcpu/0/temperature/1","Children":[]}]},
                  {"Text":"Load","Children":[
                    {"Text":"CPU Total","Value":"12,3 %","SensorId":"/intelcpu/0/load/0","Children":[]}]}]}]}]}
            """;

        var readings = LibreHardwareMonitorClient.Parse(json);

        Assert.Equal(3, readings.Count);
        var core = readings[0];
        Assert.Equal("/intelcpu/0/temperature/0", core.Id);
        Assert.Equal("Intel Core i7", core.Hardware);
        Assert.Equal("Core #1", core.Name);
        Assert.Equal(45.0, core.Value);
        Assert.Equal("°C", core.Unit);
        Assert.Equal("%", readings[2].Unit);
    }
}

public class SettingsTests
{
    [Fact]
    public void Custom_sensor_ids_are_clean_and_unique()
    {
        var used = new HashSet<string>();

        Assert.Equal("cpu_temp", CompanionSettings.NormalizeCustomSensorId(" CPU_Temp! ", used));
        var repeated = CompanionSettings.NormalizeCustomSensorId("cpu_temp", used);
        Assert.NotEqual("cpu_temp", repeated);
        Assert.Matches("^[0-9a-f]{32}$", repeated);
        Assert.Matches("^[0-9a-f]{32}$", CompanionSettings.NormalizeCustomSensorId("!!!", used));
    }

    [Fact]
    public void Normalized_settings_always_have_an_api_key_and_a_serial_number()
    {
        var settings = new CompanionSettings { ApiKey = "", SerialNumber = " " };

        settings.Normalize();

        Assert.Matches("^[0-9a-f]{32}$", settings.ApiKey);
        Assert.Matches("^[0-9a-f]{32}$", settings.SerialNumber);
    }

    [Theory]
    [InlineData(0, false, 1883)]
    [InlineData(70000, true, 8883)]
    [InlineData(1884, false, 1884)]
    public void Mqtt_port_falls_back_to_the_default_of_the_transport(int port, bool tls, int expected)
    {
        var settings = new CompanionSettings { MqttPort = port, MqttUseTls = tls };

        settings.Normalize();

        Assert.Equal(expected, settings.MqttPort);
    }

    [Fact]
    public void Local_api_never_binds_to_loopback_only()
    {
        var settings = new CompanionSettings { BindHost = "127.0.0.1" };

        settings.Normalize();

        Assert.Equal("0.0.0.0", settings.BindHost);
    }
}
