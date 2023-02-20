// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.ComponentModel.Composition;
using System.Net;
using System.Numerics;
using Amethyst.Plugins.Contract;
using CoreOSC;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VRC.OSCQuery;
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
        TcpPort = tcpPort == -1 ? OSCExtensions.GetAvailableTcpPort() : tcpPort;
    }
}

[Export(typeof(IServiceEndpoint))]
[ExportMetadata("Name", "OSC")]
[ExportMetadata("Guid", "K2VRTEAM-AME2-APII-SNDP-SENDPTVRCOSC")]
[ExportMetadata("Publisher", "K2VR Team")]
[ExportMetadata("Website", "https://github.com/KinectToVR/plugin_OSC")]
public class Osc : IServiceEndpoint
{
    private const string AmethystOscServiceName = "AMETHYST-OSC";
    private const string OscTargetAddressTrackersPosition = "/tracking/trackers/{0}/position";
    private const string OscTargetAddressTrackersRotation = "/tracking/trackers/{0}/rotation";

    private static OSCQueryService _sOscQueryService;
    private static UDPSender _sOscClient;
    private static OscConfig _sOscConfig = new("127.0.0.1");
    private static readonly Vector3 HeadOffset = new(0, 0, 0.2f);

    private Exception _lastInitException;
    [Import(typeof(IAmethystHost))] private IAmethystHost Host { get; set; }

    private bool IsIpValid { get; set; } = true;
    private bool IsOscPortValid { get; set; } = true;
    private bool IsTcpPortValid { get; set; } = true;
    private bool PluginLoaded { get; set; }

    private OscLogger Logger => AmethystLogger ??= new OscLogger(Host);
    private OscLogger AmethystLogger { get; set; }

    public bool IsSettingsDaemonSupported => true;
    public object SettingsInterfaceRoot => MInterfaceRoot;

    public int ServiceStatus { get; private set; } = (int)OscStatusEnum.Unknown;

