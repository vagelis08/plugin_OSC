// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.ComponentModel.Composition;
using System.Net;
using System.Numerics;
using Amethyst.Plugins.Contract;
using BuildSoft.OscCore;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using VRC.OSCQuery;
using static VRC.OSCQuery.Extensions;
using OSCExtensions = VRC.OSCQuery.Extensions;

namespace plugin_OSC;

public struct OscConfig
{
    private string _mTargetIpAddress;
    public int OscSendPort { get; set; }
    public int OscReceivePort { get; set; }
    public int TcpPort { get; set; }

    public string TargetIpAddress
    {
        get => _mTargetIpAddress;
        set
        {
            _mTargetIpAddress = value;
            if (value.ToLowerInvariant() == "localhost") _mTargetIpAddress = "127.0.0.1";
        }
    }

    public OscConfig(string targetIpAddress = "127.0.0.1", int udpSendPort = -1,
        int udpListenPort = -1, int tcpPort = -1)
    {
        TargetIpAddress = targetIpAddress;
        OscSendPort = udpSendPort == -1 ? 9000 : udpSendPort;
        OscReceivePort = udpListenPort == -1 ? 9001 : udpListenPort;
        TcpPort = tcpPort == -1 ? GetAvailableTcpPort() : tcpPort;
    }
}

public class OscPosition(Vector3 position = default, Quaternion orientation = default)
{
    public Vector3 Position { get; set; } = position;
    public Quaternion Orientation { get; set; } = orientation;
}

[Export(typeof(IServiceEndpoint))]
[ExportMetadata("Name", "VRChat OSC")]
[ExportMetadata("Guid", "K2VRTEAM-AME2-APII-SNDP-SENDPTVRCOSC")]
[ExportMetadata("Publisher", "K2VR Team")]
[ExportMetadata("Version", "1.0.0.1")]
[ExportMetadata("Website", "https://github.com/KinectToVR/plugin_OSC")]
[ExportMetadata("DependencyLink", "https://docs.k2vr.tech/{0}/osc/")]
[ExportMetadata("CoreSetupData", typeof(SetupData))]
public class Osc : IServiceEndpoint
{
    private const string AmethystOscServiceName = "AMETHYST-OSC";

    private const string TRACKERS_ROOT = "/tracking/trackers";
    private const string TRACKERS_POSITION = "position";
    private const string TRACKERS_ROTATION = "rotation";

    private static OSCQueryService _oscQueryService;
    private static OSCQueryService _oscQuery;
    private static OscServer _receiver;

    private static OscConfig _sOscConfig = new("127.0.0.1");
    private static readonly Vector3 HeadOffset = new(0, 0, 0.2f);
    private readonly SortedDictionary<string, OscClientPlus> _receivers = new();

    private Exception _lastInitException;
    [Import(typeof(IAmethystHost))] private IAmethystHost Host { get; set; }

    private bool IsIpValid { get; set; } = true;
    private bool IsOscPortValid { get; set; } = true;
    private bool IsTcpPortValid { get; set; } = true;
    private bool PluginLoaded { get; set; }

    private OscLogger Logger => AmethystLogger ??= new OscLogger(Host);
    private OscLogger AmethystLogger { get; set; }

    private static SortedDictionary<int, OscPosition> Positions { get; } = new();

    public bool IsSettingsDaemonSupported => true;
    public object SettingsInterfaceRoot => MInterfaceRoot;
    public int ServiceStatus { get; private set; } = (int)OscStatusEnum.Unknown;

    public string ServiceStatusString => Host?.RequestLocalizedString($"/Statuses/{(OscStatusEnum)ServiceStatus}")
        .Replace("{0}", _lastInitException?.Message ?? "[Not available]") ?? "Status doko?";

    public Uri ErrorDocsUri => new($"https://docs.k2vr.tech/{Host?.DocsLanguageCode ?? "en"}/osc/");

    public SortedSet<TrackerType> AdditionalSupportedTrackerTypes => new()
    {
        TrackerType.TrackerLeftFoot, // Already OK
        TrackerType.TrackerRightFoot, // Already OK
        TrackerType.TrackerLeftElbow,
        TrackerType.TrackerRightElbow,
        TrackerType.TrackerLeftKnee,
        TrackerType.TrackerRightKnee,
        TrackerType.TrackerWaist, // Already OK
        TrackerType.TrackerChest
    };

    public bool AutoStartAmethyst
    {
        get => false;
        set { }
    }

    public bool AutoCloseAmethyst
    {
        get => false;
        set { }
    }

    public InputActions ControllerInputActions
    {
        // ReSharper disable once AssignNullToNotNullAttribute
        get => null;
        set { }
    }

    public bool CanAutoStartAmethyst => false;
    public bool IsRestartOnChangesNeeded => false;

    public bool IsAmethystVisible => true;
    public string TrackingSystemName => "OSC";

    public (Vector3 Position, Quaternion Orientation)? HeadsetPose => Positions.TryGetValue(0, out var headPose)
        ? new ValueTuple<Vector3, Quaternion>(headPose.Position, headPose.Orientation)
        : null; // Otherwise just disable the whole thing

    public void DisplayToast((string Title, string Text) message)
    {
        // Hope VRChat lets us do this someday...
        Host?.Log("DisplayToast!");
    }

    public void Heartbeat()
    {
        try
        {
            // If the service hasn't started yet
            if (!_receivers.Any()) return;

            var headJoint = Host?.GetHookJointPose();
            if (!headJoint.HasValue) return;

            // Vector3 eulerAngles = NumericExtensions.ToEulerAngles(headJoint.Value.Orientation);
            foreach (var receiver in _receivers.Values.ToList())
                SendTrackerDataToReceiver("head", new OscPosition(headJoint.Value.Position + HeadOffset), receiver);
        }
        catch (Exception ex)
        {
            Host?.Log($"Unhandled Exception: {ex.GetType().Name} " +
                      $"in {ex.Source}: {ex.Message}\n{ex.StackTrace}", LogSeverity.Fatal);
        }
    }

