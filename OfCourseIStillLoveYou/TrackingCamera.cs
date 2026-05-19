using System;
using System.Linq;
using System.Threading.Tasks;
using HullcamVDS;
using OfCourseIStillLoveYou.Client;
using OfCourseIStillLoveYou.TUFX;
using UnityEngine;
using UnityEngine.Rendering;
// Vendored wrapper around Unity-native AsyncGPUReadback + yangrc1234's
// OpenGL plugin. Picks the right backend at runtime via
// SystemInfo.supportsAsyncGPUReadback. Critical for Steam Deck native KSP
// which runs on OpenGLCore where Unity's AsyncGPUReadback is unsupported.
using Yangrc.OpenGLAsyncReadback;

namespace OfCourseIStillLoveYou
{
    public class TrackingCamera
    {
        private const float ButtonHeight = 18;
        private const float Gap = 2;
        private const float Line = ButtonHeight + Gap;
        private const float ButtonWidth = 3 * ButtonHeight + 4 * Gap;
        private const float MaxCameraSize = 360;
        private const string Altitude = "ALTITUDE: ", Km = " KM", Speed = "SPEED: ", Kmh = " KM/H";

        private static readonly float controlsStartY = 22;
        private static readonly Font TelemetryFont = Font.CreateDynamicFontFromOSFont("Bahnschrift Semibold", 17);

        private static readonly GUIStyle ButtonStyle = new GUIStyle(HighLogic.Skin.button)
            {fontSize = 10, wordWrap = true};


        private static readonly GUIStyle TelemetryGuiStyle = new GUIStyle()
            {alignment = TextAnchor.MiddleCenter, normal = new GUIStyleState() {textColor = Color.white}, fontStyle = FontStyle.Bold, font = TelemetryFont };


        public static Texture2D ResizeTexture =
            GameDatabase.Instance.GetTexture("OfCourseIStillLoveYou/Textures/" + "resizeSquare", false);

        private readonly MuMechModuleHullCamera _hullcamera;


        private float _initialCamImageWidthSize = 360;
        private float _initialCamImageHeightSize = 360;
        private float _adjCamImageWidthSize = 360;
        private float _adjCamImageHeightSize = 360;

        private readonly Camera[] _cameras = new Camera[3];
        private float _windowHeight;

        private Rect _windowRect;
        private float _windowWidth;
        public RenderTexture TargetCamRenderTexture;
        // Scratch RT used as the destination of the Hullcam VDS filter Blit.
        // Allocated once in the constructor with the same dims/format/depth as
        // TargetCamRenderTexture; the readback runs against this when a
        // non-Normal filter is active. Released in Disable() to avoid leaking
        // GPU memory across vessel changes.
        private RenderTexture _filteredRenderTexture;

        // Always Blit through this depth=0 RT before issuing the readback so
        // the OpenGL plugin gets a clean GL_TEXTURE_2D handle. See constructor
        // for the full rationale.
        private RenderTexture _readbackRenderTexture;
        // RGBA32 (not ARGB32). RGBA32 maps to GraphicsFormat.R8G8B8A8_UNorm,
        // which is in DirectX 11's list of formats that support ReadPixels
        // usage (and thus AsyncGPUReadback). ARGB32 maps to
        // GraphicsFormat.B8G8R8A8_SRGB which is NOT supported — using it as
        // the dstFormat for AsyncGPUReadback on Steam Deck (Proton/DXVK)
        // returned hasError. RGBA32 also matches the source RenderTexture
        // (RenderTextureFormat.ARGB32 → R8G8B8A8_UNorm), so LoadRawTextureData
        // round-trips bytes without byte-order swapping.
        private readonly Texture2D _texture2D = new Texture2D(Settings.Width, Settings.Height, TextureFormat.RGBA32, false);

        // Hullcam VDS filter cache. Looked up once on first use from
        // _hullcamera.cameraMode and reused. _filterMode tracks the mode we
        // built the filter for so we can re-create it if the player flips the
        // mode in the part action menu mid-flight. _filterDisabled is a sticky
        // give-up flag: any throw from CreateFilter / Activate / RenderImage
        // sets it so we fall back to the unfiltered readback path forever.
        private CameraFilter _filter;
        private int _filterMode = -1;
        private bool _filterDisabled;
        private bool _filterFallbackLogged;

        public bool OddFrames;
        private byte[] _jpgTexture;

