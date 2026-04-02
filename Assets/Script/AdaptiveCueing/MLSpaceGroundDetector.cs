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

    [DisallowMultipleComponent]
    public class MLSpaceGroundDetector : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private ARRenderer arRenderer;

        [Header("Ground Settings")]
        [Tooltip("Estimated user height in meters (used to calculate floor from head position)")]
        [SerializeField] private float estimatedUserHeight = 1.65f;
        [SerializeField] private bool autoSetGroundOnLocalized = true;

        [Header("Fallback Settings")]
        [Tooltip("Seconds to wait for Space localization before allowing manual calibration fallback")]
        [SerializeField] private float localizationTimeout = 10f;

        private MagicLeapLocalizationMapFeature localizationMapFeature;
        private SpaceLocalizationStatus status = SpaceLocalizationStatus.NotInitialized;
        private string localizedSpaceName = "";
        private float initTime;
        private bool timedOut;

        public SpaceLocalizationStatus Status => status;
        public string LocalizedSpaceName => localizedSpaceName;
        public bool IsLocalized => status == SpaceLocalizationStatus.Localized;
        public bool HasTimedOut => timedOut;

        private void Awake()
        {
            Debug.Log("[MLSpace] MLSpaceGroundDetector Awake - Component is in scene!");
        }

        private void Start()
        {
            Debug.Log("[MLSpace] MLSpaceGroundDetector Start - Initializing...");
            initTime = Time.time;

            if (arRenderer == null)
            {
                arRenderer = FindObjectOfType<ARRenderer>();
                if (arRenderer != null)
                {
                    Debug.Log("[MLSpace] Found ARRenderer automatically.");
                }
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
                    Debug.LogWarning($"[MLSpace] Localization timeout ({localizationTimeout}s). Fallback to manual calibration is now allowed.");
                }
            }
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
                    Debug.Log($"[MLSpace] === LOCALIZED to Space: \"{localizedSpaceName}\" (Confidence: {data.Confidence}) ===");

                    if (autoSetGroundOnLocalized)
                    {
                        SetGroundFromSpaceOrigin();
                    }
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

        public void SetGroundFromSpaceOrigin()
        {
            if (localizationMapFeature == null)
            {
                Debug.LogError("[MLSpace] Localization feature not available.");
                return;
            }

            Camera mainCam = Camera.main;
            if (mainCam == null)
            {
                Debug.LogError("[MLSpace] No main camera found to estimate ground height.");
                return;
            }

            float headHeight = mainCam.transform.position.y;
            float groundHeight = headHeight - estimatedUserHeight;

            Pose mapOrigin = localizationMapFeature.GetMapOrigin();
            Debug.Log($"[MLSpace] Space origin: {mapOrigin.position}");
            Debug.Log($"[MLSpace] Head position Y: {headHeight:F3}m");
            Debug.Log($"[MLSpace] Estimated user height: {estimatedUserHeight:F2}m");
            Debug.Log($"[MLSpace] === GROUND SET to Y={groundHeight:F3}m ===");
            Debug.Log($"[MLSpace] Ready to place cues with trigger!");

            if (arRenderer != null)
            {
                arRenderer.SetGroundHeightFromPlaneDetection(groundHeight);
            }
            else
            {
                Debug.LogWarning("[MLSpace] No ARRenderer found to set ground height.");
            }
        }
    }
}
