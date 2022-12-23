// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Amethyst.Plugins.Contract;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Net.Sockets;
using TrackerType = Amethyst.Plugins.Contract.TrackerType;
using VRC.OSCQuery;
using OSCExtensions = VRC.OSCQuery.Extensions;
using CoreOSC;
using System.Data;
using System.Net;

namespace plugin_OSC;

public static class ServiceData {
    public const string Name = "OSC";
    public const string Guid = "K2VRTEAM-AME2-APII-SNDP-SENDPTVRCOSC";
}

public struct OSCConfig {
    private string m_targetIpAddress;
    public string targetIpAddress {
        get => m_targetIpAddress;
        set {
            m_targetIpAddress = value;
            if (value.ToLowerInvariant() == "localhost") {
                m_targetIpAddress = "127.0.0.1";
            }
        }
    }
    public int oscSendPort, oscReceivePort, tcpPort;

    public OSCConfig(string targetIpAddress = "127.0.0.1", int udpSendPort = -1, int udpListenPort = -1, int tcpPort = -1) {
        this.targetIpAddress    = targetIpAddress;
        this.oscSendPort        = udpSendPort   == -1 ? 9000 : udpSendPort;
        this.oscReceivePort     = udpListenPort == -1 ? 9001 : udpListenPort;
        this.tcpPort            = tcpPort       == -1 ? OSCExtensions.GetAvailableTcpPort() : tcpPort;
    }
}

[Export(typeof(IServiceEndpoint))]
[ExportMetadata("Name", ServiceData.Name)]
[ExportMetadata("Guid", ServiceData.Guid)]
[ExportMetadata("Publisher", "K2VR Team")]
[ExportMetadata("Website", "https://github.com/KinectToVR/plugin_OSC")]
public class OSC : IServiceEndpoint {

    private const string AMETHYST_OSC_SERVICE_NAME = "AMETHYST-OSC";
    private const string OSC_TARGET_ADDRESS_TRACKERS_POSITION = "/tracking/trackers/{0}/position";
    private const string OSC_TARGET_ADDRESS_TRACKERS_ROTATION = "/tracking/trackers/{0}/rotation";
    private static OSCQueryService s_oscQueryService;
    private static UDPSender s_oscClient;

    private static OSCConfig s_oscConfig = new OSCConfig("127.0.0.1", -1, -1, -1);
    private bool m_isIpValid { get; set; } = true;
    private bool m_isOscPortValid { get; set; } = true;
    private bool m_isTcpPortValid { get; set; } = true;
    private bool m_pluginLoaded { get; set; }
    public OSCLogger Logger {
        get {
            if ( m_ameLogger == null) {
                m_ameLogger = new OSCLogger(Host);
            }
            return m_ameLogger;
        }
    }
    private OSCLogger m_ameLogger { get; set; }

    #region UI Elements
    private Page m_interfaceRoot { get; set; }
    private TextBox m_tcpPortTextbox { get; set; }
    private TextBox m_udpPortTextbox { get; set; }
    private TextBox m_ipTextbox { get; set; }
    private TextBlock m_ipAddressLabel { get; set; }
    private TextBlock m_tcpPortLabel { get; set; }
    private TextBlock m_udpPortLabel { get; set; }
    #endregion

    [Import(typeof(IAmethystHost))] private IAmethystHost Host { get; set; }
    public bool IsSettingsDaemonSupported => true;

    public object SettingsInterfaceRoot => m_interfaceRoot;

    public int ServiceStatus { get; private set; }

    [DefaultValue("Not Defined\nE_NOT_DEFINED\nStatus message not defined!")]
    public string ServiceStatusString { get; private set; }

    public SortedSet<TrackerType> AdditionalSupportedTrackerTypes =>
        new()
        {
            // TrackerType.TrackerHanded,       // OSC models a humanoid
            TrackerType.TrackerLeftFoot,     // Already OK
            TrackerType.TrackerRightFoot,    // Already OK
            // TrackerType.TrackerLeftShoulder,
            // TrackerType.TrackerRightShoulder,
            TrackerType.TrackerLeftElbow,
            TrackerType.TrackerRightElbow,
            TrackerType.TrackerLeftKnee,
            TrackerType.TrackerRightKnee,
            TrackerType.TrackerWaist,        // Already OK
            TrackerType.TrackerChest,        // OSC models a humanoid
            // TrackerType.TrackerCamera,       // OSC models a humanoid
            // TrackerType.TrackerKeyboard      // OSC models a humanoid
        };

