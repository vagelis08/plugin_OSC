<h1 dir=auto>
<b>OSC (OscQuery)</b>
<a style="color:#9966cc;" href="https://github.com/KinectToVR/Amethyst">Amethyst</a>
<text>service plugin</text>
</h1>

## **License**
This project is licensed under the GNU GPL v3 License 

## **Overview**
This is just a fork of the actual OSC plugin, modified to work with Dance Dash.

## **Downloads**
You're going to find built plugins in [repo Releases](https://github.com/KinectToVR/plugin_OSC/releases/latest).

## **Build & Deploy**
Both build and deployment instructions [are available here](https://github.com/KinectToVR/plugin_KinectV2/blob/main/.github/workflows/build.yml).
 - Initialize git submodules: `git submodule update --init --recursive`
 - Open in Visual Studio and publish using the prepared publish profile  
   (`plugin_OSC` → `Publish` → `Publish` → `Open folder`)
 - Copy the published plugin to the `plugins` folder of your local Amethyst installation  
   or register by adding it to `$env:AppData\Amethyst\amethystpaths.k2path`
   ```jsonc
   {
    "external_plugins": [
        // Add the published plugin path here, this is an example:
        "F:\\source\\repos\\plugin_OSC\\plugin_OSC\\bin\\Release\\Publish"
    ]
   }
   ```

## **Wanna make one too? (K2API Devices Docs)**
[This repository](https://github.com/KinectToVR/Amethyst.Plugins.Templates) contains templates for plugin types supported by Amethyst.<br>
Install the templates by `dotnet new install Amethyst.Plugins.Templates::1.2.0`  
and use them in Visual Studio (recommended) or straight from the DotNet CLI.  
The project templates already contain most of the needed documentation,  
although please feel free to check out [the official wesite](https://docs.k2vr.tech/) for more docs sometime.

The build and publishment workflow is the same as in this repo (excluding vendor deps).  
