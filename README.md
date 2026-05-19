<p align="center">
    <a href="https://paypal.me/jrodrigv"><img src="https://img.shields.io/badge/paypal-donate-yellow.svg?style=flat&logo=paypal" alt="PayPal"/></a>
    <a href="https://dev.azure.com/jrodrigv/Personal/_build/latest?definitionId=6&branchName=main"><img src="https://dev.azure.com/jrodrigv/Personal/_apis/build/status/jrodrigv.OfCourseIStillLoveYou?branchName=main" alt="Azure Devops"/></a>
     <a href="../../releases"><img src="https://img.shields.io/github/downloads/jrodrigv/OfCourseIStillLoveYou/total.svg?style=flat&logo=github&logoColor=white" alt="Total downloads" /></a>
          <a href="../../releases"><img src="https://img.shields.io/github/release/jrodrigv/OfCourseIStillLoveYou.svg?style=flat&logo=github&logoColor=white" alt="Latest release" /></a>
</p>

# OfCourseIStillLoveYou

KSP mod to display hullcam cameras views on different GUI inside or outside the game using a Desktop app and Server app.

> **About the `kerbcam-spike` branch.** This is an experimental branch used to
> measure and validate per-frame performance improvements that would form the
> case for a from-scratch successor mod (`kerbcam`). The changes here aren't
> intended for upstream merge — they exist to record concrete measurements:
>
> - On a Steam Deck (Linux native KSP, OpenGL/Mesa) streaming 5 hullcams,
>   replacing the synchronous `Texture2D.ReadPixels + EncodeToJPG` with an
>   asynchronous GPU readback path (vendored from
>   [yangrc1234/UnityOpenGLAsyncReadback](https://github.com/yangrc1234/UnityOpenGLAsyncReadback),
>   MIT-licensed, see `OfCourseIStillLoveYou/Vendor/UnityOpenGLAsyncReadback/LICENSE`)
>   recovers **~+37% mean / +88% p50 in-game framerate** in the canonical
>   mods-streaming test scene.
> - The sync portion of the readback (issuing the request) drops from 13-16 ms
>   to ~0.13 ms per camera — a 100×+ reduction. The remaining ~8 ms per camera
>   is the JPEG encode + texture upload, which is the next lever.
> - Per-frame timing instrumentation is gated behind `/p:KerbCamBaseline=true`;
>   default builds are byte-identical to unmodified releases.
> - Also includes a Hullcam VDS filter integration: per-camera `cameraMode` is
>   honoured at capture time by Blitting through the matching `CameraFilter*`
>   material before readback. Black-and-white, night-vision, CRT-scanline, etc.
>   look authentic in the streamed feed instead of generic colour.
> - A small KSP-specific gotcha worth noting: the yangrc plugin's
>   `AsyncReadbackUpdater` is supposed to auto-spawn via
>   `[RuntimeInitializeOnLoadMethod(BeforeSceneLoad)]`, but KSP loads mod DLLs
>   AFTER that hook fires, so it never runs. The patch in `TrackingCamera.cs`
>   spawns it manually on first use — without this every readback request stays
>   forever-pending.
>
> If you're an OCISLY user reading this and wondering about the changes: this
> branch is purely an experiment, not a release candidate. Stick to `main`.

## Requirements:
* KSP 1.12.5
* NET 7 runtime https://dotnet.microsoft.com/en-us/download/dotnet/7.0
* Latest HullcamVDS https://github.com/linuxgurugamer/HullcamVDSContinued/releases

## Highly recommended mods:
* Physics Range Extender
* Scatterer 0.0838 or newer https://github.com/LGhassen/Scatterer/releases
* If you want to use TUFX you need to use this version -> TUFX JR edition https://github.com/jrodrigv/TUFX/releases 

## Mod Installation:
* Download the zip file for Windows, Linux or Mac.
* Copy the GameData folder into your KSP root folder

## Mod Configuration:
Inside the settings.cfg file you can modify the Cameras resolution and server connection

```Settings
{
  EndPoint = localhost
  Port = 5077
  Width = 768
  Height = 768
}
```
## Desktop & Server app setup:
* Unzip the OfCourseIStillLoveYou.Server.zip and OfCourseIStillLoveYou.DesktopClient.zip
* By default the mod, the server and the desktop client will connect to localhost:5077 but you can modify it:
  * Server: *OfCourseIStillLoveYou.Server.exe --endpoint 192.168.1.8  --port 5001* .
  * DesktopClient: Open the settings.json inside OfCourseIStillLoveYou.DesktopClient and modify the endpoint and port.
  * Mod: Inside the mod folder there is a settings.cfg file with the endpoint and port.
* Execute the OfCourseIStillLoveYou.Server.exe first, then OfCourseIStillLoveYou.DesktopClient.exe and finally start KSP

## Running the server as a Docker Container
* Pull the image *docker pull jrodrigv/ofcourseistillloveyou:latest*
* Create a new container - example overriding endpoint to listen everything and from port 5000: *docker run -d -p 192.168.0.14:5000:5000 ofcourseistillloveyou:server_v1.0 --port 5000 --endpoint 0.0.0.0*

## Desktop Client usage
* To hide all the UI controls & telemetry, double click on the camera image view
* To move the window, click and drag from the camera image view
* To resise the window, click and drag from the resize texture corner (bottom - right corner)
* To close the UI, press the "X" button

## Mod usage

Take a look to this video tutorial :)

https://youtu.be/OV0Z4xpFYlA