    public int Initialize()
    {
        Host?.Log("Called Initialize!");
        Host?.Log("Init!");

        // Assume [nothing]
        ServiceStatus = (int)OscStatusEnum.Unknown;

        try
        {
            if (_receivers.Any())
            {
                Host?.Log("OSC Server was already running! Shutting it down...", LogSeverity.Warning);
                _receivers.Clear();
            }

            if (_receiver != null)
            {
                Host?.Log("OSC Receiver was already running! Shutting it down...", LogSeverity.Warning);
                _receiver.Dispose();
                _receiver = null;
            }

            if (_oscQueryService != null)
            {
                Host?.Log("OSC Query Service was already running! Shutting it down...", LogSeverity.Warning);
                _oscQueryService.Dispose();
                _oscQueryService = null;
            }

            if (_oscQuery != null)
            {
                Host?.Log("OSC Query Service was already running! Shutting it down...", LogSeverity.Warning);
                _oscQuery.Dispose();
                _oscQuery = null;
            }

            // Create OSC Server on available port
            var port = GetAvailableTcpPort();
            var udpPort = GetAvailableUdpPort();

            // Starts the OSC server
            _receiver = OscServer.GetOrCreate(udpPort);
            _oscQuery = new OSCQueryServiceBuilder()
                .WithServiceName(Guid.NewGuid().ToString())
                .WithLogger(Logger)
                .WithTcpPort(port)
                .WithUdpPort(udpPort)
                .WithDiscovery(new MeaModDiscovery(Logger))
                .StartHttpServer()
                .AdvertiseOSC()
                .AdvertiseOSCQuery()
                .Build();

            // Starts the OSC client
            _oscQueryService = new OSCQueryServiceBuilder()
                .WithServiceName(AmethystOscServiceName)
                .WithLogger(Logger)
                .WithTcpPort(_sOscConfig.TcpPort)
                .WithUdpPort(_sOscConfig.OscSendPort)
                .WithDiscovery(new MeaModDiscovery(Logger))
                .StartHttpServer()
                .AdvertiseOSCQuery()
                .Build();

            // Listen for other services
            _oscQueryService.OnOscQueryServiceAdded += OnOscQueryServiceFound;
            var services = _oscQueryService.GetOSCQueryServices();

            // Trigger event for any existing OSCQueryServices
            foreach (var profile in services.Where(x => x.name is not AmethystOscServiceName))
                OnOscQueryServiceFound(profile);

            // Query network for services
            _oscQuery.RefreshServices();
            _oscQueryService.RefreshServices();

            // Register the HMD pose reader
            SetupTracker(0);
            SaveSettings();
        }
        catch (Exception ex)
        {
            Host?.Log($"Unhandled Exception: {ex.GetType().Name} " +
                      $"in {ex.Source}: {ex.Message}\n{ex.StackTrace}", LogSeverity.Fatal);

            _lastInitException = ex;
            ServiceStatus = (int)OscStatusEnum.InitException;
        }

        return 0;
    }

