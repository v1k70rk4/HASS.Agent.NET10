using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Logging;

namespace HASS.Agent.Companion.Tests;

/// <summary>
/// Several users' apps and the service share one settings file. A save writes what was changed
/// since the settings were read, over the file as it is; and a save that cannot replace the
/// file never leaves it cut short.
/// </summary>
public sealed class SettingsSaveTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hass-agent-tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly FileLog _log;

    public SettingsSaveTests()
    {
        Directory.CreateDirectory(_folder);
        _paths = new AppPaths(_folder, Path.Combine(_folder, "settings.json"), Path.Combine(_folder, "app.log"), Path.Combine(_folder, "device-id"), []);
        _log = new FileLog(Path.Combine(_folder, "test.log"));
    }

    public void Dispose()
    {
        _log.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private CompanionSettings InFile() => SettingsStore.Import(_paths.SettingsFile);

    [Fact]
    public void Two_apps_keep_what_the_other_changed()
    {
        SettingsStore.Save(_paths, new CompanionSettings { DeviceName = "pc", MqttHost = "old.local" });
        var first = SettingsStore.LoadOrCreate(_paths, _log);
        var second = SettingsStore.LoadOrCreate(_paths, _log);

        first.MqttHost = "new.local";
        SettingsStore.Save(_paths, first);
        second.UpdateProxyCustom = true;
        second.UpdateProxyAddress = "http://127.0.0.1:7890";
        SettingsStore.Save(_paths, second);

        var file = InFile();
        Assert.Equal("new.local", file.MqttHost);
        Assert.Equal("http://127.0.0.1:7890", file.UpdateProxyAddress);
        // The app goes on with the settings it holds, until it reads the file again.
        Assert.Equal("old.local", second.MqttHost);

        // And the first one's next save of something else keeps the second one's proxy.
        first.DeviceName = "desk";
        SettingsStore.Save(_paths, first);
        file = InFile();
        Assert.Equal("desk", file.DeviceName);
        Assert.True(file.UpdateProxyCustom);
        Assert.Equal("new.local", file.MqttHost);
    }

    [Fact]
    public void A_setting_changed_here_is_saved_over_the_other_apps_value()
    {
        SettingsStore.Save(_paths, new CompanionSettings { DeviceName = "pc", MqttHost = "old.local" });
        var first = SettingsStore.LoadOrCreate(_paths, _log);
        var second = SettingsStore.LoadOrCreate(_paths, _log);

        first.MqttHost = "first.local";
        SettingsStore.Save(_paths, first);
        second.MqttHost = "second.local";
        SettingsStore.Save(_paths, second);

        Assert.Equal("second.local", InFile().MqttHost);
    }

    [Fact]
    public void Settings_that_were_never_read_from_the_file_are_written_whole()
    {
        SettingsStore.Save(_paths, new CompanionSettings { DeviceName = "pc", MqttHost = "old.local" });

        // An import: every setting is the imported one, the ones left at their default too.
        SettingsStore.Save(_paths, new CompanionSettings { DeviceName = "imported" });

        var file = InFile();
        Assert.Equal("imported", file.DeviceName);
        Assert.NotEqual("old.local", file.MqttHost);
    }

    [Fact]
    public void A_copy_saves_what_was_changed_on_it_and_keeps_the_rest_of_the_file()
    {
        SettingsStore.Save(_paths, new CompanionSettings { DeviceName = "pc", MqttHost = "old.local" });
        var inUse = SettingsStore.LoadOrCreate(_paths, _log);
        var other = SettingsStore.LoadOrCreate(_paths, _log);
        other.MqttHost = "other.local";
        SettingsStore.Save(_paths, other);

        var copy = SettingsStore.Clone(inUse);
        copy.DeviceName = "copy";
        SettingsStore.Save(_paths, copy);

        var file = InFile();
        Assert.Equal("copy", file.DeviceName);
        Assert.Equal("other.local", file.MqttHost);
    }

    [Fact]
    public void A_reload_starts_from_what_the_file_has()
    {
        SettingsStore.Save(_paths, new CompanionSettings { DeviceName = "pc", MqttHost = "old.local" });
        var inUse = SettingsStore.LoadOrCreate(_paths, _log);
        var other = SettingsStore.LoadOrCreate(_paths, _log);
        other.MqttHost = "other.local";
        SettingsStore.Save(_paths, other);

        Assert.Equal(SettingsReload.Changed, SettingsStore.ReloadInto(_paths, inUse, _log));
        other.MqttHost = "again.local";
        SettingsStore.Save(_paths, other);
        inUse.DeviceName = "desk";
        SettingsStore.Save(_paths, inUse);

        var file = InFile();
        Assert.Equal("desk", file.DeviceName);
        Assert.Equal("again.local", file.MqttHost);
    }

    [Fact]
    public void The_merge_takes_only_what_reads_as_settings()
    {
        const string Seen = """{"deviceName":"pc","port":5115,"mqttHost":"old.local"}""";
        const string Ours = """{"deviceName":"desk","port":5115,"mqttHost":"old.local","newSetting":true}""";

        // Changed elsewhere and not here: the file's. Changed here: ours. New here: ours.
        var merged = SettingsStore.MergeUnchangedFromFile(Ours, Seen, """{"deviceName":"other","port":5115,"mqttHost":"new.local"}""");
        Assert.Contains("\"deviceName\": \"desk\"", merged, StringComparison.Ordinal);
        Assert.Contains("\"mqttHost\": \"new.local\"", merged, StringComparison.Ordinal);
        Assert.Contains("\"newSetting\": true", merged, StringComparison.Ordinal);

        // No file, a file that is no JSON object, or a value that is no setting: ours as they are.
        Assert.Equal(Ours, SettingsStore.MergeUnchangedFromFile(Ours, Seen, null));
        Assert.Equal(Ours, SettingsStore.MergeUnchangedFromFile(Ours, Seen, "not json"));
        Assert.Equal(Ours, SettingsStore.MergeUnchangedFromFile(Ours, Seen, "[1]"));
        Assert.Equal(Ours, SettingsStore.MergeUnchangedFromFile(Ours, Seen, """{"deviceName":"other","port":"many","mqttHost":"old.local"}"""));

        // The save that sets "updates only at this PC" keeps its own value of it.
        const string Ticked = """{"remoteUpdateInstallBlocked":true}""";
        Assert.Equal(Ticked, SettingsStore.MergeUnchangedFromFile(Ticked, Ticked, """{"remoteUpdateInstallBlocked":false}""", keepOurRemoteInstallSwitch: true));
        Assert.Contains("false", SettingsStore.MergeUnchangedFromFile(Ticked, Ticked, """{"remoteUpdateInstallBlocked":false}"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_save_waits_for_another_apps_save_to_end()
    {
        SettingsStore.Save(_paths, new CompanionSettings { DeviceName = "pc" });
        var held = new ManualResetEventSlim();
        var letGo = new ManualResetEventSlim();
        // Another app in its save: the turn is a mutex, held by the thread that took it.
        var other = new Thread(() =>
        {
            using var turn = SettingsStore.TakeSaveTurn(_paths);
            Assert.NotNull(turn);
            held.Set();
            letGo.Wait(TimeSpan.FromSeconds(10));
        });
        other.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        var save = Task.Run(() => SettingsStore.Save(_paths, new CompanionSettings { DeviceName = "desk" }), TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(save.IsCompleted);
        Assert.Equal("pc", InFile().DeviceName);

        letGo.Set();
        await save.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        other.Join();
        Assert.Equal("desk", InFile().DeviceName);
    }

    [Fact]
    public async Task A_start_waits_for_another_apps_save_to_end()
    {
        SettingsStore.Save(_paths, new CompanionSettings { DeviceName = "pc" });
        var held = new ManualResetEventSlim();
        var letGo = new ManualResetEventSlim();
        var other = new Thread(() =>
        {
            using var turn = SettingsStore.TakeSaveTurn(_paths);
            held.Set();
            letGo.Wait(TimeSpan.FromSeconds(10));
        });
        other.Start();
        Assert.True(held.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        var load = Task.Run(() => SettingsStore.LoadOrCreate(_paths, _log), TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(load.IsCompleted);

        letGo.Set();
        Assert.Equal("pc", (await load.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).DeviceName);
        other.Join();
    }

    [Fact]
    public void A_save_fails_rather_than_write_without_the_file_it_cannot_read()
    {
        SettingsStore.Save(_paths, new CompanionSettings { DeviceName = "pc", MqttHost = "old.local" });
        var inUse = SettingsStore.LoadOrCreate(_paths, _log);
        var before = File.ReadAllText(_paths.SettingsFile);
        inUse.DeviceName = "desk";

        using (new FileStream(_paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => SettingsStore.Save(_paths, inUse));
        }

        Assert.Equal(before, File.ReadAllText(_paths.SettingsFile));
        // And once it can be read again, the save goes through.
        SettingsStore.Save(_paths, inUse);
        Assert.Equal("desk", InFile().DeviceName);
    }

    [Fact]
    public void A_file_that_cannot_be_replaced_is_written_in_place_with_the_old_one_kept()
    {
        SettingsStore.Save(_paths, new CompanionSettings { DeviceName = "pc", MqttHost = "old.local" });
        var before = File.ReadAllText(_paths.SettingsFile);
        File.Delete(_paths.SettingsFile + ".bak");

        // Open elsewhere without leave to delete it: a replace is refused, a write is not.
        using (new FileStream(_paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            SettingsStore.Save(_paths, new CompanionSettings { DeviceName = "pc", MqttHost = "new.local" });
        }

        Assert.Equal("new.local", InFile().MqttHost);
        Assert.Equal(before, File.ReadAllText(_paths.SettingsFile + ".bak"));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public void A_save_that_fails_leaves_the_file_as_it_was()
    {
        SettingsStore.Save(_paths, new CompanionSettings { DeviceName = "pc", MqttHost = "old.local" });
        var before = File.ReadAllText(_paths.SettingsFile);

        // Open elsewhere for reading alone: neither a replace nor a write gets through.
        using (new FileStream(_paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() =>
                SettingsStore.Save(_paths, new CompanionSettings { DeviceName = "pc", MqttHost = "new.local" }));
        }

        Assert.Equal(before, File.ReadAllText(_paths.SettingsFile));
        Assert.Equal("old.local", InFile().MqttHost);
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }
}
