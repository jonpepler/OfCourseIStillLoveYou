# Vendored: UnityOpenGLAsyncReadback

Vendored copy of `Yangrc.OpenGLAsyncReadback` from
https://github.com/yangrc1234/UnityOpenGLAsyncReadback (MIT licensed,
copyright 2018 Aurélien Labate and 2019 yangrc — see `LICENSE`).

This plugin provides true asynchronous GPU→CPU readback on Unity's OpenGL
backend, where Unity's own `AsyncGPUReadback` API is a no-op
(`SystemInfo.supportsAsyncGPUReadback == false`). The public C# entry point
is `Yangrc.OpenGLAsyncReadback.UniversalAsyncGPUReadbackRequest.Request(...)`,
which dispatches at runtime to:

- Unity's native `AsyncGPUReadback` when supported (D3D11 / Vulkan / Metal),
- The bundled `AsyncGPUReadbackPlugin` (`libAsyncGPUReadbackPlugin.so` on Linux,
  `AsyncGPUReadbackPlugin.dll` on Windows) otherwise.

## Files

- `AsyncGPUReadbackPlugin.cs` — C# wrapper + DllImports.
- `AsyncReadbackUpdater.cs` — `MonoBehaviour` that pumps pending readbacks
  every frame. Auto-instantiated via `[RuntimeInitializeOnLoadMethod]` on
  `GraphicsDeviceType.OpenGLCore`.
- `LICENSE` — upstream MIT license, preserved verbatim.

## Native plugin binary

The matching native plugin lives in the mod's `GameData/OfCourseIStillLoveYou/Plugins/x86_64/libAsyncGPUReadbackPlugin.so`
(Linux). It's the prebuilt blob from the upstream's `UnityExampleProject/Assets/OpenglAsyncReadback/Plugins/Linux/`
folder.

## Updating

To pull an upstream update:

```sh
cd /tmp && git clone --depth 1 https://github.com/yangrc1234/UnityOpenGLAsyncReadback.git yangrc-update
cp /tmp/yangrc-update/UnityExampleProject/Assets/OpenglAsyncReadback/Scripts/{AsyncGPUReadbackPlugin,AsyncReadbackUpdater}.cs <repo>/Vendor/UnityOpenGLAsyncReadback/
cp /tmp/yangrc-update/LICENSE <repo>/Vendor/UnityOpenGLAsyncReadback/
cp /tmp/yangrc-update/UnityExampleProject/Assets/OpenglAsyncReadback/Plugins/Linux/libAsyncGPUReadbackPlugin.so <kspdata>/GameData/OfCourseIStillLoveYou/Plugins/x86_64/
```

## Why vendor instead of subtree

Today this lives in the OCISLY fork because that's where the active patch
is. Once the kerbcam project hosts the rebuild plugin proper, this should
move to `kerbcam/vendor/UnityOpenGLAsyncReadback/` as a git subtree with
proper history, and CI builds the `.so` on every push (the upstream's
`NativePlugin/CMakeLists.txt` is what produces the binary).
