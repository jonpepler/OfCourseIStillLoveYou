#if KERBCAM_BASELINE
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace OfCourseIStillLoveYou
{
    // Baseline perf instrumentation for the kerbcam rebuild planning work.
    // See the kerbcam-spike branch README for context.
    //
    // Compile-time gated on KERBCAM_BASELINE; default builds carry zero overhead
    // and no extra code (the entire class is excluded).
    public static class KerbCamBaseline
    {
        private const string OutputPath = "GameData/OfCourseIStillLoveYou/baseline.csv";
        private const int FlushIntervalSamples = 60; // ~2s @ 30 fps

        private static readonly List<string> Pending = new List<string>(128);
        private static bool _headerWritten;
        private static int _sinceFlush;

        // Schema (current): unscaled_time_ms,camera_id,camera_name,encode_ms,jpeg_bytes,callback_ms
        //
        // Column semantics changed when the AsyncGPUReadback spike landed:
        //   - `encode_ms` is now the SYNC main-thread cost of issuing the
        //     readback request (formerly it covered ReadPixels + EncodeToJPG).
        //     This is the headline number the spike is trying to drive down.
        //   - `callback_ms` (new, last column) is the main-thread cost of the
        //     readback callback: LoadRawTextureData + Apply + EncodeToJPG +
        //     payload construction + Task.Run scheduling. The Task body
        //     itself (gRPC send) runs off-thread and is excluded.
        //
        // Pre-spike baseline CSVs only have the first 5 columns and should be
        // compared against `encode_ms` only; post-spike rows should sum
        // `encode_ms + callback_ms` for the equivalent total main-thread cost.
        public static void LogCaptureFrame(int cameraId, string cameraName, double encodeMs, int jpegBytes, double callbackMs)
        {
            // Time.unscaledTime is main-thread only, so this method is too.
            // The capture site is already on the Unity main thread, so it's
            // fine to call directly from SendCameraImage().
            var nowMs = Time.unscaledTime * 1000.0;
            var line = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:F1},{1},{2},{3:F3},{4},{5:F3}",
                nowMs, cameraId, cameraName, encodeMs, jpegBytes, callbackMs);
            Pending.Add(line);
            _sinceFlush++;
            if (_sinceFlush >= FlushIntervalSamples) Flush();
        }

        // Returns Time.unscaledTime * 1000 as a string, suitable for piping
        // through OCISLY's existing string-typed metadata fields (e.g. Altitude).
        // Used to encode a KSP-side capture timestamp the relay can pair with
        // its own receipt time.
        public static string UnscaledTimeMsString()
        {
            return (Time.unscaledTime * 1000.0).ToString(
                "F1", System.Globalization.CultureInfo.InvariantCulture);
        }

        public static void Flush()
        {
            if (Pending.Count == 0) return;
            try
            {
                var path = Path.Combine(KSPUtil.ApplicationRootPath, OutputPath);
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using (var sw = new StreamWriter(path, append: true, encoding: Encoding.UTF8))
                {
                    if (!_headerWritten)
                    {
                        sw.WriteLine("unscaled_time_ms,camera_id,camera_name,encode_ms,jpeg_bytes,callback_ms");
                        _headerWritten = true;
                    }
                    foreach (var row in Pending) sw.WriteLine(row);
                }
            }
            catch (Exception ex)
            {
                Debug.Log("[KerbCamBaseline] flush failed: " + ex.Message);
            }
            finally
            {
                Pending.Clear();
                _sinceFlush = 0;
            }
        }
    }
}
#endif
