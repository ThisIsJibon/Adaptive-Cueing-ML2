using UnityEngine;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.NativeTypes;
using MagicLeap.OpenXR.Features.LocalizationMaps;

namespace AdaptiveCueing
{
    public enum SpaceLocalizationStatus
    {
        NotInitialized,
        WaitingForLocalization,
        Localized,
        Failed
    }

    public enum GroundDetectionState
    {
        WaitingForSpace,
        WaitingForGroundLook,
        GroundSet
    }

    [DisallowMultipleComponent]
    public class MLSpaceGroundDetector : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private ARRenderer arRenderer;

        [Header("Ground Detection")]
        [Tooltip("How far to project your gaze to determine ground level")]
        [SerializeField] private float gazeDistance = 3f;

        [Header("Localization")]
        [Tooltip("Seconds to wait for Space localization before timeout")]
        [SerializeField] private float localizationTimeout = 10f;

        private MagicLeapLocalizationMapFeature localizationMapFeature;
        private SpaceLocalizationStatus status = SpaceLocalizationStatus.NotInitialized;
        private GroundDetectionState groundState = GroundDetectionState.WaitingForSpace;
        private string localizedSpaceName = "";
        private float initTime;
        private bool timedOut;
        private Camera mainCamera;

        public SpaceLocalizationStatus Status => status;
        public GroundDetectionState GroundState => groundState;
        public string LocalizedSpaceName => localizedSpaceName;
        public bool IsLocalized => status == SpaceLocalizationStatus.Localized;
        public bool IsWaitingForGroundLook => groundState == GroundDetectionState.WaitingForGroundLook;
        public bool IsGroundSet => groundState == GroundDetectionState.GroundSet;
        public bool HasTimedOut => timedOut;

        private void Awake()
        {
            Debug.Log("[MLSpace] MLSpaceGroundDetector Awake - Component is in scene!");
        }

        private void Start()
        {
            Debug.Log("[MLSpace] MLSpaceGroundDetector Start - Initializing...");
            initTime = Time.time;
            mainCamera = Camera.main;

            if (arRenderer == null)
            {
                arRenderer = FindObjectOfType<ARRenderer>();
                if (arRenderer != null)
                    Debug.Log("[MLSpace] Found ARRenderer.");
            }

            InitializeLocalizationFeature();
        }

        private void Update()
        {
            if (!timedOut && status == SpaceLocalizationStatus.WaitingForLocalization)
            {
                if (Time.time - initTime > localizationTimeout)
                {
                    timedOut = true;
                    Debug.LogWarning($"[MLSpace] Localization timeout ({localizationTimeout}s).");
                }
            }
        }

        public bool TrySetGroundFromCrosshair()
        {
            if (groundState != GroundDetectionState.WaitingForGroundLook)
            {
                if (groundState == GroundDetectionState.GroundSet)
                    Debug.Log("[MLSpace] Ground already set.");
                return false;
            }

            if (mainCamera == null)
            {
                mainCamera = Camera.main;
                if (mainCamera == null)
                {
                    Debug.LogError("[MLSpace] No camera found!");
                    return false;
                }
            }

            // Calculate the point where you're looking at gazeDistance meters away
            Vector3 gazePoint = mainCamera.transform.position + mainCamera.transform.forward * gazeDistance;
            float groundY = gazePoint.y;

            Debug.Log($"[MLSpace] Camera pos: {mainCamera.transform.position}");
            Debug.Log($"[MLSpace] Looking direction: {mainCamera.transform.forward}");
            Debug.Log($"[MLSpace] Gaze point ({gazeDistance}m away): {gazePoint}");
            Debug.Log($"[MLSpace] === GROUND Y = {groundY:F2}m ===");

            groundState = GroundDetectionState.GroundSet;

            if (arRenderer != null)
            {
                arRenderer.SetGroundHeightFromPlaneDetection(groundY);
                Debug.Log("[MLSpace] Ground set! Press trigger to place cues.");
            }

            TelemetryLogger logger = FindObjectOfType<TelemetryLogger>();
            if (logger != null)
            {
                logger.LogEvent("ground_set", string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "ground_y={0:F3};space={1}", groundY, localizedSpaceName));
            }

            return true;
        }

        private void InitializeLocalizationFeature()
        {
            Debug.Log("[MLSpace] Checking for Localization Map Feature...");

            if (OpenXRSettings.Instance == null)
            {
                Debug.LogError("[MLSpace] OpenXRSettings.Instance is NULL! OpenXR may not be initialized yet.");
                status = SpaceLocalizationStatus.Failed;
                return;
            }

            localizationMapFeature = OpenXRSettings.Instance.GetFeature<MagicLeapLocalizationMapFeature>();

            if (localizationMapFeature == null)
            {
                Debug.LogError("[MLSpace] MagicLeapLocalizationMapFeature is NULL. Feature not found in OpenXR settings.");
                status = SpaceLocalizationStatus.Failed;
                return;
            }

            if (!localizationMapFeature.enabled)
            {
                Debug.LogError("[MLSpace] Localization Map Feature found but NOT ENABLED. Enable it in: Edit > Project Settings > XR Plug-in Management > OpenXR > Magic Leap 2 Localization Maps");
                status = SpaceLocalizationStatus.Failed;
                return;
            }

            Debug.Log("[MLSpace] Localization Map Feature found and enabled. Enabling events...");

            XrResult result = localizationMapFeature.EnableLocalizationEvents(true);
            if (result != XrResult.Success)
            {
                Debug.LogError($"[MLSpace] Failed to enable localization events: {result}");
                status = SpaceLocalizationStatus.Failed;
                return;
            }

            MagicLeapLocalizationMapFeature.OnLocalizationChangedEvent += OnLocalizationChanged;
            status = SpaceLocalizationStatus.WaitingForLocalization;

            Debug.Log("[MLSpace] === INITIALIZED === Waiting for Space localization...");
            Debug.Log("[MLSpace] Make sure you have mapped this area with the ML2 Spaces app!");
        }

        private void OnDestroy()
        {
            if (localizationMapFeature != null)
            {
                MagicLeapLocalizationMapFeature.OnLocalizationChangedEvent -= OnLocalizationChanged;
            }
        }

        private void OnLocalizationChanged(LocalizationEventData data)
        {
            switch (data.State)
            {
                case LocalizationMapState.Localized:
                    localizedSpaceName = data.Map.Name;
                    status = SpaceLocalizationStatus.Localized;
                    groundState = GroundDetectionState.WaitingForGroundLook;
                    
                    Debug.Log($"[MLSpace] === LOCALIZED to Space: \"{localizedSpaceName}\" (Confidence: {data.Confidence}) ===");
                    Debug.Log("[MLSpace] ========================================");
                    Debug.Log("[MLSpace] LOOK AT THE GROUND");
                    Debug.Log("[MLSpace] Press TRIGGER to set ground level");
                    Debug.Log("[MLSpace] ========================================");
                    break;

                case LocalizationMapState.LocalizationPending:
                    status = SpaceLocalizationStatus.WaitingForLocalization;
                    Debug.Log("[MLSpace] Localization pending...");
                    break;

                case LocalizationMapState.SleepingBeforeRetry:
                    Debug.Log($"[MLSpace] Localization failed, retrying... Errors: {data.Errors}");
                    break;

                case LocalizationMapState.NotLocalized:
                    status = SpaceLocalizationStatus.WaitingForLocalization;
                    localizedSpaceName = "";
                    Debug.Log("[MLSpace] Not localized to any Space.");
                    break;
            }
        }
    }
}
