using System.Text;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Localization;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Media.Sendspin;
using HASS.Agent.Companion.Tray;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;

namespace HASS.Agent.Companion.Tests;

/// <summary>
/// The Sendspin player's parts that work without a Music Assistant: the address typed in, the
/// stores of its keys, the volume curve, the settings and the status texts.
/// </summary>
[Collection("Sendspin storage")]
public class SendspinTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("hass-agent-sendspin-").FullName;
    private readonly Func<bool> _isAtConsole = SendspinPlayerService.IsAtConsole;

    public SendspinTests()
    {
        SendspinStorage.DirectoryOverride = _folder;
    }

    public void Dispose()
    {
        SendspinStorage.DirectoryOverride = null;
        SendspinPlayerService.IsAtConsole = _isAtConsole;
        Strings.Language = Strings.English;
        try { Directory.Delete(_folder, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("192.168.1.10", "ws://192.168.1.10:8927/sendspin")]
    [InlineData("  music.local  ", "ws://music.local:8927/sendspin")]
    [InlineData("192.168.1.10:9000", "ws://192.168.1.10:9000/sendspin")]
    [InlineData("ws://192.168.1.10:8927/sendspin", "ws://192.168.1.10:8927/sendspin")]
    [InlineData("ws://192.168.1.10/other", "ws://192.168.1.10:8927/other")]
    [InlineData("ws://192.168.1.10:80", "ws://192.168.1.10/sendspin")]
    [InlineData("wss://ma.example.com", "wss://ma.example.com:8927/sendspin")]
    [InlineData("ws://[fd00:80::1]", "ws://[fd00:80::1]:8927/sendspin")]
    [InlineData("ws://[fd00::1]:9000", "ws://[fd00::1]:9000/sendspin")]
    [InlineData("[fd00::1]:80", "ws://[fd00::1]/sendspin")]
    public void A_typed_address_becomes_the_server_s_sendspin_address(string typed, string expected)
    {
        Assert.Equal(new Uri(expected), SendspinPlayerService.ParseServerAddress(typed));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("http://192.168.1.10:8927")]
    [InlineData("ws://")]
    public void What_is_not_an_address_is_none(string? typed)
    {
        Assert.Null(SendspinPlayerService.ParseServerAddress(typed));
    }

    [Theory]
    [InlineData(HandshakeFailureKind.LegacyServer, "ServerTooOld")]
    [InlineData(HandshakeFailureKind.PairingRequired, "NeedsPairing")]
    [InlineData(HandshakeFailureKind.PairingStateDiverged, "NeedsPairing")]
    [InlineData(HandshakeFailureKind.ConnectionClosed, "Connecting")]
    [InlineData(HandshakeFailureKind.HandshakeRejected, "Failed")]
    [InlineData(HandshakeFailureKind.ServerError, "Failed")]
    [InlineData(HandshakeFailureKind.ActivationRefused, "Failed")]
    public void A_handshake_that_fails_for_good_has_a_status_of_its_own(HandshakeFailureKind kind, string expected)
    {
        // After a reconnect the SDK reports such a failure only through the disconnected event,
        // and the status used to stay at "connecting" for good. The state is named, as the
        // enum is the app's own (internal) and a public test method cannot take it.
        Assert.Equal(Enum.Parse<SendspinState>(expected), SendspinPlayerService.StateForHandshakeFailure(kind));
    }

    [Fact]
    public void The_identity_stays_the_same_across_starts()
    {
        // The player's id is its public key: a new one each start would unpair it from every server.
        var first = SendspinIdentity.FromStore(new ProtectedSendspinIdentityStore(SendspinStorage.IdentityFile));
        var again = SendspinIdentity.FromStore(new ProtectedSendspinIdentityStore(SendspinStorage.IdentityFile));

        Assert.Equal(first.PeerId, again.PeerId);
    }

    [Fact]
    public void A_pairing_record_is_kept_protected_and_found_by_its_server()
    {
        var store = new ProtectedPairingRecordStore(SendspinStorage.PairingFile);
        var psk = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        store.Upsert(new PairingRecord(psk, PskCategory.LongTerm, "server-1"));

        var again = new ProtectedPairingRecordStore(SendspinStorage.PairingFile);
        var record = Assert.Single(again.List());
        Assert.Equal(psk, record.Psk.ToArray());
        Assert.True(again.IsPairedWith("server-1"));
        Assert.False(again.IsPairedWith("server-2"));

        // Not readable as it is: the PSK is not in the file in the clear.
        var onDisk = File.ReadAllBytes(SendspinStorage.PairingFile);
        Assert.DoesNotContain(Convert.ToBase64String(psk), Encoding.UTF8.GetString(onDisk));

        again.Remove(record.PskId);
        Assert.Empty(new ProtectedPairingRecordStore(SendspinStorage.PairingFile).List());
    }

    [Fact]
    public void A_pairing_file_that_cannot_be_read_back_counts_as_none()
    {
        // Another Windows user's, or a copy from another PC: the player pairs again, it does not fail.
        File.WriteAllBytes(SendspinStorage.PairingFile, [1, 2, 3, 4]);

        Assert.Empty(new ProtectedPairingRecordStore(SendspinStorage.PairingFile).List());
    }

    [Fact]
    public void The_pairing_token_is_the_same_until_a_new_one_is_made()
    {
        var player = new SendspinPlayerService(new CompanionSettings(), new FileLog(Path.Combine(_folder, "test.log")));

        var token = player.GetPairingToken();

        Assert.StartsWith("SP:", token, StringComparison.Ordinal);
        Assert.Equal(token, player.GetPairingToken());
        var rotated = player.RotatePairingToken();
        Assert.NotEqual(token, rotated);
        Assert.Equal(rotated, player.GetPairingToken());
    }

    [Fact]
    public async Task Only_the_app_at_the_pc_s_screen_plays()
    {
        // Another user's session, or one over Remote Desktop: quiet, so a PC is never two players.
        SendspinPlayerService.IsAtConsole = () => false;
        var player = new SendspinPlayerService(new CompanionSettings { SendspinEnabled = true }, new FileLog(Path.Combine(_folder, "test.log")));

        await player.ApplySettingsAsync();

        Assert.Equal(SendspinState.NotAtConsole, player.Status.State);
        await player.DisposeAsync();
    }

    [Fact]
    public void A_factory_reset_makes_a_new_player_of_its_own_settings_folder_only()
    {
        var config = Directory.CreateTempSubdirectory("hass-agent-reset-").FullName;
        try
        {
            var paths = new AppPaths(config, Path.Combine(config, "settings.json"), Path.Combine(config, "log.txt"), Path.Combine(config, "device-id"), []);
            var sendspin = Directory.CreateDirectory(Path.Combine(config, SendspinStorage.FolderName)).FullName;
            foreach (var name in SendspinStorage.FileNames)
            {
                File.WriteAllText(Path.Combine(sendspin, name), "x");
            }

            // The PC's own player (the overridden folder here) is not this reset's to touch.
            foreach (var name in SendspinStorage.FileNames)
            {
                File.WriteAllText(Path.Combine(SendspinStorage.Directory, name), "keep");
            }

            SettingsStore.FactoryReset(paths, new FileLog(paths.LogFile));

            Assert.Empty(Directory.GetFiles(sendspin));
            foreach (var name in SendspinStorage.FileNames)
            {
                Assert.Equal("keep", File.ReadAllText(Path.Combine(SendspinStorage.Directory, name)));
            }
        }
        finally
        {
            try { Directory.Delete(config, recursive: true); } catch { }
        }
    }

    [Fact]
    public void The_volume_follows_the_loudness_curve_and_mute_and_duck_silence()
    {
        var gain = new SendspinWasapiOutput.GainSampleProvider { Volume = 0.5f };
        Assert.Equal(Math.Pow(0.5, 1.5), gain.Target, 5);

        gain.Duck = 0.25f;
        Assert.Equal(Math.Pow(0.5, 1.5) * 0.25, gain.Target, 5);

        gain.Muted = true;
        Assert.Equal(0f, gain.Target);
    }

    [Fact]
    public void A_change_of_the_gain_is_eased_not_stepped()
    {
        var gain = new SendspinWasapiOutput.GainSampleProvider { Volume = 0f };
        var block = Enumerable.Repeat(1f, 2 * 1000).ToArray(); // 1000 stereo frames at 48 kHz

        gain.Apply(block, 0, block.Length, channels: 2, sampleRate: 48000);

        // From 1 down to 0 over about 10 ms (480 frames): the first frames are still near full.
        Assert.True(block[0] > 0.99f);
        Assert.True(block[200] is > 0.5f and < 1f);
        Assert.Equal(0f, block[^1]);
    }

    [Fact]
    public void The_queued_latency_can_still_be_read_from_naudio()
    {
        // Read by reflection from a private field of NAudio's WasapiOut. If an NAudio update
        // renames it, the player still plays but its sync is off by the device buffer: this fails first.
        Assert.True(SendspinWasapiOutput.CanReadAudioClient);
    }

    [Fact]
    public void The_player_is_named_after_the_device_unless_named_otherwise()
    {
        var settings = new CompanionSettings { DeviceName = "LIVING-PC" };
        Assert.Equal("LIVING-PC", settings.EffectiveSendspinPlayerName);

        settings.SendspinPlayerName = "  Living room  ";
        Assert.Equal("Living room", settings.EffectiveSendspinPlayerName);

        settings.SendspinPlayerName = new string('x', 100);
        settings.Normalize();
        Assert.Equal(CompanionSettings.MaxSendspinNameLength, settings.SendspinPlayerName.Length);
        Assert.False(new CompanionSettings().SendspinEnabled);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("hu")]
    public void Every_state_has_a_status_text(string language)
    {
        Strings.Language = language;
        foreach (var state in Enum.GetValues<SendspinState>())
        {
            var text = MainForm.SendspinStatusText(new SendspinStatus(state, "Music Assistant", "Song", "Artist", "Speakers", "detail"));

            Assert.False(string.IsNullOrWhiteSpace(text), state.ToString());
            Assert.DoesNotContain("{", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Mp.", text, StringComparison.Ordinal);
        }
    }
}

/// <summary>The tests that point the Sendspin storage to their own folder run one at a time.</summary>
[CollectionDefinition("Sendspin storage", DisableParallelization = true)]
public class SendspinStorageCollection
{
}