    public string ServiceStatusString => Host?.RequestLocalizedString($"/Statuses/{(OscStatusEnum)ServiceStatus}")
        .Replace("{0}", _lastInitException?.Message ?? "[Not available]");

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
        get => null;
        set { }
    }

    public bool CanAutoStartAmethyst => false;
    public bool IsRestartOnChangesNeeded => false;

    public bool IsAmethystVisible => true;
    public string TrackingSystemName => "OSC";

    public (Vector3 Position, Quaternion Orientation)? HeadsetPose => null;

    public void DisplayToast((string Title, string Text) message)
    {
        // @TODO: Hope VRChat lets us do this
        Host?.Log("DisplayToast!");
    }

    public void Heartbeat()
    {
        // If the service hasn't started yet
        if (_sOscClient == null) return;

        var headJoint = Host?.GetHookJointPose();
        if (!headJoint.HasValue) return;

        // Vector3 eulerAngles = NumericExtensions.ToEulerAngles(headJoint.Value.Orientation);
        var position = headJoint.Value.Position + HeadOffset;

        _sOscClient.Send(new OscMessage(string.Format(OscTargetAddressTrackersPosition, "head"),
            position.X, position.Y, -position.Z));
        // s_oscClient.Send(new OscMessage(string.Format(OSC_TARGET_ADDRESS_TRACKERS_ROTATION, "head"), eulerAngles.X, eulerAngles.Y, eulerAngles.Z));
    }

    public int Initialize()
    {
        Host?.Log("Called Initialize!");
        Host?.Log("Init!");

        // Assume [nothing]
        ServiceStatus = (int)OscStatusEnum.Unknown;

        try
        {
            if (_sOscClient != null)
            {
                Host?.Log("OSC Client was already running! Shutting it down...", LogSeverity.Warning);
                _sOscClient.Close();
                _sOscClient = null;
            }

            if (_sOscQueryService != null)
            {
                Host?.Log("OSC Query Service was already running! Shutting it down...", LogSeverity.Warning);
                _sOscQueryService.Dispose();
                _sOscQueryService = null;
            }

            // Starts the OSC server
            _sOscQueryService = new OSCQueryServiceBuilder()
                .WithServiceName(AmethystOscServiceName)
                .WithLogger(Logger)
                .WithTcpPort(_sOscConfig.TcpPort)
                .WithUdpPort(_sOscConfig.OscSendPort)
                .WithDiscovery(new MeaModDiscovery(Logger))
                .StartHttpServer()
                .AdvertiseOSCQuery()
                .AdvertiseOSC()
                .Build();

            _sOscQueryService.RefreshServices();

            Host?.Log($"{_sOscQueryService.ServerName} running at TCP: " +
                      $"{_sOscQueryService.TcpPort} OSC: {_sOscQueryService.OscPort}");

            // s_oscClient = new UDPDuplex(s_oscConfig.TargetIpAddress, s_oscConfig.oscReceivePort, s_oscConfig.oscSendPort, HandleOscPacketEvent);
            _sOscClient = new UDPSender(_sOscConfig.TargetIpAddress, _sOscConfig.OscSendPort);
            // Host?.Log($"Started OSC Server at {s_oscClient.RemoteAddress}, sending on port: {s_oscClient.Port} receiving on port: {s_oscClient.RemotePort}");
            Host?.Log($"Started OSC Server at {_sOscClient.Address}, sending on port: {_sOscClient.Port}");

            ServiceStatus = (int)OscStatusEnum.Success;
            SaveSettings();
        }
        catch (Exception ex)
        {
            Host?.Log($"Unhandled Exception: {ex.GetType().Name} " +
                      $"in {ex.Source}: {ex.Message}\n{ex.StackTrace}", LogSeverity.Fatal);

            _lastInitException = ex;
            ServiceStatus = (int)OscStatusEnum.InitException;
        }

        // @TODO: Init
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
            PlaceholderText = OSCExtensions.GetAvailableTcpPort().ToString(),
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

        _sOscQueryService?.Dispose();
        _sOscQueryService = null;

        _sOscClient?.Close();
        _sOscClient = null;

        ServiceStatus = (int)OscStatusEnum.Unknown;
    }

    public Task<IEnumerable<(TrackerBase Tracker, bool Success)>> SetTrackerStates(
        IEnumerable<TrackerBase> trackerBases, bool wantReply = true)
    {
        if (_sOscClient == null)
        {
            Host?.Log("OSC Client is null!", LogSeverity.Warning);
            return Task.FromResult(wantReply ? trackerBases.Select(x => (x, false)) : null);
        }

        try
        {
            foreach (var tracker in trackerBases)
            {
                var trackerId = TrackerRoleToOscId(tracker.Role);
                if (trackerId <= 0) continue;

                var eulerAngles = NumericExtensions.ToEulerAngles(tracker.Orientation);
                var position = tracker.Position;

                _sOscClient.Send(new OscMessage(string.Format(OscTargetAddressTrackersPosition, trackerId),
                    position.X, position.Y, -position.Z));
                _sOscClient.Send(new OscMessage(string.Format(OscTargetAddressTrackersRotation, trackerId),
                    eulerAngles.X, eulerAngles.Y, eulerAngles.Z));
            }
        }
        catch (Exception ex)
        {
            Host?.Log($"Unhandled Exception: {ex.GetType().Name} " +
                      $"in {ex.Source}: {ex.Message}\n{ex.StackTrace}", LogSeverity.Fatal);
            return Task.FromResult(trackerBases.Select(x => (x, false)));
        }

        return Task.FromResult(trackerBases.Select(x => (x, true)));
    }

    public Task<IEnumerable<(TrackerBase Tracker, bool Success)>> UpdateTrackerPoses(
        IEnumerable<TrackerBase> trackerBases, bool wantReply = true)
    {
        if (_sOscClient == null)
        {
            Host?.Log("OSC Client is null!", LogSeverity.Warning);
            return Task.FromResult(wantReply ? trackerBases.Select(x => (x, false)) : null);
        }

        try
        {
            foreach (var tracker in trackerBases)
            {
                var trackerId = TrackerRoleToOscId(tracker.Role);

                if (trackerId <= 0) continue;
                var eulerAngles = NumericExtensions.ToEulerAngles(tracker.Orientation);
                var position = tracker.Position;

                _sOscClient.Send(new OscMessage(string.Format(OscTargetAddressTrackersPosition, trackerId),
                    position.X, position.Y, -position.Z));
                _sOscClient.Send(new OscMessage(string.Format(OscTargetAddressTrackersRotation, trackerId),
                    eulerAngles.X, eulerAngles.Y, eulerAngles.Z));
            }
        }
        catch (Exception ex)
        {
            Host?.Log($"Unhandled Exception: {ex.GetType().Name} " +
                      $"in {ex.Source}: {ex.Message}\n{ex.StackTrace}", LogSeverity.Fatal);
            return Task.FromResult(trackerBases.Select(x => (x, false)));
        }

        return Task.FromResult(trackerBases.Select(x => (x, true)));
    }

    public TrackerBase GetTrackerPose(string contains, bool canBeFromAmethyst = true)
    {
        // Get pos & rot
        return null;
    }

    public Task<(int Status, string StatusMessage, long PingTime)> TestConnection()
    {
        // @TODO: Test connection somehow
        Host?.Log("TestConnection!");
        return Task.FromResult((0, "OK", 0L));
    }

    private void HandleOscPacketEvent(OscPacket packet)
    {
        var message = (OscMessage)packet;
        Host?.Log($"Received message at {message.Address} with value {message.Arguments}!");

        // @TODO: Handle specific messages?
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