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
using TrackerType = Amethyst.Plugins.Contract.TrackerType;
using VRC.OSCQuery;
using OSCExtensions = VRC.OSCQuery.Extensions;

namespace plugin_OSC;

public static class ServiceData {
    public const string Name = "OSC";
    public const string Guid = "K2VRTEAM-AME2-APII-SNDP-SENDPTVRCOSC";
}

public struct OSCConfig {
    public string targetIpAddress;
    public int udpPort, tcpPort;

    public OSCConfig(string targetIpAddress = "127.0.0.1", int udpPort = -1, int tcpPort = -1) {
        this.targetIpAddress = targetIpAddress;
        this.udpPort = udpPort == -1 ? OSCExtensions.GetAvailableUdpPort() : udpPort;
        this.tcpPort = tcpPort == -1 ? OSCExtensions.GetAvailableTcpPort() : tcpPort;
    }
}

[Export(typeof(IServiceEndpoint))]
[ExportMetadata("Name", ServiceData.Name)]
[ExportMetadata("Guid", ServiceData.Guid)]
[ExportMetadata("Publisher", "K2VR Team")]
[ExportMetadata("Website", "https://github.com/KinectToVR/plugin_OSC")]
public class OSC : IServiceEndpoint {
    private const string AMETHYST_OSC_SERVICE_NAME = "AMETHYST-OSC";
    private static OSCQueryService s_oscQueryService;
    private static OSCConfig s_oscConfig;
    private bool m_initialized { get; set; }
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
    private Button m_reconnectButton { get ; set; }
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
            // TrackerType.TrackerLeftFoot,     // Already OK
            // TrackerType.TrackerRightFoot,    // Already OK
            TrackerType.TrackerLeftShoulder,
            TrackerType.TrackerRightShoulder,
            TrackerType.TrackerLeftElbow,
            TrackerType.TrackerRightElbow,
            TrackerType.TrackerLeftKnee,
            TrackerType.TrackerRightKnee,
            // TrackerType.TrackerWaist,        // Already OK
            // TrackerType.TrackerChest,        // OSC models a humanoid
            // TrackerType.TrackerCamera,       // OSC models a humanoid
            // TrackerType.TrackerKeyboard      // OSC models a humanoid
        };

    public bool AutoStartAmethyst { get => false; set { } }
    public bool AutoCloseAmethyst { get => false; set { } }

    public bool IsRestartOnChangesNeeded => false;
    public InputActions ControllerInputActions { get => null; set { } }
    public bool IsAmethystVisible => false;
    public string TrackingSystemName => "OSC";

    public (Vector3 Position, Quaternion Orientation)? HeadsetPose => null;

    public void DisplayToast((string Title, string Text) message) {
        // @TODO: Hope VRChat lets us do this
        Host?.Log("[OSC] DisplayToast!");
    }

    public void Heartbeat() {
        // @TODO: Hearbeat
    }

    public int Initialize() {

        Host?.Log("[OSC] Called Initialize!");

        ServiceStatus = 0;

        Host?.Log("[OSC] Init!", LogSeverity.Info);

        if ( s_oscQueryService != null ) {
            Host?.Log("[OSC] Oopsie!!", LogSeverity.Info);
        }

        int tcpPort;
        if (!int.TryParse(m_tcpPortTextbox.Text.AsSpan(), out tcpPort)) {
            tcpPort = OSCExtensions.GetAvailableTcpPort();
        }
        int udpPort;
        if ( !int.TryParse(m_udpPortTextbox.Text.AsSpan(), out udpPort) ) {
            udpPort = OSCExtensions.GetAvailableUdpPort();
        }

        // Starts the OSC server
        s_oscQueryService = new OSCQueryServiceBuilder()
            .WithServiceName(AMETHYST_OSC_SERVICE_NAME)
            .WithLogger(Logger)
            .WithTcpPort(tcpPort)
            .WithUdpPort(udpPort)
            .WithDiscovery(new MeaModDiscovery(Logger))
            .StartHttpServer()
            .AdvertiseOSCQuery()
            .AdvertiseOSC()
            .Build();

        s_oscQueryService.RefreshServices();

        Host?.Log($"{s_oscQueryService.ServerName} running at TCP: {s_oscQueryService.TcpPort} OSC: {s_oscQueryService.OscPort}");

        // @TODO: Init
        return 0;
    }

    public void OnLoad() {

        Host?.Log("[OSC] Called OnLoad!");

        m_ipTextbox = new TextBox() {
            PlaceholderText = "localhost",
        };
        m_udpPortTextbox = new TextBox() {
            PlaceholderText = OSCExtensions.GetAvailableUdpPort().ToString(),
        };
        m_tcpPortTextbox = new TextBox() {
            PlaceholderText = OSCExtensions.GetAvailableTcpPort().ToString(),
        };
        m_reconnectButton = new Button() {
            Content = Host?.RequestLocalizedString("/Settings/Buttons/Reconnect", ServiceData.Guid)
        };
        m_ipAddressLabel = new TextBlock() {
            Text = Host?.RequestLocalizedString("/Settings/Labels/IPAddress", ServiceData.Guid)
        };
        m_udpPortLabel = new TextBlock() {
            Text = Host?.RequestLocalizedString("/Settings/Labels/UDPPort", ServiceData.Guid)
        };
        m_tcpPortLabel = new TextBlock() {
            Text = Host?.RequestLocalizedString("/Settings/Labels/TCPPort", ServiceData.Guid)
        };

        // Creates UI
        m_interfaceRoot = new Page {
            Content = new StackPanel {
                Children = {
                    new StackPanel {
                        Children = { m_ipAddressLabel, m_ipTextbox },
                        Orientation = Orientation.Horizontal
                    },
                    new StackPanel {
                        Children = { m_udpPortLabel, m_udpPortTextbox },
                        Orientation = Orientation.Horizontal
                    },
                    new StackPanel {
                        Children = { m_tcpPortLabel, m_tcpPortTextbox },
                        Orientation = Orientation.Horizontal
                    },
                    m_reconnectButton
                },
                Orientation = Orientation.Vertical,
            }
        };

        m_pluginLoaded = true;
    }

    public bool? RequestServiceRestart(string reason, bool wantReply = false) {
        Host?.Log($"[OSC] Requested restart; {( wantReply ? "Expecting reply" : "" )} with reason \"{reason}\"!", LogSeverity.Info);
        return wantReply ? false : null;
    }

    public void Shutdown() {

        Host?.Log("[OSC] YOU SHOULD KILL YOURSELF ⚡⚡⚡⚡!");
        // @TODO: Kill OSC Server, and free memory
        if ( s_oscQueryService != null ) {
            s_oscQueryService.Dispose();
            s_oscQueryService = null;
            GC.Collect(0, GCCollectionMode.Aggressive);
        }
    }

    public async Task<IEnumerable<(TrackerBase Tracker, bool Success)>> SetTrackerStates(IEnumerable<TrackerBase> trackerBases, bool wantReply = true) {
        return trackerBases.Select(x => (x, true));
    }

    public async Task<IEnumerable<(TrackerBase Tracker, bool Success)>> UpdateTrackerPoses(IEnumerable<TrackerBase> trackerBases, bool wantReply = true) {
        return trackerBases.Select(x => (x, true));
    }

    public TrackerBase GetTrackerPose(string contains, bool canBeFromAmethyst = true) {
        // Get pos & rot
        return null;
    }

    public async Task<(int Status, string StatusMessage, long PingTime)> TestConnection() {

        Host?.Log("[OSC] TestConnection!");
        // @TODO: Test connection somehow
        return (0, "OK", 0L);
    }
};