    public void OnLoad()
    {
        Host?.Log("Called OnLoad!");

        // Try loading OSC settings
        LoadSettings();

        // Construct the plugin UI
        MIpAddressLabel = new TextBlock
        {
            Margin = new Thickness { Top = 2 },
            Text = Host?.RequestLocalizedString("/Settings/Labels/IPAddress"),
            FontWeight = FontWeights.SemiBold
        };
        MIpTextbox = new TextBox
        {
            PlaceholderText = "localhost",
            Text = _sOscConfig.TargetIpAddress
        };

        MUdpPortLabel = new TextBlock
        {
            Margin = new Thickness { Top = 2 },
            Text = Host?.RequestLocalizedString("/Settings/Labels/UDPPort"),
            FontWeight = FontWeights.SemiBold
        };
        MUdpPortTextbox = new TextBox
        {
            PlaceholderText = "9000",
            Text = _sOscConfig.OscSendPort.ToString()
        };

        MTcpPortLabel = new TextBlock
        {
            Margin = new Thickness { Top = 2 },
            Text = Host?.RequestLocalizedString("/Settings/Labels/TCPPort"),
            FontWeight = FontWeights.SemiBold
        };
        MTcpPortTextbox = new TextBox
        {
            PlaceholderText = GetAvailableTcpPort().ToString(),
            Text = _sOscConfig.TcpPort.ToString()
        };

        MIpTextbox.TextChanged += (_, _) =>
        {
            var currentIp = MIpTextbox.Text.Length == 0 ? MIpTextbox.PlaceholderText : MIpTextbox.Text;
            if (ValidateIp(currentIp))
            {
                _sOscConfig.TargetIpAddress = currentIp;
                IsIpValid = true;
            }
            else
            {
                IsIpValid = false;
            }
        };

        MUdpPortTextbox.TextChanged += (_, _) =>
        {
            var currentOscPort = MUdpPortTextbox.Text.Length == 0
                ? MUdpPortTextbox.PlaceholderText
                : MUdpPortTextbox.Text;
            if (int.TryParse(currentOscPort.AsSpan(), out var result)) _sOscConfig.OscSendPort = result;
        };

        MTcpPortTextbox.TextChanged += (_, _) =>
        {
            var currentTcpPort = MTcpPortTextbox.Text.Length == 0
                ? MTcpPortTextbox.PlaceholderText
                : MTcpPortTextbox.Text;
            if (int.TryParse(currentTcpPort.AsSpan(), out var result)) _sOscConfig.TcpPort = result;
        };

        // UI Layout
        Grid.SetColumn(MIpAddressLabel, 0);
        Grid.SetRow(MIpAddressLabel, 0);
        Grid.SetColumn(MIpTextbox, 1);
        Grid.SetRow(MIpTextbox, 0);

        Grid.SetColumn(MUdpPortLabel, 0);
        Grid.SetRow(MUdpPortLabel, 1);
        Grid.SetColumn(MUdpPortTextbox, 1);
        Grid.SetRow(MUdpPortTextbox, 1);

        Grid.SetColumn(MTcpPortLabel, 0);
        Grid.SetRow(MTcpPortLabel, 2);
        Grid.SetColumn(MTcpPortTextbox, 1);
        Grid.SetRow(MTcpPortTextbox, 2);

        // Creates UI
        MInterfaceRoot = new Page
        {
            Content = new Grid
            {
                Margin = new Thickness { Left = 3 },
                Children =
                {
                    MIpAddressLabel, MIpTextbox,
                    MUdpPortLabel, MUdpPortTextbox,
                    MTcpPortLabel, MTcpPortTextbox
                },
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) }
                },
                RowSpacing = 6,
                RowDefinitions =
                {
                    new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                    new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                    new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
                }
            }
        };

        PluginLoaded = true;
    }

    public bool? RequestServiceRestart(string reason, bool wantReply = false)
    {
        Host?.Log($"Requested restart; {(wantReply ? "Expecting reply" : "")} with reason \"{reason}\"!");
        return wantReply ? false : null;
    }

    public void Shutdown()
    {
        Host?.Log("Shutting down...");

        _oscQueryService?.Dispose();
        _oscQueryService = null;

        _oscQuery?.Dispose();
        _oscQuery = null;

        _receiver.Dispose();
        _receiver = null;

        ServiceStatus = (int)OscStatusEnum.Unknown;
    }

    public Task<IEnumerable<(TrackerBase Tracker, bool Success)>> SetTrackerStates(
        IEnumerable<TrackerBase> trackerBases, bool wantReply = true)
    {
        if (!_receivers.Any())
        {
            Host?.Log("OSC Client is null!", LogSeverity.Warning);
            return Task.FromResult(wantReply ? trackerBases.Select(x => (x, false)) : null);
        }

        var trackers = trackerBases.ToList();
        foreach (var receiver in _receivers.Values.ToList())
            try
            {
                foreach (var tracker in trackers)
                {
                    var trackerId = TrackerRoleToOscId(tracker.Role);
                    if (trackerId <= 0) continue;

                    SendTrackerDataToReceiver(trackerId.ToString(),
                        new OscPosition(tracker.Position, tracker.Orientation), receiver);
                }
            }
            catch (Exception ex)
            {
                Host?.Log($"Unhandled Exception: {ex.GetType().Name} " +
                          $"in {ex.Source}: {ex.Message}\n{ex.StackTrace}", LogSeverity.Fatal);
                return Task.FromResult(trackers.Select(x => (x, false)));
            }

        return Task.FromResult(trackers.Select(x => (x, true)));
    }

    public Task<IEnumerable<(TrackerBase Tracker, bool Success)>> UpdateTrackerPoses(
        IEnumerable<TrackerBase> trackerBases, bool wantReply = true, CancellationToken? token = null)
    {
        if (!_receivers.Any())
        {
            Host?.Log("OSC Client is null!", LogSeverity.Warning);
            return Task.FromResult(wantReply ? trackerBases.Select(x => (x, false)) : null);
        }

        var trackers = trackerBases.ToList();
        foreach (var receiver in _receivers.Values.ToList())
            try
            {
                foreach (var tracker in trackers)
                {
                    var trackerId = TrackerRoleToOscId(tracker.Role);

                    if (trackerId <= 0) continue;

                    SendTrackerDataToReceiver(trackerId.ToString(),
                        new OscPosition(tracker.Position, tracker.Orientation), receiver);
                }
            }
            catch (Exception ex)
            {
                Host?.Log($"Unhandled Exception: {ex.GetType().Name} " +
                          $"in {ex.Source}: {ex.Message}\n{ex.StackTrace}", LogSeverity.Fatal);
                return Task.FromResult(trackers.Select(x => (x, false)));
            }

        return Task.FromResult(trackers.Select(x => (x, true)));
    }

    public TrackerBase GetTrackerPose(string contains, bool canBeFromAmethyst = true)
    {
        // Get pos & rot
        // ReSharper disable once AssignNullToNotNullAttribute
        return null;
    }

    public Task<(int Status, string StatusMessage, long PingTime)> TestConnection()
    {
        // @TODO: Test connection somehow
        Host?.Log("TestConnection!");
        return Task.FromResult((_receivers.Any() ? 0 : -1, "OK", 0L));
    }

    private async void OnOscQueryServiceFound(OSCQueryServiceProfile profile)
    {
        try
        {
            Host?.Log($"Found service {profile.name} at {profile.address}!");
            if (!await ServiceSupportsTracking(profile)) return;

            var hostInfo = await GetHostInfo(profile.address, profile.port);
            if (_receivers.TryGetValue(profile.name, out var value) &&
                value.Destination.Address.Equals(profile.address) &&
                value.Destination.Port == hostInfo.oscPort)
            {
                Host?.Log($"Service with key \"{profile.name}\" at " +
                          $"{profile.address}:{hostInfo.oscPort} already registered, skipping");
                return;
            }

            AddTrackingReceiver(profile.name, profile.address, hostInfo.oscPort);

            Host?.Log($"Set up {profile.name} at {profile.address}:{hostInfo.oscPort}");
            ServiceStatus = (int)OscStatusEnum.Success;
            Host?.RefreshStatusInterface(); // We're connected now!
            return; // That's all (assuming everything's okay.....)

            // Checks for compatibility by looking for matching Chatbox root node
            async Task<bool> ServiceSupportsTracking(OSCQueryServiceProfile p)
            {
                var tree = await GetOSCTree(p.address, p.port);
                return tree.GetNodeWithPath(TRACKERS_ROOT) != null;
            }

            // Does the actual construction of the OSC Client
            void AddTrackingReceiver(string key, IPAddress address, int port)
            {
                var receiver = new OscClientPlus(address.ToString(), port);
                _receivers[key] = receiver;
            }
        }
        catch (Exception e)
        {
            Host?.Log($"Couldn't set up service with key \"{profile.name}\": {e.Message}");
            Host?.Log(e);
        }
    }

    private void SetupTracker(int index)
    {
        var trackerName = index == 0 ? "head" : index.ToString();
        Positions[index] = new OscPosition(); // Prepare the container

        _oscQuery.AddEndpoint($"{TRACKERS_ROOT}/{trackerName}/{TRACKERS_POSITION}", "fff",
            Attributes.AccessValues.WriteOnly);
        _oscQuery.AddEndpoint($"{TRACKERS_ROOT}/{trackerName}/{TRACKERS_ROTATION}", "fff",
            Attributes.AccessValues.WriteOnly);

        var result = _receiver.TryAddMethod($"{TRACKERS_ROOT}/{trackerName}/{TRACKERS_POSITION}",
            message =>
            {
                if (message.ReadFloatElement(0) != 0)
                    Positions[index].Position = new Vector3(
                        message.ReadFloatElement(0),
                        message.ReadFloatElement(1),
                        -message.ReadFloatElement(2));
            }
        );

        Host?.Log($"TryAddMethod for \"head\" returned {result}");
        result = _receiver.TryAddMethod($"{TRACKERS_ROOT}/{trackerName}/{TRACKERS_ROTATION}",
            message =>
            {
                if (message.ReadFloatElement(0) != 0)
                    Positions[index].Orientation = Quaternion.CreateFromYawPitchRoll(
                        message.ReadFloatElement(1),
                        message.ReadFloatElement(0),
                        message.ReadFloatElement(2));
            }
        );

        Host?.Log($"TryAddMethod for \"head\" returned {result}");
    }

    private static int TrackerRoleToOscId(TrackerType role)
    {
        return role switch
        {
            TrackerType.TrackerWaist => 1,
            TrackerType.TrackerLeftFoot => 2,
            TrackerType.TrackerRightFoot => 3,
            TrackerType.TrackerLeftKnee => 4,
            TrackerType.TrackerRightKnee => 5,
            TrackerType.TrackerLeftElbow => 6,
            TrackerType.TrackerRightElbow => 7,
            TrackerType.TrackerChest => 8,
            _ => -1
        };
    }

    private void LoadSettings()
    {
        // Host should already be resolved at this point, but check anyway
        _sOscConfig.TargetIpAddress = ValidateIp( // Check if the saved IP is valid, fallback to local
            Host?.PluginSettings.GetSetting("ipAddress", "127.0.0.1"), "127.0.0.1");
        _sOscConfig.OscSendPort = Host?.PluginSettings.GetSetting("oscPort", 9000) ?? 9000;
        _sOscConfig.TcpPort = Host?.PluginSettings.GetSetting("tcpPort", 54126) ?? 54126;
    }

    private void SaveSettings()
    {
        Host?.PluginSettings.SetSetting("ipAddress", _sOscConfig.TargetIpAddress);
        Host?.PluginSettings.SetSetting("oscPort", _sOscConfig.OscSendPort);
        Host?.PluginSettings.SetSetting("tcpPort", _sOscConfig.TcpPort);
    }

    private static bool ValidateIp(string ip)
    {
        return !string.IsNullOrEmpty(ip) &&
               (ip.ToLowerInvariant() == "localhost" ||
                IPAddress.TryParse(ip.AsSpan(), out _));
    }

    private static string ValidateIp(string ip, string fallback)
    {
        return !string.IsNullOrEmpty(ip) &&
               (ip.ToLowerInvariant() == "localhost" ||
                IPAddress.TryParse(ip.AsSpan(), out _))
            ? ip // Return the provided one if valid
            : fallback; // Return the placeholder
    }

    /// Convenience function to send tracker data from a transform and name
    /// The Head data should be sent like this:
    /// `/tracking/trackers/head/position`
    /// `/tracking/trackers/head/rotation`
    /// 
    /// The Tracker data should be sent like this, where `i` is the number of the tracker between 1-8 (no tracker 0!)
    /// `/tracking/trackers/i/position`
    /// `/tracking/trackers/i/rotation`
    private void SendTrackerDataToReceiver(string trackerName, OscPosition target, OscClientPlus receiver)
    {
        // Exit early if transform is null
        if (target is null) return;

        receiver.Send($"{TRACKERS_ROOT}/{trackerName}/{TRACKERS_POSITION}",
            new BuildSoft.OscCore.UnityObjects.Vector3(target.Position.X, target.Position.Y, -target.Position.Z));

        var eulerAngles = NumericExtensions.ToEulerAngles(target.Orientation);
        receiver.Send($"{TRACKERS_ROOT}/{trackerName}/{TRACKERS_ROTATION}",
            new BuildSoft.OscCore.UnityObjects.Vector3(eulerAngles.X, eulerAngles.Y, eulerAngles.Z));
    }

    private enum OscStatusEnum
    {
        Unknown = -2,
        InitException,
        Success
    }

    #region UI Elements

    private Page MInterfaceRoot { get; set; }
    private TextBox MTcpPortTextbox { get; set; }
    private TextBox MUdpPortTextbox { get; set; }
    private TextBox MIpTextbox { get; set; }
    private TextBlock MIpAddressLabel { get; set; }
    private TextBlock MTcpPortLabel { get; set; }
    private TextBlock MUdpPortLabel { get; set; }

    #endregion
}

