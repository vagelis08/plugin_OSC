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
using ABI.System.Numerics;

namespace plugin_OSC;

public static class ServiceData {
    public const string Name = "OSC";
    public const string Guid = "K2VRTEAM-AME2-APII-SNDP-SENDPTVRCOSC";
}


[Export(typeof(IServiceEndpoint))]
[ExportMetadata("Name", ServiceData.Name)]
[ExportMetadata("Guid", ServiceData.Guid)]
[ExportMetadata("Publisher", "K2VR Team")]
[ExportMetadata("Website", "https://github.com/KinectToVR/plugin_OSC")]
public class OSC : IServiceEndpoint {
    private bool PluginLoaded { get; set; }
    private Page InterfaceRoot { get; set; }

    public bool IsSettingsDaemonSupported => true;

    public object SettingsInterfaceRoot => InterfaceRoot;

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

    public (System.Numerics.Vector3 Position, System.Numerics.Quaternion Orientation)? HeadsetPose => ( System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity );

    public void DisplayToast((string Title, string Text) message) {
        // @TODO: Hope VRChat lets us do this
    }

    public void Heartbeat() {
        // @TODO: Hearbeat
    }

    public int Initialize() {
        // @TODO: Init
        return 0;
    }

    public void OnLoad() {

        // Creates UI
        InterfaceRoot = new Page {
            Content = new Grid {
                // Children = { ReManifestButton, ReRegisterButton },
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
                }
            }
        };

        PluginLoaded = true;
    }

    public bool? RequestServiceRestart(string reason, bool wantReply = false) {
        return wantReply ? false : null;
    }

    public void Shutdown() {
        // @TODO: Kill OSC Server, and free memory
    }

    public Task<IEnumerable<(TrackerBase Tracker, bool Success)>> SetTrackerStates(IEnumerable<TrackerBase> trackerBases, bool wantReply = true) {
        return null;
    }

    public Task<IEnumerable<(TrackerBase Tracker, bool Success)>> UpdateTrackerPoses(IEnumerable<TrackerBase> trackerBases, bool wantReply = true) {
        return null;
    }

    public TrackerBase GetTrackerPose(string contains, bool canBeFromAmethyst = true) {
        // Get pos & rot
        return null;
    }

    public async Task<(int Status, string StatusMessage, long PingTime)> TestConnection() {
        // @TODO: Test connection somehow
        return (0, "OK", 0L);
    }
}