    public bool AutoStartAmethyst { get => false; set { } }
    public bool AutoCloseAmethyst { get => false; set { } }
    public bool CanAutoStartAmethyst => false;

    public bool IsRestartOnChangesNeeded => false;
    public InputActions ControllerInputActions { get => null; set { } }
    public bool IsAmethystVisible => true;
    public string TrackingSystemName => "OSC";

    public (Vector3 Position, Quaternion Orientation)? HeadsetPose => null;

    public void DisplayToast((string Title, string Text) message) {
        // @TODO: Hope VRChat lets us do this
        Host?.Log("DisplayToast!");
    }

    public void Heartbeat() {
        if ( s_oscClient == null ) {
            // If the service hasn't started yet
            return;
        }

        (Vector3 Position, Quaternion Orientation)? headJoint = this.Host?.GetHookJointPose(false);
        if ( headJoint.HasValue ) {
            
            // Vector3 eulerAngles = NumericExtensions.ToEulerAngles(headJoint.Value.Orientation);
            Vector3 position = headJoint.Value.Position;

            s_oscClient.Send(new OscMessage(string.Format(OSC_TARGET_ADDRESS_TRACKERS_POSITION, "head"), position.X, position.Y, position.Z));
            // s_oscClient.Send(new OscMessage(string.Format(OSC_TARGET_ADDRESS_TRACKERS_ROTATION, "head"), eulerAngles.X, eulerAngles.Y, eulerAngles.Z));
        }
    }

    public int Initialize() {

        Host?.Log("Called Initialize!");

        ServiceStatus = 0;

        Host?.Log("Init!");

        try {


            if ( s_oscClient != null ) {
                Host?.Log("OSC Client was already running! Shutting it down...", LogSeverity.Warning);
                s_oscClient.Close();
                s_oscClient = null;
            }

            if ( s_oscQueryService != null ) {
                Host?.Log("OSC Query Service was already running! Shutting it down...", LogSeverity.Warning);
                s_oscQueryService.Dispose();
                s_oscQueryService = null;
            }

            // Starts the OSC server
            s_oscQueryService = new OSCQueryServiceBuilder()
                .WithServiceName(AMETHYST_OSC_SERVICE_NAME)
                .WithLogger(Logger)
                .WithTcpPort(s_oscConfig.tcpPort)
                .WithUdpPort(s_oscConfig.oscSendPort)
                .WithDiscovery(new MeaModDiscovery(Logger))
                .StartHttpServer()
                .AdvertiseOSCQuery()
                .AdvertiseOSC()
                .Build();

            s_oscQueryService.RefreshServices();

            Host?.Log($"{s_oscQueryService.ServerName} running at TCP: {s_oscQueryService.TcpPort} OSC: {s_oscQueryService.OscPort}");

            // s_oscClient = new UDPDuplex(s_oscConfig.targetIpAddress, s_oscConfig.oscReceivePort, s_oscConfig.oscSendPort, HandleOscPacketEvent);
            s_oscClient = new UDPSender(s_oscConfig.targetIpAddress, s_oscConfig.oscSendPort);
            // Host?.Log($"Started OSC Server at {s_oscClient.RemoteAddress}, sending on port: {s_oscClient.Port} receiving on port: {s_oscClient.RemotePort}");
            Host?.Log($"Started OSC Server at {s_oscClient.Address}, sending on port: {s_oscClient.Port}");
            SaveSettings();

        } catch ( Exception ex ) {
            Host?.Log($"Unhandled Exception: {ex.GetType().Name} in {ex.Source}: {ex.Message}\n{ex.StackTrace}", LogSeverity.Fatal);
            ServiceStatus = -1;
        }

        // @TODO: Init
        return 0;
    }

