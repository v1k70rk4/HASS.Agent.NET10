using HASS.Agent.Companion.Logging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace HASS.Agent.Companion.Media;

internal sealed class AudioEndpointService : IDisposable
{
    private readonly FileLog _log;
    private readonly MMDeviceEnumerator _enumerator;
    private readonly NotificationClient _notificationClient;
    private MMDevice? _renderDevice;
    private readonly object _gate = new();
    private bool _disposed;

    public AudioEndpointService(FileLog log)
    {
        _log = log;
        _enumerator = new MMDeviceEnumerator();
        _notificationClient = new NotificationClient(this);

        try
        {
            _enumerator.RegisterEndpointNotificationCallback(_notificationClient);
        }
        catch (Exception ex)
        {
            _log.Warning($"Unable to register audio notification callback: {ex.Message}");
        }

        SubscribeRenderDevice();
    }

    /// <summary>Raised on volume/mute change or default output device change — drives push updates.</summary>
    public event EventHandler? StateChanged;

    // All audio COM access is serialized through _gate. Concurrent access during an
    // audio device change (e.g. monitor/HDMI output disappearing when the screen
    // powers off) deadlocks inside the WASAPI COM layer (Marshal.ReleaseComObject),
    // which froze the push update, the media loop and the device-change callback.

    public int GetVolume()
    {
        lock (_gate)
        {
            try
            {
                if (_disposed) return 0;
                using var device = GetDefaultDevice(DataFlow.Render);
                return Convert.ToInt32(Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100, 0));
            }
            catch (Exception ex)
            {
                _log.Warning($"Unable to read default audio volume: {ex.Message}");
                return 0;
            }
        }
    }

    public bool GetMuted()
    {
        lock (_gate)
        {
            try
            {
                if (_disposed) return false;
                using var device = GetDefaultDevice(DataFlow.Render);
                return device.AudioEndpointVolume.Mute;
            }
            catch (Exception ex)
            {
                _log.Warning($"Unable to read default audio mute state: {ex.Message}");
                return false;
            }
        }
    }

    public string GetOutputDeviceName()
    {
        lock (_gate)
        {
            try
            {
                if (_disposed) return string.Empty;
                using var device = GetDefaultDevice(DataFlow.Render);
                return device.FriendlyName;
            }
            catch (Exception ex)
            {
                _log.Warning($"Unable to read default audio output device: {ex.Message}");
                return string.Empty;
            }
        }
    }

    public bool? GetMicrophoneMuted()
    {
        lock (_gate)
        {
            try
            {
                if (_disposed) return null;
                using var device = GetDefaultDevice(DataFlow.Capture);
                return device.AudioEndpointVolume.Mute;
            }
            catch (Exception ex)
            {
                _log.Warning($"Unable to read default microphone mute state: {ex.Message}");
                return null;
            }
        }
    }

    public void SetVolume(int volume)
    {
        lock (_gate)
        {
            try
            {
                if (_disposed) return;
                using var device = GetDefaultDevice(DataFlow.Render);
                device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0, 100) / 100f;
            }
            catch (Exception ex)
            {
                _log.Warning($"Unable to set default audio volume: {ex.Message}");
            }
        }
    }

    public void SetMuted(bool muted)
    {
        lock (_gate)
        {
            try
            {
                if (_disposed) return;
                using var device = GetDefaultDevice(DataFlow.Render);
                device.AudioEndpointVolume.Mute = muted;
            }
            catch (Exception ex)
            {
                _log.Warning($"Unable to set default audio mute state: {ex.Message}");
            }
        }
    }

    // Subscribes volume/mute notifications on the current default render device.
    private void SubscribeRenderDevice()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            UnsubscribeRenderDevice();

            try
            {
                _renderDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _renderDevice.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
            }
            catch (Exception ex)
            {
                _log.Warning($"Unable to subscribe to audio volume notifications: {ex.Message}");
                _renderDevice = null;
            }
        }
    }

    private void UnsubscribeRenderDevice()
    {
        if (_renderDevice is null)
        {
            return;
        }

        try
        {
            _renderDevice.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
        }
        catch
        {
            // The device may already be gone.
        }

        try { _renderDevice.Dispose(); } catch { }
        _renderDevice = null;
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // Called by the notification client when the default output device changes.
    internal void OnDefaultRenderDeviceChanged()
    {
        // CRITICAL: this runs inside an IMMNotificationClient callback, which holds a
        // WASAPI lock. Doing COM work here (re-subscribe disposes MMDevice →
        // ReleaseComObject) deadlocks against that lock. Defer to a background thread
        // so the callback returns immediately and frees the WASAPI lock.
        Task.Run(() =>
        {
            SubscribeRenderDevice();
            StateChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    // Caller must hold _gate. Uses the shared enumerator instead of creating a new
    // COM enumerator on every call, reducing COM churn during device changes.
    private MMDevice GetDefaultDevice(DataFlow dataFlow)
    {
        return _enumerator.GetDefaultAudioEndpoint(dataFlow, Role.Multimedia);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            UnsubscribeRenderDevice();

            try { _enumerator.UnregisterEndpointNotificationCallback(_notificationClient); } catch { }
            try { _enumerator.Dispose(); } catch { }
        }
    }

    // Watches for default-device changes so we can re-subscribe volume notifications.
    private sealed class NotificationClient(AudioEndpointService owner) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia)
            {
                owner.OnDefaultRenderDeviceChanged();
            }
            else
            {
                // Capture device (microphone) change — still worth a refresh.
                owner.StateChanged?.Invoke(owner, EventArgs.Empty);
            }
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }

        public void OnDeviceAdded(string pwstrDeviceId) { }

        public void OnDeviceRemoved(string deviceId) { }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
