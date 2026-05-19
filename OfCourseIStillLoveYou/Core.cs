using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HullcamVDS;
using OfCourseIStillLoveYou.Client;
using UnityEngine;

namespace OfCourseIStillLoveYou
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class Core : MonoBehaviour
    {
        public static  Dictionary<int, TrackingCamera> TrackedCameras = new Dictionary<int, TrackingCamera>();

        private void Awake()
        {
            GrpcClient.ConnectToServer(Settings.EndPoint,Settings.Port);

            if (Settings.AutoStream)
            {
                GameEvents.onVesselChange.Add(OnVesselChange);
                GameEvents.onVesselWasModified.Add(OnVesselChange);
                GameEvents.onFlightReady.Add(OnFlightReady);
            }
        }

        private void OnDestroy()
        {
            if (Settings.AutoStream)
            {
                GameEvents.onVesselChange.Remove(OnVesselChange);
                GameEvents.onVesselWasModified.Remove(OnVesselChange);
                GameEvents.onFlightReady.Remove(OnFlightReady);
            }
        }

        private void OnFlightReady()
        {
            AutoStreamSweep();
        }

        private void OnVesselChange(Vessel _)
        {
            AutoStreamSweep();
        }

        private static void AutoStreamSweep()
        {
            if (!Settings.AutoStream) return;
            if (!FlightGlobals.ready) return;

            foreach (var hullCamera in GetAllTrackingCameras())
            {
                var instanceId = hullCamera.GetInstanceID();
                if (TrackedCameras.TryGetValue(instanceId, out var existing))
                {
                    existing.EnableStreaming();
                    continue;
                }

                var newCamera = new TrackingCamera(instanceId, hullCamera);
                TrackedCameras.Add(instanceId, newCamera);
                newCamera.EnableStreaming();
                Log($"auto-streaming new camera (id={instanceId}, vessel={hullCamera.vessel?.GetDisplayName()})");
            }
        }


        public static void Log(string message)
        {
            Debug.Log($"[OfCourseIStillLoveYou]: {message}");
        }

        public static List<MuMechModuleHullCamera> GetAllTrackingCameras()
        {
            List<MuMechModuleHullCamera> result = new List<MuMechModuleHullCamera>();

            if (!FlightGlobals.ready) return result;


            foreach (var vessel in FlightGlobals.VesselsLoaded)
            {
                result.AddRange(vessel.FindPartModulesImplementing<MuMechModuleHullCamera>());
            }

            return result;
        }

        void Update()
        {
            ToggleRender();
        }

        void LateUpdate()
        {
            Refresh();
        }


        private void Refresh()
        {
            foreach (var trackedCamerasValue in TrackedCameras.Values.Where(trackedCamerasValue => trackedCamerasValue.Enabled))
            {
                if (!trackedCamerasValue.OddFrames) continue;
               
                trackedCamerasValue.CalculateSpeedAltitude();
                trackedCamerasValue.SendCameraImage();
               
            }
        }

        private void ToggleRender()
        {
            foreach (var trackedCamerasValue in TrackedCameras.Values.Where(trackedCamerasValue => trackedCamerasValue.Enabled))
            {
                trackedCamerasValue.ToogleCameras();
            }
        }
    }
}