internal class SetupData : ICoreSetupData
{
    public object PluginIcon => new PathIcon
    {
        Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry),
            "M404.7,52.8h-383C9.7,52.9,0,62.6,0,74.5v90.6c0,12,9.7,21.7,21.7,21.7h333.9l29.5,38c3.7,4.7,7.6,7.1,11.6,7.1c2.6,0,5.1-1,6.9-2.9c2.2-2.2,3.3-5.3,3.3-9.2v-33.1c11.1-1.1,19.5-10.5,19.5-21.6V74.5C426.4,62.5,416.7,52.8,404.7,52.8C404.7,52.8,404.7,52.8,404.7,52.8z M418.4,75.1v90.6c0,7.6-6.1,13.7-13.7,13.7l0,0h-5.8l0,40.5v0.5c0,2.7-0.8,4-2.2,4s-3.2-1.3-5.3-4l-31.9-41H21.7c-7.6,0-13.7-6.1-13.7-13.7c0,0,0,0,0,0V74.5c0-7.6,6.1-13.7,13.7-13.7c0,0,0,0,0,0h383c7.6,0,13.7,6.1,13.7,13.7c0,0,0,0,0,0L418.4,75.1zM76,83.7c-2.2,0-3.8,0.7-4.3,2.6l-15,53.5L41.5,86.3c-0.5-1.9-2.2-2.6-4.3-2.6c-3.5,0-8.1,2.2-8.1,5.3c0,0.3,0.1,0.7,0.2,1l0,0l19,62.1c1,3,4.5,4.3,8.3,4.3s7.4-1.4,8.3-4.3l18.9-62.1c0.1-0.3,0.2-0.7,0.2-1C84.1,86,79.4,83.7,76,83.7zM126,124.1c7.4-2.3,12.9-8.4,12.9-19.7c0-15.7-10.5-20.6-23.4-20.6H96c-2.2-0.1-4.1,1.6-4.1,3.9c0,0,0,0.1,0,0.1v64.2c0,2.7,3.2,4,6.4,4s6.4-1.4,6.4-4v-25.8h8.3l14,27.2c0.8,1.8,2.5,3,4.4,3.1c3.8,0,8-3.4,8-6.7c0-0.6-0.2-1.1-0.5-1.6L126,124.1z M115.6,116h-10.9V95h10.9c6.4,0,10.6,2.8,10.6,10.6S122,116,115.6,116zM312.1,99.8 303.5,130.1 320.6,130.1 M383.6,77.2H176.1c-7.6,0-13.7,6.1-13.7,13.7v57.8c0,7.6,6.1,13.7,13.7,13.7h207.5c7.6,0,13.7-6.1,13.7-13.7c0,0,0,0,0,0V90.9C397.3,83.3,391.1,77.2,383.6,77.2L383.6,77.2z M196.4,145.2c9,0,9.8-6.4,10.1-10.6c0.2-3.1,3-4,6.3-4c4.4,0,6.5,1.3,6.5,6.3c0,11.9-9.8,19.5-23.6,19.5c-12.4,0-22.7-6.1-22.7-22.6v-27.6c0-16.5,10.4-22.6,22.8-22.6c13.7,0,23.5,7.3,23.5,18.8c0,5.1-2.1,6.3-6.4,6.3c-3.6,0-6.3-1.1-6.4-4s-0.9-9.8-10.3-9.8c-6.6,0-10.4,3.7-10.4,11.3v27.5C185.8,141.6,189.6,145.2,196.4,145.2L196.4,145.2z M276.8,151.8c0,2.7-3.3,4-6.4,4s-6.4-1.4-6.4-4v-28.3h-22.2v28.3c0,2.7-3.3,4-6.4,4s-6.4-1.4-6.4-4V87.7c0-2.8,3.2-4,6.4-4s6.4,1.2,6.4,4v25.8H264V87.7c0-2.8,3.2-4,6.4-4s6.4,1.2,6.4,4L276.8,151.8z M331.5,155.8L331.5,155.8c-2.2,0-3.9-0.7-4.3-2.6l-3.8-13.1h-22.6l-3.8,13.1c-0.5,1.9-2.2,2.6-4.3,2.6c-3.5,0-8.1-2.2-8.1-5.3c0-0.3,0.1-0.7,0.2-1l19-62.1c0.9-3,4.5-4.3,8.2-4.3s7.4,1.4,8.3,4.3l19,62.1c0.1,0.3,0.2,0.7,0.2,1C339.6,153.6,335,155.9,331.5,155.8L331.5,155.8z M382.6,95.2h-14.4v56.5c0,2.7-3.3,4-6.4,4s-6.4-1.4-6.4-4V95.3h-14.5c-2.6,0-4-2.7-4-5.8c0-2.8,1.3-5.7,4-5.7h41.8c2.8,0,4,3,4,5.7C386.7,92.5,385.2,95.3,382.6,95.2L382.6,95.2zM196.4,145.2 196.4,145.2 196.4,145.2 M25.4,25.5c1.7,1.4,3.9,2.1,6.1,2c2.3,0.1,4.5-0.6,6.3-2c1.6-1.3,2.5-3.2,2.4-5.2c0.1-0.7-0.1-1.3-0.6-1.8c-0.5-0.4-1.2-0.5-1.8-0.4c-1.5,0-2.3,0.5-2.3,1.5c0,0.4-0.1,0.9-0.2,1.3c-0.1,0.4-0.3,0.8-0.5,1.2c-0.3,0.5-0.7,0.8-1.1,1c-0.6,0.3-1.3,0.4-1.9,0.4c-2.6,0-3.9-1.4-3.9-4.2V9.1c0-2.8,1.3-4.2,3.8-4.2c0.6,0,1.2,0.1,1.7,0.3c0.4,0.1,0.8,0.4,1.1,0.7c0.3,0.3,0.5,0.6,0.6,0.9c0.1,0.3,0.2,0.6,0.3,0.9c0,0.3,0.1,0.5,0.1,0.8c0,1,0.8,1.5,2.4,1.5c0.6,0.1,1.3-0.1,1.8-0.5C40,9,40.2,8.3,40.1,7.6c0.1-2-0.9-3.8-2.4-5c-1.8-1.3-4-2-6.2-1.9c-2.2-0.1-4.4,0.6-6.1,2c-1.6,1.3-2.3,3.4-2.3,6.3v10.2l0,0C23.1,22,23.8,24.1,25.4,25.5zM43.6,26.8c0.5,0.3,1.1,0.5,1.6,0.5c0.6,0,1.2-0.2,1.7-0.5c0.5-0.3,0.7-0.7,0.7-1v-6.5c-0.1-1.4,0.4-2.8,1.2-3.9c0.7-0.9,1.8-1.5,2.9-1.5H53c0.5,0,0.9-0.2,1.2-0.6c0.3-0.4,0.5-0.9,0.5-1.5c0-0.5-0.2-1-0.5-1.4C53.9,10,53.5,9.8,53,9.8h-1.2c-1,0-1.9,0.3-2.7,0.9c-0.8,0.6-1.4,1.4-1.8,2.2v-1.5c0-0.4-0.2-0.8-0.6-1.1c-0.5-0.3-1-0.4-1.5-0.4c-0.6,0-1.1,0.1-1.7,0.4c-0.4,0.2-0.6,0.6-0.6,1v14.3C43,26.2,43.2,26.6,43.6,26.8zM58.1,25.6c1.8,1.4,4,2.2,6.3,2c1.6,0.1,3.1-0.3,4.6-1c1.2-0.7,1.8-1.3,1.8-2c0-0.5-0.2-1-0.5-1.4c-0.3-0.5-0.8-0.7-1.3-0.7c-0.6,0.1-1.2,0.4-1.7,0.7c-0.8,0.4-1.8,0.7-2.7,0.7c-1.1,0-2.2-0.3-3.1-1c-0.8-0.6-1.2-1.6-1.2-2.5v-0.5h7.4c0.4,0,0.8,0,1.2-0.1c0.3-0.1,0.7-0.2,1-0.4c0.4-0.2,0.7-0.6,0.8-1c0.2-0.6,0.3-1.2,0.3-1.8c0-1.9-0.8-3.7-2.2-4.9c-1.5-1.3-3.3-1.9-5.2-1.9c-2.1,0-4.1,0.8-5.6,2.2c-1.5,1.3-2.3,3.2-2.3,5.1v3.1l0,0C55.6,22.3,56.5,24.3,58.1,25.6z M60.3,16.2c0-0.8,0.3-1.5,0.9-1.9c1.3-1,3.2-1,4.5,0c0.6,0.5,0.9,1.2,0.9,1.9c0,0.3-0.1,0.5-0.2,0.7c-0.2,0.2-0.5,0.2-0.8,0.2h-5.3V16.2zM72.9,22.6 72.9,22.6 72.9,22.6 M77.6,27.6c2,0,3.8-1,5.5-3v1.1c0,0.5,0.2,0.9,0.6,1.1c0.4,0.3,1,0.5,1.5,0.5c0.6,0,1.1-0.1,1.6-0.4c0.4-0.2,0.6-0.6,0.7-1v-8.9c0.1-1.9-0.6-3.7-1.9-5.1c-1.3-1.3-3.3-2-5.9-2c-1.4,0-2.7,0.3-3.9,0.8c-1.2,0.5-1.9,1.1-1.9,1.8c0,0.6,0.1,1.1,0.4,1.6c0.2,0.4,0.7,0.7,1.2,0.7c0.5-0.2,0.9-0.4,1.3-0.6c0.9-0.4,1.8-0.6,2.8-0.6c0.9-0.1,1.8,0.3,2.4,1c0.5,0.7,0.8,1.5,0.8,2.4v0.5h-1.5c-2.2-0.1-4.3,0.3-6.3,1.2c-1.5,0.8-2.3,2.4-2.2,4c-0.1,1.4,0.4,2.7,1.3,3.7C75.1,27.2,76.3,27.6,77.6,27.6z M78.8,20.3c1.1-0.3,2.3-0.5,3.5-0.4h0.6v0.7c0,0.9-0.4,1.7-1.1,2.3c-0.6,0.6-1.4,1-2.2,1c-0.5,0-1-0.1-1.4-0.5c-0.4-0.4-0.6-0.9-0.5-1.4C77.5,21.2,78,20.5,78.8,20.3zM97.7,27.3h1.4c0.6,0,1.1-0.2,1.5-0.6c0.3-0.4,0.5-0.9,0.5-1.5c0-0.5-0.2-1-0.5-1.4c-0.4-0.4-0.9-0.6-1.5-0.6h-1.4c-0.6,0.1-1.1-0.1-1.6-0.4c-0.3-0.4-0.5-0.9-0.4-1.4v-7.6h4.3c0.4,0,0.7-0.2,0.9-0.6c0.2-0.4,0.3-0.8,0.3-1.3c0-0.5-0.1-0.9-0.3-1.3c-0.2-0.3-0.5-0.5-0.9-0.6h-4.3V4c0-0.4-0.3-0.8-0.7-1c-0.5-0.3-1.1-0.4-1.7-0.4c-0.6,0-1.1,0.1-1.6,0.4C91.3,3.2,91,3.6,91,4v17.5C91,25.4,93.2,27.3,97.7,27.3zM105.2,25.6c1.8,1.4,4,2.2,6.3,2c1.6,0.1,3.1-0.3,4.6-1c1.2-0.7,1.8-1.3,1.8-2c0-0.5-0.2-1-0.5-1.4c-0.3-0.4-0.8-0.7-1.3-0.7c-0.6,0.1-1.2,0.4-1.7,0.7c-0.8,0.4-1.8,0.7-2.7,0.7c-1.1,0-2.2-0.3-3.1-1c-0.8-0.6-1.2-1.6-1.2-2.5v-0.5h7.4c0.4,0,0.8,0,1.2-0.1c0.3-0.1,0.7-0.2,1-0.4c0.4-0.2,0.7-0.6,0.8-1c0.2-0.6,0.3-1.2,0.3-1.8c0-1.9-0.8-3.7-2.2-4.9c-1.5-1.3-3.3-2-5.3-1.9c-2.1,0-4.1,0.8-5.6,2.2c-1.5,1.3-2.3,3.2-2.3,5.1v3.1l0,0C102.7,22.3,103.6,24.3,105.2,25.6z M107.5,16.2c0-0.8,0.3-1.5,0.9-1.9c1.3-1,3.2-1,4.5,0c0.6,0.5,0.9,1.2,0.9,1.9c0,0.3-0.1,0.5-0.2,0.7c-0.2,0.2-0.5,0.2-0.8,0.2h-5.3V16.2zM120.9,26.8c0.4,0.5,1.1,0.7,1.7,0.7c0.6,0,1.2-0.3,1.6-0.8c0.9-1,0.9-2.5,0-3.5c-0.4-0.5-1-0.7-1.6-0.7c-0.6,0-1.3,0.3-1.7,0.7c-0.4,0.5-0.7,1.1-0.7,1.7C120.2,25.7,120.5,26.3,120.9,26.8L120.9,26.8zM136.8,26.2c1.8,1,3.9,1.5,5.9,1.4c2.3,0.1,4.5-0.6,6.3-2c1.6-1.5,2.5-3.6,2.4-5.8c0-1.3-0.3-2.7-0.9-3.9c-0.5-1-1.2-1.9-2.2-2.5c-0.9-0.6-1.8-1.1-2.8-1.6c-1-0.5-1.9-0.9-2.8-1.2c-0.8-0.3-1.5-0.7-2.2-1.3c-1-0.9-1.2-2.4-0.3-3.4c0.1-0.2,0.3-0.3,0.5-0.5c0.9-0.5,1.9-0.8,3-0.7c1.1,0,2.2,0.2,3.2,0.7c0.6,0.3,1.2,0.5,1.8,0.7c0.6,0,1.1-0.3,1.4-0.9c0.3-0.5,0.5-1.1,0.5-1.7c0-0.6-0.3-1.2-0.7-1.5c-0.6-0.4-1.2-0.7-1.9-0.9c-0.7-0.2-1.5-0.3-2.2-0.4c-0.7-0.1-1.3-0.1-2-0.1c-1.1,0-2.1,0.1-3.2,0.4c-1,0.3-1.9,0.7-2.8,1.2c-0.9,0.6-1.6,1.4-2.1,2.3c-0.5,1.1-0.8,2.3-0.8,3.4c0,1.1,0.2,2.1,0.6,3.1c0.4,0.8,0.9,1.6,1.7,2.2c0.7,0.6,1.5,1.1,2.3,1.5c0.8,0.4,1.7,0.8,2.5,1.2s1.6,0.7,2.3,1.1c0.7,0.3,1.2,0.8,1.7,1.4c0.4,0.6,0.7,1.2,0.6,1.9c0.1,0.9-0.4,1.8-1.2,2.4c-0.9,0.6-1.9,0.8-2.9,0.8c-0.8,0-1.6-0.1-2.3-0.5c-0.6-0.2-1.1-0.6-1.6-1c-0.4-0.4-0.8-0.7-1.2-1c-0.3-0.3-0.7-0.4-1.1-0.5c-0.6,0-1.1,0.3-1.4,0.9c-0.4,0.5-0.6,1.1-0.6,1.7l0,0C134.3,24.2,135.1,25.2,136.8,26.2zM154.9,26.8c0.5,0.3,1.1,0.5,1.6,0.5c0.6,0,1.2-0.1,1.6-0.5c0.4-0.2,0.7-0.6,0.7-1v-8.6c0-0.9,0.3-1.7,0.9-2.4c0.5-0.6,1.3-0.9,2.1-0.9c0.9,0,1.7,0.4,2.2,1c0.6,0.6,0.9,1.4,0.9,2.2v8.6c0,0.5,0.3,0.9,0.7,1.1c0.5,0.3,1.1,0.4,1.6,0.4c0.6,0,1.1-0.1,1.6-0.4c0.4-0.2,0.7-0.6,0.7-1.1v-8.6c0-1.9-0.6-3.8-1.9-5.2c-1.1-1.4-2.7-2.2-4.4-2.2c-1,0-2,0.3-2.8,0.8c-0.8,0.5-1.4,1.2-1.7,2V1.5c0-0.4-0.3-0.8-0.7-1c-0.5-0.3-1.1-0.4-1.7-0.4c-0.6,0-1.2,0.1-1.7,0.4c-0.4,0.2-0.6,0.6-0.6,1v24.3l0,0C154.3,26.2,154.5,26.6,154.9,26.8zM172.2,22.6 172.2,22.6 172.2,22.6 M176.9,27.6c2,0,3.8-1,5.5-3v1.1c0,0.5,0.2,0.9,0.6,1.1c0.4,0.3,1,0.5,1.5,0.5c0.6,0,1.1-0.1,1.6-0.4c0.4-0.2,0.6-0.6,0.6-1v-8.9c0.1-1.9-0.6-3.7-1.9-5.1c-1.3-1.3-3.3-2-5.9-2c-1.4,0-2.7,0.3-3.9,0.8c-1.2,0.5-1.9,1.1-1.9,1.8c0,0.6,0.1,1.1,0.4,1.6c0.2,0.4,0.7,0.7,1.2,0.7c0.5-0.2,0.9-0.4,1.3-0.6c0.9-0.4,1.8-0.6,2.8-0.6c0.9-0.1,1.8,0.3,2.4,1c0.5,0.7,0.8,1.5,0.8,2.4v0.5h-1.5c-2.2-0.1-4.3,0.3-6.3,1.2c-1.5,0.8-2.3,2.4-2.2,4c-0.1,1.4,0.4,2.7,1.3,3.7C174.5,27.2,175.7,27.6,176.9,27.6z M178.2,20.3c1.1-0.3,2.3-0.5,3.5-0.4h0.6v0.7c0,0.9-0.4,1.7-1.1,2.3c-0.6,0.6-1.4,1-2.2,1c-0.5,0-1-0.1-1.4-0.5c-0.4-0.4-0.6-0.9-0.5-1.4C176.9,21.2,177.4,20.5,178.2,20.3L178.2,20.3zM191,26.8c0.5,0.3,1.1,0.5,1.6,0.5c0.6,0,1.2-0.2,1.7-0.5c0.5-0.3,0.7-0.7,0.7-1v-6.5c-0.1-1.4,0.4-2.8,1.2-3.9c0.7-0.9,1.8-1.5,2.9-1.5h1.1c0.5,0,0.9-0.2,1.2-0.6c0.3-0.4,0.5-0.9,0.5-1.5c0-0.5-0.2-1-0.5-1.4c-0.3-0.4-0.7-0.6-1.2-0.6h-1.1c-1,0-1.9,0.3-2.6,0.9c-0.8,0.6-1.4,1.4-1.8,2.3v-1.5c0-0.4-0.2-0.8-0.6-1.1c-0.5-0.3-1-0.4-1.5-0.4c-0.6,0-1.1,0.1-1.6,0.4c-0.4,0.2-0.6,0.6-0.6,1.1v14.3C190.4,26.2,190.6,26.6,191,26.8zM205.5,25.6c1.8,1.4,4,2.2,6.3,2c1.6,0.1,3.1-0.3,4.6-1c1.2-0.7,1.8-1.3,1.8-2c0-0.5-0.2-1-0.5-1.4c-0.3-0.4-0.8-0.7-1.3-0.7c-0.6,0.1-1.2,0.4-1.7,0.7c-0.8,0.4-1.8,0.7-2.7,0.7c-1.1,0-2.2-0.3-3.1-1c-0.8-0.6-1.2-1.6-1.2-2.5v-0.5h7.4c0.4,0,0.8,0,1.2-0.1c0.3-0.1,0.7-0.2,1-0.4c0.4-0.2,0.7-0.6,0.8-1c0.2-0.6,0.3-1.2,0.3-1.8c0-1.9-0.8-3.7-2.2-4.9c-1.4-1.3-3.3-2-5.2-1.9c-2.1,0-4.1,0.7-5.6,2.2c-1.5,1.3-2.4,3.1-2.4,5.1v3.1l0,0C202.9,22.3,203.8,24.3,205.5,25.6z M207.7,16.2c0-0.8,0.3-1.5,0.9-1.9c1.3-1,3.2-1,4.5,0c0.6,0.5,0.9,1.2,0.9,1.9c0,0.3-0.1,0.5-0.2,0.7c-0.2,0.2-0.5,0.2-0.8,0.2h-5.3L207.7,16.2zM221.1,26.8c0.4,0.5,1.1,0.7,1.7,0.7c0.6,0,1.2-0.3,1.6-0.8c0.9-1,0.9-2.5,0-3.5c-0.4-0.5-1-0.7-1.6-0.7c-0.6,0-1.3,0.3-1.7,0.7c-0.4,0.5-0.7,1.1-0.7,1.7C220.5,25.7,220.7,26.3,221.1,26.8L221.1,26.8zM236.5,26.9c0.4,0.3,0.9,0.4,1.4,0.4h12.9c0.5,0,0.9-0.2,1.1-0.6c0.3-0.4,0.4-0.9,0.4-1.4c0-0.5-0.1-1-0.4-1.5c-0.2-0.4-0.6-0.6-1.1-0.6h-10.3v-7.3h5.5c0.4,0,0.9-0.2,1.1-0.6c0.3-0.4,0.4-0.8,0.4-1.2c0-0.5-0.1-0.9-0.4-1.3c-0.2-0.4-0.7-0.6-1.1-0.6h-5.5V4.9h10.3c0.4,0,0.9-0.2,1.1-0.6c0.3-0.4,0.4-1,0.4-1.5c0-0.5-0.1-1-0.3-1.4c-0.2-0.4-0.7-0.6-1.1-0.6h-13c-0.5,0-1,0.1-1.4,0.4c-0.4,0.2-0.6,0.6-0.6,1.1v23.6l0,0C235.8,26.3,236.1,26.7,236.5,26.9zM254.8,26.8c0.5,0.4,1.2,0.7,1.9,0.8c0.4,0,0.9-0.2,1.2-0.5l3.6-5.5l3.7,5.5c0.3,0.3,0.7,0.5,1.1,0.5c0.7,0,1.4-0.3,2-0.8c0.6-0.3,1-0.9,1-1.5c0-0.3-0.1-0.6-0.3-0.8l-4.3-6l4-5.6c0.2-0.2,0.3-0.5,0.3-0.8c-0.1-0.7-0.5-1.3-1.1-1.6c-0.6-0.5-1.3-0.7-2-0.8c-0.4,0-0.8,0.2-1.1,0.5l-3.3,5.3l-3.3-5.3c-0.2-0.3-0.6-0.5-1-0.5c-0.7,0-1.4,0.3-2,0.8c-0.6,0.3-1,0.9-1.1,1.6c0,0.3,0.1,0.5,0.3,0.8l4,5.6l-4.3,6c-0.2,0.2-0.3,0.5-0.3,0.8l0,0C253.7,25.8,254,26.3,254.8,26.8zM272.5,34.9c0.5,0.3,1.1,0.5,1.6,0.5c0.6,0,1.2-0.1,1.7-0.5c0.4-0.2,0.7-0.6,0.7-1v-9.2c0.4,0.8,1.1,1.5,1.9,2c0.9,0.6,1.9,0.9,2.9,0.9c1.7,0,3.4-0.9,4.4-2.2c1.3-1.4,1.9-3.3,1.9-5.2v-3c0-1.9-0.7-3.7-1.9-5.1c-1.1-1.4-2.8-2.2-4.6-2.2c-1,0-2.1,0.3-2.9,0.8c-0.8,0.5-1.5,1.2-2,2v-1.1c0-0.4-0.2-0.8-0.6-1.1c-0.5-0.3-1-0.4-1.6-0.4c-0.6,0-1.1,0.1-1.6,0.4c-0.4,0.2-0.6,0.6-0.6,1.1v22.3C271.9,34.3,272.1,34.7,272.5,34.9z M276.5,20.8v-3.6c0-1.7,1.4-3.2,3.1-3.2c0,0,0,0,0.1,0c0.9,0,1.7,0.4,2.3,1c0.6,0.6,1,1.4,1,2.2v3c0,0.9-0.4,1.7-1,2.3c-1.1,1.2-3,1.4-4.2,0.3c-0.1-0.1-0.2-0.1-0.2-0.2c-0.5-0.4-0.9-1-1-1.7L276.5,20.8zM292.5,25.6c1.8,1.4,4,2.2,6.3,2c1.6,0.1,3.1-0.3,4.6-1c1.2-0.7,1.8-1.3,1.8-2c0-0.5-0.2-1-0.5-1.4c-0.3-0.4-0.8-0.7-1.3-0.7c-0.6,0.1-1.2,0.4-1.7,0.7c-0.8,0.4-1.8,0.7-2.7,0.7c-1.1,0-2.2-0.3-3.1-1c-0.8-0.6-1.2-1.6-1.2-2.5v-0.5h7.4c0.4,0,0.8,0,1.2-0.1c0.3-0.1,0.7-0.2,1-0.4c0.4-0.2,0.7-0.6,0.8-1c0.2-0.6,0.3-1.2,0.3-1.8c0-1.9-0.8-3.7-2.2-4.9c-1.4-1.3-3.3-2-5.2-1.9c-2.1,0-4.1,0.7-5.6,2.2c-1.5,1.3-2.3,3.1-2.4,5.1v3.1l0,0C289.9,22.3,290.8,24.3,292.5,25.6z M294.7,16.2c0-0.8,0.3-1.5,0.9-1.9c1.3-1,3.2-1,4.5,0c0.6,0.5,0.9,1.2,0.9,1.9c0,0.3-0.1,0.5-0.2,0.7c-0.2,0.2-0.5,0.2-0.8,0.2h-5.3V16.2zM308.9,26.8c0.5,0.3,1.1,0.5,1.6,0.5c0.6,0,1.2-0.2,1.7-0.5c0.5-0.3,0.7-0.7,0.7-1v-6.5c-0.1-1.4,0.4-2.8,1.2-3.9c0.7-0.9,1.8-1.5,2.9-1.5h1.1c0.5,0,0.9-0.2,1.2-0.6c0.3-0.4,0.5-0.9,0.5-1.5c0-0.5-0.2-1-0.5-1.4c-0.3-0.4-0.7-0.6-1.2-0.6h-1.1c-1,0-1.9,0.3-2.6,0.9c-0.8,0.6-1.4,1.4-1.8,2.3v-1.5c0-0.4-0.2-0.8-0.6-1.1c-0.5-0.3-1-0.4-1.5-0.4c-0.6,0-1.1,0.1-1.6,0.4c-0.4,0.2-0.6,0.6-0.6,1.1v14.3C308.3,26.2,308.5,26.6,308.9,26.8zM324.4,10c-0.6,0-1.1,0.1-1.6,0.4c-0.4,0.2-0.6,0.6-0.6,1.1v14.3c0,0.4,0.3,0.8,0.6,1c1,0.6,2.2,0.6,3.2,0c0.4-0.2,0.6-0.6,0.7-1V11.5c0-0.4-0.3-0.8-0.6-1.1C325.5,10.1,324.9,10,324.4,10zM322.5,5.6c0.5,0.4,1.2,0.6,1.9,0.6c0.6,0,1.3-0.2,1.7-0.6c0.8-0.6,1-1.8,0.3-2.6c-0.1-0.1-0.2-0.2-0.3-0.3C325.6,2.2,325,2,324.4,2c-0.7,0-1.4,0.2-1.9,0.6c-0.5,0.3-0.8,0.9-0.8,1.5C321.7,4.7,322,5.2,322.5,5.6zM332.1,25.6c1.8,1.4,4,2.2,6.3,2c1.6,0.1,3.1-0.3,4.6-1c1.2-0.7,1.8-1.3,1.8-2c0-0.5-0.2-1-0.5-1.4c-0.3-0.4-0.8-0.7-1.3-0.7c-0.6,0.1-1.2,0.4-1.7,0.7c-0.8,0.4-1.8,0.7-2.7,0.7c-1.1,0-2.2-0.3-3.1-1c-0.8-0.6-1.2-1.6-1.2-2.5v-0.5h7.4c0.4,0,0.8,0,1.2-0.1c0.3-0.1,0.7-0.2,1-0.4c0.4-0.2,0.7-0.6,0.8-1c0.2-0.6,0.3-1.2,0.3-1.8c0-1.9-0.8-3.7-2.2-4.9c-1.5-1.3-3.3-2-5.3-1.9c-2.1,0-4,0.8-5.5,2.2c-1.5,1.3-2.3,3.2-2.3,5.1v3.1l0,0C329.6,22.3,330.5,24.2,332.1,25.6z M334.3,16.2c0-0.8,0.3-1.5,0.9-1.9c1.3-1,3.2-1,4.5,0c0.6,0.5,0.9,1.2,0.9,1.9c0,0.3-0.1,0.5-0.2,0.7c-0.2,0.2-0.5,0.2-0.8,0.2h-5.3V16.2zM348.5,26.8c1,0.6,2.3,0.6,3.3,0c0.4-0.2,0.7-0.6,0.7-1v-8.6c0-0.9,0.3-1.7,0.9-2.4c0.5-0.6,1.3-0.9,2.1-0.9c0.8,0,1.7,0.4,2.2,1c0.6,0.6,0.9,1.4,0.9,2.3v8.6c0,0.5,0.3,0.9,0.7,1.1c0.5,0.3,1.1,0.4,1.6,0.4c0.6,0,1.1-0.1,1.6-0.4c0.4-0.2,0.7-0.6,0.7-1.1v-8.6c0-1.9-0.6-3.8-1.9-5.2c-1.1-1.4-2.7-2.2-4.4-2.2c-1.1,0-2.1,0.3-3,0.8c-0.8,0.5-1.4,1.2-1.9,2v-1.1c0-0.4-0.2-0.8-0.6-1.1c-0.4-0.3-1-0.4-1.5-0.4c-0.6,0-1.2,0.1-1.7,0.4c-0.4,0.2-0.6,0.6-0.6,1.1v14.3l0,0C347.9,26.2,348.1,26.6,348.5,26.8zM368.3,25.6c1.3,1.3,3.3,2,5.9,2c1.5,0,2.9-0.3,4.2-0.9c1.2-0.6,1.8-1.3,1.8-2c0-0.5-0.2-1.1-0.6-1.5c-0.3-0.5-0.9-0.7-1.4-0.7c-0.5,0.1-0.9,0.3-1.3,0.5c-0.7,0.4-1.5,0.5-2.3,0.5c-2.4,0-3.6-1.1-3.6-3.3v-2.9c0-2.2,1.2-3.3,3.5-3.3c0.8,0,1.6,0.2,2.3,0.5c0.4,0.2,0.8,0.4,1.3,0.5c0.5,0,1-0.3,1.3-0.8c0.3-0.4,0.5-1,0.5-1.5c0-0.7-0.6-1.4-1.7-2c-1.3-0.6-2.7-0.9-4.1-0.9c-2.6,0-4.6,0.7-5.9,2c-1.4,1.5-2.1,3.4-2,5.4v2.9l0,0C366.2,22.1,366.9,24.1,368.3,25.6zM384.3,25.6c1.8,1.4,4,2.2,6.3,2c1.6,0.1,3.1-0.3,4.6-1c1.2-0.7,1.8-1.3,1.8-2c0-0.5-0.2-1-0.5-1.4c-0.3-0.4-0.8-0.7-1.3-0.7c-0.6,0.1-1.2,0.4-1.7,0.7c-0.8,0.4-1.8,0.7-2.7,0.7c-1.1,0-2.2-0.3-3.1-1c-0.8-0.6-1.2-1.6-1.2-2.5v-0.5h7.4c0.4,0,0.8,0,1.2-0.1c0.3-0.1,0.7-0.2,1-0.4c0.4-0.2,0.7-0.6,0.8-1c0.2-0.6,0.3-1.2,0.3-1.8c0-1.9-0.8-3.7-2.2-4.9c-1.5-1.3-3.3-2-5.3-1.9c-2.1,0-4.1,0.8-5.6,2.2c-1.5,1.3-2.3,3.2-2.3,5.1v3.1l0,0C381.8,22.3,382.7,24.3,384.3,25.6z M386.5,16.2c0-0.8,0.3-1.5,0.9-1.9c1.3-1,3.2-1,4.5,0c0.6,0.5,0.9,1.2,0.9,1.9c0,0.3-0.1,0.5-0.2,0.7c-0.2,0.2-0.5,0.2-0.8,0.2h-5.3V16.2zM400,26.8c0.4,0.5,1.1,0.7,1.7,0.7c0.6,0,1.2-0.3,1.6-0.8c0.9-1,0.9-2.5,0-3.5c-0.4-0.5-1-0.7-1.6-0.7c-0.6,0-1.3,0.3-1.7,0.7c-0.4,0.5-0.7,1.1-0.7,1.7C399.3,25.7,399.6,26.3,400,26.8L400,26.8z")
    };

    public string GroupName => string.Empty;
    public Type PluginType => typeof(IServiceEndpoint);
}