    public void OnLoad() {

        Host?.Log("Called OnLoad!");

        LoadSettings();

        m_ipTextbox = new TextBox() {
            PlaceholderText = "localhost",
            Text = s_oscConfig.targetIpAddress,
            Margin = new Thickness(0, 0, 0, 0)
        };
        m_udpPortTextbox = new TextBox() {
            PlaceholderText = "9000",
            Text = s_oscConfig.oscSendPort.ToString(),
            Margin = new Thickness(0, 0, 0, 0)
        };
        m_tcpPortTextbox = new TextBox() {
            PlaceholderText = OSCExtensions.GetAvailableTcpPort().ToString(),
            Text = s_oscConfig.tcpPort.ToString(),
            Margin = new Thickness(0, 5, 0, 0)
        };
        m_ipAddressLabel = new TextBlock() {
            Text = Host?.RequestLocalizedString("/Settings/Labels/IPAddress"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 5, 0, 0)
        };
        m_udpPortLabel = new TextBlock() {
            Text = Host?.RequestLocalizedString("/Settings/Labels/UDPPort"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 5, 0, 0)
        };
        m_tcpPortLabel = new TextBlock() {
            Text = Host?.RequestLocalizedString("/Settings/Labels/TCPPort"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 5, 0, 0)
        };
        m_ipTextbox.TextChanged += (sender, args) => {
            string currentIp = m_ipTextbox.Text.Length == 0 ? m_ipTextbox.PlaceholderText : m_ipTextbox.Text;
            if ( ValidateIp (currentIp) ) {
                s_oscConfig.targetIpAddress = currentIp;
                m_isIpValid = true;
            } else {
                m_isIpValid = false;
            }
        };
        m_udpPortTextbox.TextChanged += (sender, args) => {
            string currentOscPort = m_udpPortTextbox.Text.Length == 0 ? m_udpPortTextbox.PlaceholderText : m_udpPortTextbox.Text;
            if (int.TryParse(currentOscPort.AsSpan(), out int result) ) {
                s_oscConfig.oscSendPort = result;
            }
        };
        m_tcpPortTextbox.TextChanged += (sender, args) => {
            string currentTcpPort = m_tcpPortTextbox.Text.Length == 0 ? m_tcpPortTextbox.PlaceholderText : m_tcpPortTextbox.Text;
            if (int.TryParse(currentTcpPort.AsSpan(), out int result) ) {
                s_oscConfig.tcpPort = result;
            }
        };

        // UI Layout
        Grid.SetColumn(m_ipAddressLabel, 0);
        Grid.SetRow(m_ipAddressLabel, 0);
        Grid.SetColumn(m_ipTextbox, 1);
        Grid.SetRow(m_ipTextbox, 0);

        Grid.SetColumn(m_udpPortLabel, 0);
        Grid.SetRow(m_udpPortLabel, 1);
        Grid.SetColumn(m_udpPortTextbox, 1);
        Grid.SetRow(m_udpPortTextbox, 1);

        Grid.SetColumn(m_tcpPortLabel, 0);
        Grid.SetRow(m_tcpPortLabel, 2);
        Grid.SetColumn(m_tcpPortTextbox, 1);
        Grid.SetRow(m_tcpPortTextbox, 2);

        // Creates UI
        m_interfaceRoot = new Page {
            Content = new Grid {
                Children = {
                    m_ipAddressLabel, m_ipTextbox,
                    m_udpPortLabel, m_udpPortTextbox,
                    m_tcpPortLabel, m_tcpPortTextbox
                },
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(5, GridUnitType.Star) }
                },
                RowDefinitions =
                {
                    new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                    new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                    new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
                }
            }
        };

        m_pluginLoaded = true;
    }

    private void HandleOscPacketEvent(OscPacket packet) {
        var message = (OscMessage) packet;
        Host?.Log($"Received message at {message.Address} with value {message.Arguments}!");

        // @TODO: Handle specific messages?
    }

    public bool? RequestServiceRestart(string reason, bool wantReply = false) {
        Host?.Log($"Requested restart; {( wantReply ? "Expecting reply" : "" )} with reason \"{reason}\"!");
        return wantReply ? false : null;
    }

    public void Shutdown() {

        Host?.Log("Shutting down...");
        if ( s_oscQueryService != null ) {
            s_oscQueryService.Dispose();
            s_oscQueryService = null;
        }

        if ( s_oscClient != null ) {
            s_oscClient.Close();
            s_oscClient = null;
        }
    }

    public async Task<IEnumerable<(TrackerBase Tracker, bool Success)>> SetTrackerStates(IEnumerable<TrackerBase> trackerBases, bool wantReply = true) {

        if ( s_oscClient == null ) {
            Host?.Log("OSC Client is null!", LogSeverity.Warning);
            return wantReply ? trackerBases.Select(x => (x, false)) : null;
        }

        try {
            foreach ( var tracker in trackerBases ) {
                int trackerId = TrackerRoleToOscId(tracker.Role);

                if ( trackerId > 0 ) {
                    Vector3 eulerAngles = NumericExtensions.ToEulerAngles(tracker.Orientation);
                    Vector3 position = tracker.Position;

                    s_oscClient.Send(new OscMessage(string.Format(OSC_TARGET_ADDRESS_TRACKERS_POSITION, trackerId), position.X, position.Y, position.Z));
                    s_oscClient.Send(new OscMessage(string.Format(OSC_TARGET_ADDRESS_TRACKERS_ROTATION, trackerId), eulerAngles.X, eulerAngles.Y, eulerAngles.Z));
                }
            }
        } catch ( Exception ex ) {
            Host?.Log($"Unhandled Exception: {ex.GetType().Name} in {ex.Source}: {ex.Message}\n{ex.StackTrace}", LogSeverity.Fatal);
            return trackerBases.Select(x => (x, false));
        }
        return trackerBases.Select(x => (x, true));
    }

    public async Task<IEnumerable<(TrackerBase Tracker, bool Success)>> UpdateTrackerPoses(IEnumerable<TrackerBase> trackerBases, bool wantReply = true) {

        if ( s_oscClient == null ) {
            Host?.Log("OSC Client is null!", LogSeverity.Warning);
            return wantReply ? trackerBases.Select(x => (x, false)) : null;
        }

        try {
            foreach ( var tracker in trackerBases ) {
                int trackerId = TrackerRoleToOscId(tracker.Role);

                if ( trackerId > 0 ) {
                    Vector3 eulerAngles = NumericExtensions.ToEulerAngles(tracker.Orientation);
                    Vector3 position = tracker.Position;

                    s_oscClient.Send(new OscMessage(string.Format(OSC_TARGET_ADDRESS_TRACKERS_POSITION, trackerId), position.X, position.Y, position.Z));
                    s_oscClient.Send(new OscMessage(string.Format(OSC_TARGET_ADDRESS_TRACKERS_ROTATION, trackerId), eulerAngles.X, eulerAngles.Y, eulerAngles.Z));
                }
            }
        } catch ( Exception ex ) {
            Host?.Log($"Unhandled Exception: {ex.GetType().Name} in {ex.Source}: {ex.Message}\n{ex.StackTrace}", LogSeverity.Fatal);
            return trackerBases.Select(x => (x, false));
        }
        return trackerBases.Select(x => (x, true));
    }

    public TrackerBase GetTrackerPose(string contains, bool canBeFromAmethyst = true) {
        // Get pos & rot
        return null;
    }

    public async Task<(int Status, string StatusMessage, long PingTime)> TestConnection() {

        Host?.Log("TestConnection!");
        // @TODO: Test connection somehow
        return (0, "OK", 0L);
    }

    private static int TrackerRoleToOscId(TrackerType role) {
        switch ( role ) {
            case TrackerType.TrackerWaist:
                return 1;
            case TrackerType.TrackerLeftFoot:
                return 2;
            case TrackerType.TrackerRightFoot:
                return 3;
            case TrackerType.TrackerLeftKnee:
                return 4;
            case TrackerType.TrackerRightKnee:
                return 5;
            case TrackerType.TrackerLeftElbow:
                return 6;
            case TrackerType.TrackerRightElbow:
                return 7;
            case TrackerType.TrackerChest:
                return 8;
            default:
                return -1;
        }
    }

    private void LoadSettings() {
        if ( Host?.PluginSettings.GetSetting<string>("ipAddress") != null && ValidateIp(Host?.PluginSettings.GetSetting<string>("ipAddress").ToString()) ) {
            s_oscConfig.targetIpAddress = ( string ) Host?.PluginSettings.GetSetting("ipAddress", "localhost");
        }
        if ( Host?.PluginSettings.GetSetting<int>("oscPort") != null && int.TryParse(Host?.PluginSettings.GetSetting<int>("oscPort").ToString(), out int resultOsc) ) {
            s_oscConfig.oscSendPort = resultOsc;
        }
        if ( Host?.PluginSettings.GetSetting<int>("tcpPort") != null && int.TryParse(Host?.PluginSettings.GetSetting<int>("tcpPort").ToString(), out int resultTcp) ) {
            s_oscConfig.tcpPort = resultTcp;
        }
    }

    private void SaveSettings() {
        Host?.PluginSettings.SetSetting("ipAddress", s_oscConfig.targetIpAddress.ToString());
        Host?.PluginSettings.SetSetting("oscPort", s_oscConfig.oscSendPort);
        Host?.PluginSettings.SetSetting("tcpPort", s_oscConfig.tcpPort);
    }

    private static bool ValidateIp(string ip) {
        string lowerIp = ip.ToLowerInvariant();
        if ( lowerIp == "localhost" )
            return true;
        if (IPAddress.TryParse(ip.AsSpan(), out _))
            return true;
        return false;
    }
}