        // Async readback in-flight guard. The OddFrames-driven Refresh loop
        // ticks SendCameraImage every other frame; if the GPU hasn't finished
        // copying the previous frame yet (typically 2-3 frames of latency on
        // discrete GPUs, less on the Deck's APU), skip this tick rather than
        // queueing another request. Keeps memory + Native dispatcher pressure
        // bounded and preserves the "one in-flight per camera" invariant.
        private bool _readbackInFlight;
        private int _consecutiveReadbackErrors;

        public void ToogleCameras()
        {
            OddFrames = !OddFrames;
            foreach (var camera in this._cameras)
            {
                camera.enabled = OddFrames;
            }
        }

        private bool _firstFrameLogged;
        private int _consecutiveEmptyJpegs;
        private int _consecutiveExceptions;
        private int _consecutiveSendExceptions;

#if KERBCAM_BASELINE
        private static bool _kerbcamDiagnosticsLogged;
        private void LogKerbcamDiagnosticsOnce(RenderTexture src)
        {
            if (_kerbcamDiagnosticsLogged) return;
            _kerbcamDiagnosticsLogged = true;
            try
            {
                Debug.Log($"[KerbCamBaseline] graphicsDeviceType={SystemInfo.graphicsDeviceType}");
                Debug.Log($"[KerbCamBaseline] graphicsDeviceVersion={SystemInfo.graphicsDeviceVersion}");
                Debug.Log($"[KerbCamBaseline] supportsAsyncGPUReadback={SystemInfo.supportsAsyncGPUReadback}");
                Debug.Log($"[KerbCamBaseline] src.graphicsFormat={src.graphicsFormat} format={src.format} depth={src.depth}");
                Debug.Log($"[KerbCamBaseline] IsFormatSupported R8G8B8A8_UNorm.ReadPixels={UnityEngine.SystemInfo.IsFormatSupported(UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm, UnityEngine.Experimental.Rendering.FormatUsage.ReadPixels)}");
                Debug.Log($"[KerbCamBaseline] IsFormatSupported R8G8B8A8_SRGB.ReadPixels={UnityEngine.SystemInfo.IsFormatSupported(UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB, UnityEngine.Experimental.Rendering.FormatUsage.ReadPixels)}");
                Debug.Log($"[KerbCamBaseline] IsFormatSupported B8G8R8A8_UNorm.ReadPixels={UnityEngine.SystemInfo.IsFormatSupported(UnityEngine.Experimental.Rendering.GraphicsFormat.B8G8R8A8_UNorm, UnityEngine.Experimental.Rendering.FormatUsage.ReadPixels)}");
                Debug.Log($"[KerbCamBaseline] IsFormatSupported B8G8R8A8_SRGB.ReadPixels={UnityEngine.SystemInfo.IsFormatSupported(UnityEngine.Experimental.Rendering.GraphicsFormat.B8G8R8A8_SRGB, UnityEngine.Experimental.Rendering.FormatUsage.ReadPixels)}");
            }
            catch (Exception ex)
            {
                Debug.Log($"[KerbCamBaseline] diagnostic failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
#endif

        // KSP loads mod DLLs after Unity's BeforeSceneLoad hook fires, so the
        // yangrc plugin's `[RuntimeInitializeOnLoadMethod]` never runs and the
        // AsyncReadbackUpdater MonoBehaviour that pumps pending requests never
        // gets created. Without the updater, requests issued via
        // OpenGLAsyncReadbackRequest stay forever-pending — `.done` never goes
        // true and ProcessReadbackComplete never fires. Manually spawn the
        // updater on first use to bridge that gap.
        private static bool _updaterSpawnAttempted;
        private static void EnsureOpenGLAsyncReadbackUpdater()
        {
            if (_updaterSpawnAttempted) return;
            _updaterSpawnAttempted = true;
            if (AsyncReadbackUpdater.instance != null) return;
            if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.OpenGLCore) return;
            try
            {
                var go = new GameObject("__OpenGL Async Readback Updater__");
                go.hideFlags = HideFlags.HideAndDontSave;
                GameObject.DontDestroyOnLoad(go);
                go.AddComponent<AsyncReadbackUpdater>();
                Debug.Log("[OCISLY] spawned AsyncReadbackUpdater (KSP doesn't fire RuntimeInitializeOnLoadMethod for late-loaded mod DLLs)");
            }
            catch (Exception ex)
            {
                Debug.Log($"[OCISLY] failed to spawn AsyncReadbackUpdater: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public void SendCameraImage()
        {
            if (!OddFrames) return;
            if (!StreamingEnabled) return;

            EnsureOpenGLAsyncReadbackUpdater();

            // Poll: if a request from a previous tick is now done, drain it
            // through the JPEG/send pipeline before issuing a new one. The
            // wrapper is poll-based (not callback-based) — Unity-native
            // AsyncGPUReadback is wrapped, the OpenGL plugin path is wrapped,
            // both expose the same .done/.hasError/.GetData<T>() shape.
            //
            // If still pending, return — caller (Refresh in Core.cs) will
            // re-tick on its OddFrames cadence and we'll poll again.
            if (_readbackInFlight)
            {
                if (!_pendingRequest.done) return;
                ProcessReadbackComplete(_pendingRequest);
                // ProcessReadbackComplete's finally clears _readbackInFlight
                // so the next branch can issue a fresh request this tick.
            }

            try
            {
#if KERBCAM_BASELINE
                // Stopwatch #1 (headline): main-thread cost of the sync
                // portion of issuing the readback. With AsyncGPUReadback this
                // is just the Request() dispatch — sub-millisecond. The Hullcam
                // VDS filter Blit dispatch is inside this window by design.
                // Shader execution runs on the GPU outside this window.
                var __kerbcamSyncStopwatch = System.Diagnostics.Stopwatch.StartNew();
#endif
                // Run the Hullcam VDS filter (if any) before issuing the
                // readback so the streamed pixels carry the camera's
                // configured visual character (NV green, CRT scanlines, etc.).
                var filtered = ApplyHullcamFilter();

                // Blit through the depth-less _readbackRenderTexture so the
                // plugin gets a clean GL_TEXTURE_2D handle. Without this step
                // the readback plugin's glGetTexLevelParameteriv on a depth-
                // bundled RT comes back with 0 dimensions on Mesa/Unity and
                // the request silently does nothing (spamming
                // "OpenGL Error: Invalid texture unit!" in the meantime).
                Graphics.Blit(filtered, _readbackRenderTexture);

#if KERBCAM_BASELINE
                LogKerbcamDiagnosticsOnce(_readbackRenderTexture);
#endif

                _readbackInFlight = true;
                // UniversalAsyncGPUReadbackRequest picks the backend at
                // runtime: Unity-native AsyncGPUReadback if supported
                // (Direct3D11 / Vulkan / Metal), yangrc1234's OpenGL plugin
                // otherwise. Steam Deck native KSP hits the OpenGL path.
                _pendingRequest = UniversalAsyncGPUReadbackRequest.Request(_readbackRenderTexture, 0);

#if KERBCAM_BASELINE
                __kerbcamSyncStopwatch.Stop();
                _pendingSyncMs = __kerbcamSyncStopwatch.Elapsed.TotalMilliseconds;
#endif
            }
            catch (Exception ex)
            {
                // Request failed to enqueue — clear the guard so the next
                // tick can try again.
                _readbackInFlight = false;
                if (_consecutiveExceptions == 0 || _consecutiveExceptions % 300 == 0)
                {
                    Debug.Log($"[OCISLY] cam={Id} capture pipeline threw: {ex.GetType().Name}: {ex.Message}");
                }
                _consecutiveExceptions++;
            }
        }

        // Honour Hullcam VDS's per-camera cameraMode by Blitting through the
        // matching CameraFilter shader before the AsyncGPUReadback fires.
        //
        // Returns the RenderTexture the readback should consume:
        //   - TargetCamRenderTexture when mode=0 (Normal — no-op filter, skip
        //     the wasted copy), Hullcam VDS is missing, or the filter has
        //     thrown previously (sticky _filterDisabled fallback).
        //   - _filteredRenderTexture otherwise — the Blit destination.
        //
        // Filter instances are cached per-camera. We rebuild only if the
        // player changes mode in the part action menu mid-flight (cheap
        // equality check on _filterMode).
        private RenderTexture ApplyHullcamFilter()
        {
            if (_filterDisabled || _hullcamera == null)
                return TargetCamRenderTexture;

            int desiredMode;
            try
            {
                // Hullcam VDS's own changeCameraMode() uses == 0, == 1, … on
                // the float field — implicit truncation. Match that.
                desiredMode = (int)_hullcamera.cameraMode;
            }
            catch (Exception)
            {
                // Defensive: if reading the field somehow throws (unlikely),
                // give up cleanly rather than spamming exceptions.
                _filterDisabled = true;
                LogFilterFallback("unable to read cameraMode field");
                return TargetCamRenderTexture;
            }

            if (desiredMode < 0 || desiredMode > 8)
                return TargetCamRenderTexture;

            // Mode 0 (Normal) is a pass-through Blit — wasted GPU copy.
            // Read back from the original RT directly.
            if (desiredMode == 0)
                return TargetCamRenderTexture;

            try
            {
                if (_filter == null || _filterMode != desiredMode)
                {
                    // Idempotent + cheap (null-guarded). MovieTime.Awake() in
                    // Hullcam VDS likely beat us to it, but call defensively
                    // so we don't crash if a future Hullcam version reorders
                    // initialization. Never call ReleaseAssets() — the
                    // materials/textures are shared static state with
                    // MovieTime's in-game GUI.
                    CameraFilter.InitializeAssets();

                    var built = CameraFilter.CreateFilter((CameraFilter.eCameraMode)desiredMode);
                    if (built == null)
                    {
                        // Unknown mode — Hullcam VDS returns null from the
                        // factory in that case. Fall back to unfiltered for
                        // this frame; don't sticky-disable, so a subsequent
                        // valid mode flip still works.
                        return TargetCamRenderTexture;
                    }
                    // LoResTV/HiResTV need Activate() to seed the vHoldRoller
                    // roll speed/frequency; NightVision uses it to snapshot
                    // ambient light. Skip and the iconic vertical-hold tear
                    // never rolls and NV ambience is wrong.
                    built.Activate();

                    // Mode-swap path: give the outgoing filter a chance to
                    // undo any side effects. NightVision in particular
                    // mutates RenderSettings.ambientLight (global scene
                    // state); dropping the instance without Deactivate()
                    // leaks the mutation onto every other camera until this
                    // tracking camera is fully disabled.
                    if (_filter != null)
                    {
                        try { _filter.Deactivate(); }
                        catch (Exception ex)
                        {
                            Debug.Log($"[OCISLY] cam={Id} filter Deactivate (mode swap) threw: {ex.GetType().Name}: {ex.Message}");
                        }
                    }
                    _filter = built;
                    _filterMode = desiredMode;
                }

                // Blit dispatch — main-thread cost is sub-microsecond; shader
                // runs on the GPU before the queued AsyncGPUReadback fires.
                // Inside the KERBCAM_BASELINE sync stopwatch by design.
                _filter.RenderImageWithFilter(TargetCamRenderTexture, _filteredRenderTexture);
                return _filteredRenderTexture;
            }
            catch (TypeLoadException)
            {
                // Hullcam VDS assembly is missing entirely (user uninstalled
                // it but kept OCISLY). Sticky-disable and fall back.
                _filterDisabled = true;
                LogFilterFallback("HullcamVDS assembly not loaded");
                return TargetCamRenderTexture;
            }
            catch (Exception ex)
            {
                // Anything else — bad shader, missing texture, asset bundle
                // load failure. Sticky-disable for this camera so we don't
                // spam the log every frame.
                _filterDisabled = true;
                LogFilterFallback($"{ex.GetType().Name}: {ex.Message}");
                return TargetCamRenderTexture;
            }
        }

        private void LogFilterFallback(string reason)
        {
            if (_filterFallbackLogged) return;
            _filterFallbackLogged = true;
            Debug.Log($"[OCISLY] cam={Id} HullcamVDS filter disabled: {reason} — falling back to unfiltered capture for this camera");
        }

#if KERBCAM_BASELINE
        // Per-camera scratch for the headline (sync) timing — carried from
        // SendCameraImage into the matched ProcessReadbackComplete invocation
        // so both timings end up in the same CSV row.
        private double _pendingSyncMs;
#endif

        // In-flight readback for this camera. The struct holds either a
        // Unity-native AsyncGPUReadbackRequest or a yangrc OpenGL request,
        // chosen at runtime by Yangrc.OpenGLAsyncReadback's dispatcher based
        // on SystemInfo.supportsAsyncGPUReadback. SendCameraImage polls
        // _pendingRequest.done before issuing a new request, and routes the
        // completed request through ProcessReadbackComplete.
        private UniversalAsyncGPUReadbackRequest _pendingRequest;

        private void ProcessReadbackComplete(UniversalAsyncGPUReadbackRequest request)
        {
#if KERBCAM_BASELINE
            // Stopwatch #2: the main-thread cost of everything the callback
            // does synchronously — LoadRawTextureData + Apply + EncodeToJPG +
            // payload construction + Task.Run scheduling. The Task body
            // itself (the actual gRPC call) runs off-thread and is not
            // included here intentionally.
            var __kerbcamCbStopwatch = System.Diagnostics.Stopwatch.StartNew();
#endif
            try
            {
                if (request.hasError)
                {
                    if (_consecutiveReadbackErrors == 0 || _consecutiveReadbackErrors % 300 == 0)
                    {
                        Debug.Log($"[OCISLY] cam={Id} AsyncGPUReadback returned hasError (skipping frame)");
                    }
                    _consecutiveReadbackErrors++;
                    return;
                }
                _consecutiveReadbackErrors = 0;

                // GetData<byte> hands back a NativeArray that is only valid
                // for the duration of this callback. Copy into _texture2D
                // synchronously and never let the NativeArray escape into
                // the Task.Run lambda below.
                var data = request.GetData<byte>();
                _texture2D.LoadRawTextureData(data);
                _texture2D.Apply();

                _jpgTexture = _texture2D.EncodeToJPG();

                if (_jpgTexture == null || _jpgTexture.Length == 0)
                {
                    if (_consecutiveEmptyJpegs == 0 || _consecutiveEmptyJpegs % 300 == 0)
                    {
                        Debug.Log($"[OCISLY] cam={Id} EncodeToJPG produced empty buffer");
                    }
                    _consecutiveEmptyJpegs++;
                    return;
                }
                _consecutiveEmptyJpegs = 0;
                _consecutiveExceptions = 0;

                if (!_firstFrameLogged)
                {
                    _firstFrameLogged = true;
                    Debug.Log($"[OCISLY] cam={Id} streaming OK ({_texture2D.width}x{_texture2D.height}, {_jpgTexture.Length} bytes)");
                }

                var payload = new CameraData
                {
                    CameraId = Id.ToString(),
                    CameraName = Name,
                    Speed = SpeedString,
#if KERBCAM_BASELINE
                    // Repurpose Altitude to carry a KSP-side capture timestamp
                    // (Time.unscaledTime * 1000, ms). The gonogo relay pairs
                    // this with its own receipt time when KERBCAM_BASELINE=1 in
                    // relay env. The real altitude string is gone during a
                    // baseline run — that's expected; see plan doc.
                    Altitude = KerbCamBaseline.UnscaledTimeMsString(),
#else
                    Altitude = AltitudeString,
#endif
                    Texture = _jpgTexture,
                };

                // After 5 consecutive send failures (e.g. relay process not
                // running, gRPC channel wedged), back off: only attempt 1 in
                // every 300 frames. Throwing an exception per frame is
                // expensive in Unity even when the catch swallows it, because
                // the runtime still captures a stack trace.
                if (_consecutiveSendExceptions < 5 || _consecutiveSendExceptions % 300 == 0)
                {
                    Task.Run(() =>
                    {
                        try
                        {
                            GrpcClient.SendCameraTextureAsync(payload);
                            _consecutiveSendExceptions = 0;
                        }
                        catch (Exception ex)
                        {
                            if (_consecutiveSendExceptions == 0 || _consecutiveSendExceptions % 300 == 0)
                            {
                                Debug.Log($"[OCISLY] cam={Id} SendCameraTextureAsync threw: {ex.GetType().Name}: {ex.Message} (backing off; will retry every ~300 frames until recovery)");
                            }
                            _consecutiveSendExceptions++;
                        }
                    });
                }
                else
                {
                    _consecutiveSendExceptions++;
                }
            }
            catch (Exception ex)
            {
                if (_consecutiveExceptions == 0 || _consecutiveExceptions % 300 == 0)
                {
                    Debug.Log($"[OCISLY] cam={Id} readback callback threw: {ex.GetType().Name}: {ex.Message}");
                }
                _consecutiveExceptions++;
            }
            finally
            {
#if KERBCAM_BASELINE
                __kerbcamCbStopwatch.Stop();
                var __kerbcamCbMs = __kerbcamCbStopwatch.Elapsed.TotalMilliseconds;
                // Name is lazy-initialized inside the GUI render path; fall back
                // to the underlying hullcam name so CSV rows aren't anonymous
                // when nobody's opened the OCISLY camera window.
                var __kerbcamName = !string.IsNullOrEmpty(Name) ? Name : (_hullcamera != null ? _hullcamera.cameraName : "?");
                KerbCamBaseline.LogCaptureFrame(
                    Id,
                    __kerbcamName,
                    _pendingSyncMs,
                    _jpgTexture?.Length ?? 0,
                    __kerbcamCbMs);
#endif
                // Clear last so a stray exception from any of the above
                // paths can't permanently wedge the camera.
                _readbackInFlight = false;
            }
        }


        public TrackingCamera(int id, MuMechModuleHullCamera hullcamera)
        {
            Id = id;
            _hullcamera = hullcamera;

            TargetCamRenderTexture = new RenderTexture(Settings.Width, Settings.Height, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1
            };

            TargetCamRenderTexture.Create();

            // Scratch destination for the Hullcam VDS filter Blit. Same shape
            // as the capture target so the readback path doesn't need to know
            // which RT it's reading. Allocated unconditionally even though
            // mode=0 cameras won't use it — we don't know the mode until the
            // first SendCameraImage tick, and the player can flip the mode at
            // runtime via the part action menu.
            _filteredRenderTexture = new RenderTexture(Settings.Width, Settings.Height, 24, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1
            };
            _filteredRenderTexture.Create();

            // Readback scratch RT. Depth=0 (no depth attachment) so the
            // underlying GL handle is a plain GL_TEXTURE_2D, not a
            // renderbuffer or depth-bundled FBO. The yangrc OpenGL plugin
            // does `glBindTexture(GL_TEXTURE_2D, name)` +
            // `glGetTexLevelParameteriv(...)` on the handle we hand it; if
            // the source RT has depth=24 the handle behaves like a
            // renderbuffer on some Mesa/Unity combos and the GL operations
            // fail with "Invalid texture unit". The cheap fix: Blit through
            // this depth-less RT immediately before issuing the readback.
            _readbackRenderTexture = new RenderTexture(Settings.Width, Settings.Height, 0, RenderTextureFormat.ARGB32)
            {
                antiAliasing = 1
            };
            _readbackRenderTexture.Create();

            CalculateInitialSize();

            _windowWidth = _adjCamImageWidthSize + 3 * ButtonHeight + 16 + 2 * Gap;
            _windowHeight = _adjCamImageHeightSize  + 23;
            _windowRect = new Rect(Screen.width - _windowWidth, Screen.height - _windowHeight, _windowWidth,
                _windowHeight);
            SetCameras();

            Enabled = true;
        }

        private void CalculateInitialSize()
        {
            if (Settings.Width > Settings.Height)
            {
                _adjCamImageHeightSize = Settings.Height * MaxCameraSize / Settings.Width;
                _initialCamImageHeightSize = _adjCamImageHeightSize;
                _adjCamImageWidthSize = 360;


            }
            else
            {
                _adjCamImageWidthSize = Settings.Width * MaxCameraSize / Settings.Height;
                _initialCamImageWidthSize = _adjCamImageWidthSize;
                _adjCamImageHeightSize = 360;
            }

            Debug.Log($"OCISLY:_adjCamImageHeightSize = {_adjCamImageHeightSize} _adjCamImageWidthSize = {_adjCamImageWidthSize}");
        }

        public string Name { get; private set; }

        public Vessel Vessel => _hullcamera?.vessel;

        public int Id { get; }

        public bool Enabled { get; set; }

        public float TargetWindowScaleMax { get; set; } = 3f;

        public float TargetWindowScaleMin { get; set; } = 0.5f;


        public bool ResizingWindow { get; set; }

        public float TargetWindowScale { get; set; } = 1;
        public string AltitudeString { get; private set; }
        public string SpeedString { get; private set; }
        public bool StreamingEnabled { get; private set; }

        public void EnableStreaming()
        {
            if (!Enabled) return;
            if (StreamingEnabled) return;
            StreamingEnabled = true;
            Debug.Log($"[OCISLY] cam={Id} auto-enabled streaming");
        }

        private Camera FindCamera(string cameraName)
        {
            foreach (var cam in Camera.allCameras)
                if (cam.name == cameraName)
                    return cam;

            Debug.Log("Couldn't find " + cameraName);
            return null;
        }

        private void SetCameras()
        {
            var cam1Obj = new GameObject();
            var partNearCamera = cam1Obj.AddComponent<Camera>();

            partNearCamera.CopyFrom(Camera.allCameras.FirstOrDefault(cam => cam.name == "Camera 00"));
            partNearCamera.name = "jrNear";
            partNearCamera.transform.parent = _hullcamera.cameraTransformName.Length <= 0
                ? _hullcamera.part.transform
                : _hullcamera.part.FindModelTransform(_hullcamera.cameraTransformName);
            partNearCamera.transform.localRotation =
                Quaternion.LookRotation(_hullcamera.cameraForward, _hullcamera.cameraUp);
            partNearCamera.transform.localPosition = _hullcamera.cameraPosition;
            partNearCamera.fieldOfView = 50;
            partNearCamera.targetTexture = TargetCamRenderTexture;
            partNearCamera.allowHDR = true;
            partNearCamera.allowMSAA = true;
            partNearCamera.enabled = true;
            _cameras[0] = partNearCamera;
            _cameras[0].allowHDR = true;
            cam1Obj.AddComponent<CanvasHack>();

            //TUFX
            AddTufxPostProcessing();

             var cam2Obj = new GameObject();
            var partScaledCamera = cam2Obj.AddComponent<Camera>();
            var mainSkyCam = FindCamera("Camera ScaledSpace");

            partScaledCamera.CopyFrom(mainSkyCam);
            partScaledCamera.name = "jrScaled";


            partScaledCamera.transform.parent = mainSkyCam.transform;
            partScaledCamera.transform.localRotation = Quaternion.identity;
            partScaledCamera.transform.localPosition = Vector3.zero;
            partScaledCamera.transform.localScale = Vector3.one;
            partScaledCamera.fieldOfView = 50;
            partScaledCamera.targetTexture = TargetCamRenderTexture;
            partScaledCamera.allowHDR = true;
            partScaledCamera.allowMSAA = true;
            partScaledCamera.enabled = true;
            _cameras[1] = partScaledCamera;


            var camRotator = cam2Obj.AddComponent<TgpCamRotator>();
            camRotator.NearCamera = partNearCamera;
            cam2Obj.AddComponent<CanvasHack>();

            //galaxy camera
            var galaxyCamObj = new GameObject();
            var galaxyCam = galaxyCamObj.AddComponent<Camera>();
            var mainGalaxyCam = FindCamera("GalaxyCamera");

            galaxyCam.CopyFrom(mainGalaxyCam);
            galaxyCam.name = "jrGalaxy";
            galaxyCam.transform.parent = mainGalaxyCam.transform;
            galaxyCam.transform.position = Vector3.zero;
            galaxyCam.transform.localRotation = Quaternion.identity;
            galaxyCam.transform.localScale = Vector3.one;
            galaxyCam.fieldOfView = 50;
            galaxyCam.targetTexture = TargetCamRenderTexture;
            galaxyCam.allowHDR = true;
            galaxyCam.allowMSAA = true;
            galaxyCam.enabled = true;
            _cameras[2] = galaxyCam;

            var camRotatorgalaxy = galaxyCamObj.AddComponent<TgpCamRotator>();
            camRotatorgalaxy.NearCamera = partNearCamera;
            galaxyCamObj.AddComponent<CanvasHack>();

            foreach (var t in _cameras)
                t.enabled = false;
        }

        private void AddTufxPostProcessing()
        {
            try
            {
                TufxWrapper.AddPostProcessing(_cameras[0]);
            }
            catch
            {
                // ignored
            }
        }

        public void CreateGui()
        {
            if (!Enabled) return;

            if (_hullcamera == null || _hullcamera.vessel == null)
            {
                Disable();
                return;
            }

            Name = _hullcamera.vessel.GetDisplayName() + "." + _hullcamera.cameraName;

            _windowRect = GUI.Window(Id, _windowRect, WindowTargetCam,
                Name);
        }

        public void CheckIfResizing()
        {
            if (!Enabled) return;

            if (Event.current.type == EventType.MouseUp)
                if (ResizingWindow)
                    ResizingWindow = false;
        }

        private void WindowTargetCam(int windowId)
        {
            if (!Enabled) return;

            _adjCamImageWidthSize = _initialCamImageWidthSize * TargetWindowScale;
            _adjCamImageHeightSize = _initialCamImageHeightSize * TargetWindowScale;

            GUI.DragWindow(new Rect(0, 0, _windowHeight - 18, 30));
            if (GUI.Button(new Rect(_windowWidth - 18, 2, 20, 16), "X", GUI.skin.button))
            {
                Disable();

                return;
            }

            var imageRect = DrawTexture();

            // Right side control buttons
            DrawSideControlButtons(imageRect);

            DrawTelemetry(imageRect);


            //resizing
            var resizeRect =
                new Rect(_windowWidth - 18, _windowHeight - 18, 16, 16);


            GUI.DrawTexture(resizeRect, ResizeTexture, ScaleMode.StretchToFill, true);

            if (Event.current.type == EventType.MouseDown && Event.current.clickCount == 2 && imageRect.Contains(Event.current.mousePosition))
            {
                MinimalUi = !MinimalUi;
                ResizeTargetWindow();
            }

            if (Event.current.type == EventType.MouseDown && resizeRect.Contains(Event.current.mousePosition))
                ResizingWindow = true;

            if (Event.current.type == EventType.Repaint && ResizingWindow)
                if (Math.Abs(Mouse.delta.x) > 1 || Math.Abs(Mouse.delta.y) > 0.1f)
                {
                    var diff = Mouse.delta.x + Mouse.delta.y;
                    UpdateTargetScale(diff);
                    ResizeTargetWindow();
                }

            //ResetZoomKeys();
            RepositionWindow(ref _windowRect);
        }

        private Rect DrawTexture()
        {
            var imageRect = new Rect(2, 20, _adjCamImageWidthSize, _adjCamImageHeightSize);


            GUI.DrawTexture(imageRect, TargetCamRenderTexture, ScaleMode.StretchToFill, false);
            return imageRect;
        }

        private void DrawTelemetry(Rect imageRect)
        {
            if (MinimalUi) return;

            var dataStyle = new GUIStyle(TelemetryGuiStyle)
            {
                fontSize = (int) Mathf.Clamp(16 * TargetWindowScale, 9, 17),
            };

            var targetRangeRect = new Rect(imageRect.x,
                _adjCamImageHeightSize * 0.94f - (int) Mathf.Clamp(18 * TargetWindowScale, 9, 18), _adjCamImageWidthSize,
                (int) Mathf.Clamp(18 * TargetWindowScale, 10, 18));


            GUI.Label(targetRangeRect, String.Concat(AltitudeString, Environment.NewLine, SpeedString), dataStyle);
        }

        public bool MinimalUi { get; set; }

        private void DrawSideControlButtons(Rect imageRect)
        {
            if (MinimalUi) return;

            var startX = imageRect.width + 3 * Gap;
            var streamingRect = new Rect(startX, controlsStartY, ButtonWidth, ButtonHeight + Line);

            if (!StreamingEnabled)
            {
                if (GUI.Button(streamingRect, "Enable streaming", ButtonStyle)) StreamingEnabled = true;
            }
            else
            {
                if (GUI.Button(streamingRect, "Disable streaming", ButtonStyle)) StreamingEnabled = false;
            }
        }

        public void CalculateSpeedAltitude()
        {
            var altitudeInKm = (float) Math.Round(_hullcamera.vessel.altitude / 1000f, 1);
            var speed = (int) Math.Round(_hullcamera.vessel.speed * 3.6f, 0);

            AltitudeString = string.Concat(Altitude, altitudeInKm.ToString("0.0"), Km);
            SpeedString = string.Concat(Speed, speed, Kmh);
        }

        private void UpdateTargetScale(float diff)
        {
            var scaleDiff = diff / (_windowRect.width + _windowRect.height) * 100 * .01f;
            TargetWindowScale += Mathf.Abs(scaleDiff) > .01f ? scaleDiff : scaleDiff > 0 ? .01f : -.01f;

            TargetWindowScale += Mathf.Abs(scaleDiff) > .01f ? scaleDiff : scaleDiff > 0 ? .01f : -.01f;
            TargetWindowScale = Mathf.Clamp(TargetWindowScale,
                TargetWindowScaleMin,
                TargetWindowScaleMax);
        }


        private void ResizeTargetWindow()
        {
            if (MinimalUi)
            {
                _windowWidth = _initialCamImageWidthSize* TargetWindowScale + 2 * Gap;
            }
            else
            {
                _windowWidth = _initialCamImageWidthSize * TargetWindowScale + 3 * ButtonHeight + 16 + 2 * Gap;
            }
            _windowHeight = _initialCamImageHeightSize * TargetWindowScale + 23;
            _windowRect = new Rect(_windowRect.x, _windowRect.y, _windowWidth, _windowHeight);
        }

        internal static void RepositionWindow(ref Rect windowPosition)
        {
            // This method uses Gui point system.
            if (windowPosition.x < 0) windowPosition.x = 0;
            if (windowPosition.y < 0) windowPosition.y = 0;

            if (windowPosition.xMax > Screen.width)
                windowPosition.x = Screen.width - windowPosition.width;
            if (windowPosition.yMax > Screen.height)
                windowPosition.y = Screen.height - windowPosition.height;
        }

        public void Disable()
        {
            Enabled = false;
            StreamingEnabled = false;
            this.TargetCamRenderTexture.Release();

            // Release the filter scratch RT to free its GPU allocation when
            // the camera goes away (vessel change, user-closed window, etc.).
            // Mirrors the TargetCamRenderTexture lifetime.
            if (_filteredRenderTexture != null)
            {
                _filteredRenderTexture.Release();
            }

            if (_readbackRenderTexture != null)
            {
                _readbackRenderTexture.Release();
            }

            // Give NightVision (and any future filter with side effects) a
            // chance to undo what Activate() did. Filter instance is dropped
            // — re-enabling the camera will rebuild it on next frame.
            if (_filter != null)
            {
                try { _filter.Deactivate(); }
                catch (Exception ex)
                {
                    Debug.Log($"[OCISLY] cam={Id} filter Deactivate threw: {ex.GetType().Name}: {ex.Message}");
                }
                _filter = null;
                _filterMode = -1;
            }

            foreach (var camera in _cameras)
                if (camera != null)
                    camera.enabled = false;
        }
    }
}
