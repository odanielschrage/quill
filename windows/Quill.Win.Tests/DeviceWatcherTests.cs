using NAudio.CoreAudioApi;
using Quill.Audio;
using Xunit;

namespace Quill.Tests;

/// Windows raises the default-device notification once per role, and the mic and
/// loopback tracks follow opposite sides of the audio graph. Getting either
/// filter wrong reopens the capture when it shouldn't — three times for one
/// headphone plug, or on a change to the other side entirely. `quill devicetest`
/// covers the reopen itself; these cover when it fires.
public sealed class DeviceWatcherTests
{
    [Fact]
    public void OneChangeFiresOnceDespiteThreeRoleNotifications()
    {
        var fired = 0;
        var watcher = new WasapiTrackRecorder.DeviceWatcher(DataFlow.Render, () => fired++);

        // What Windows actually delivers for a single default change.
        watcher.OnDefaultDeviceChanged(DataFlow.Render, Role.Console, "id");
        watcher.OnDefaultDeviceChanged(DataFlow.Render, Role.Multimedia, "id");
        watcher.OnDefaultDeviceChanged(DataFlow.Render, Role.Communications, "id");

        Assert.Equal(1, fired);
    }

    /// The mic track follows the capture default; a new pair of speakers is not
    /// its business.
    [Fact]
    public void TheOtherSideOfTheGraphIsIgnored()
    {
        var fired = 0;
        var watcher = new WasapiTrackRecorder.DeviceWatcher(DataFlow.Capture, () => fired++);

        watcher.OnDefaultDeviceChanged(DataFlow.Render, Role.Console, "id");

        Assert.Equal(0, fired);
    }

    [Fact]
    public void MatchingFlowAndRoleFires()
    {
        var fired = 0;
        var watcher = new WasapiTrackRecorder.DeviceWatcher(DataFlow.Capture, () => fired++);

        watcher.OnDefaultDeviceChanged(DataFlow.Capture, Role.Console, "id");

        Assert.Equal(1, fired);
    }

    /// Devices appear and disappear constantly on a laptop — docking, Bluetooth,
    /// virtual endpoints. Only a change of the *default* is worth reopening for;
    /// the capture dying is handled separately through RecordingStopped.
    [Fact]
    public void DeviceInventoryChurnDoesNotReopen()
    {
        var fired = 0;
        var watcher = new WasapiTrackRecorder.DeviceWatcher(DataFlow.Render, () => fired++);

        watcher.OnDeviceAdded("id");
        watcher.OnDeviceRemoved("id");
        watcher.OnDeviceStateChanged("id", DeviceState.Unplugged);
        watcher.OnPropertyValueChanged("id", default);

        Assert.Equal(0, fired);
    }
}
