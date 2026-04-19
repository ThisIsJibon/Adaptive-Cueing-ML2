using System.Collections.Generic;
using MagicLeap.Android;
using MagicLeap.OpenXR.Features.LocalizationMaps;
using MagicLeap.OpenXR.Subsystems;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.NativeTypes;

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
        [SerializeField, Range(0.5f, 1f)] private float floorNormalThreshold = 0.8f;
        [SerializeField, Min(0.1f)] private float minimumGroundDrop = 0.25f;
        [SerializeField, Min(0.5f)] private float maximumGroundDrop = 2.5f;
        [SerializeField, Min(1f)] private float fallbackStandingHeight = 1.6f;

        [Header("Plane Query")]
        [SerializeField, Min(1f)] private float planeQueryRadius = 4f;
        [SerializeField, Min(0.1f)] private float planeQueryHeight = 2f;
        [SerializeField, Min(8)] private uint planeQueryMaxResults = 64;
        [SerializeField, Min(0.04f)] private float minimumPlaneArea = 0.25f;

        [Header("Localization")]
        [SerializeField, Min(1f)] private float localizationTimeout = 10f;

        private static readonly List<ARRaycastHit> RaycastHits = new List<ARRaycastHit>();

        private MagicLeapLocalizationMapFeature localizationMapFeature;
        private SpaceLocalizationStatus status = SpaceLocalizationStatus.NotInitialized;
        private GroundDetectionState groundState = GroundDetectionState.WaitingForSpace;
        private string localizedSpaceName = string.Empty;
        private float initTime;
        private bool timedOut;
        private bool localizationInitialized;
        private bool spatialMappingPermissionGranted;
        private bool spaceManagerPermissionGranted;
        private Camera mainCamera;
        private ARRaycastManager raycastManager;
        private ARPlaneManager planeManager;

        public SpaceLocalizationStatus Status => status;
        public GroundDetectionState GroundState => groundState;
        public string LocalizedSpaceName => localizedSpaceName;
        public bool IsLocalized => status == SpaceLocalizationStatus.Localized;
        public bool IsWaitingForGroundLook => groundState == GroundDetectionState.WaitingForGroundLook;
        public bool IsGroundSet => groundState == GroundDetectionState.GroundSet;
        public bool HasTimedOut => timedOut;

        private void Start()
        {
            initTime = Time.time;
            mainCamera = Camera.main;

            if (arRenderer == null)
            {
                arRenderer = FindObjectOfType<ARRenderer>();
            }

            EnsurePlaneManagers();
            RequestRequiredPermissions();
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

            UpdatePlaneQuery();
        }

        public bool TrySetGroundFromCrosshair()
        {
            if (groundState != GroundDetectionState.WaitingForGroundLook)
            {
                if (groundState == GroundDetectionState.GroundSet)
                {
                    Debug.Log("[MLSpace] Ground already set.");
                }
                return false;
            }

            if (mainCamera == null)
            {
                mainCamera = Camera.main;
                if (mainCamera == null)
                {
                    Debug.LogError("[MLSpace] No camera found.");
                    return false;
                }
            }

            GroundPoseEstimate estimate;
            if (!TryResolveLocalizedGroundPose(out estimate))
            {
                estimate = BuildHeadHeightFallback();
                Debug.LogWarning("[MLSpace] No reliable localized floor hit found. Falling back to head-height estimate.");
            }

            groundState = GroundDetectionState.GroundSet;
            if (arRenderer != null)
            {
                arRenderer.SetGroundPose(estimate);
            }

            Debug.Log($"[MLSpace] Ground pose selected from {estimate.Source} at {estimate.Position} with confidence {estimate.Confidence:F2}.");
            return true;
        }

        private void RequestRequiredPermissions()
        {
            spatialMappingPermissionGranted = Permissions.CheckPermission(Permissions.SpatialMapping);
            spaceManagerPermissionGranted = Permissions.CheckPermission(Permissions.SpaceManager);

            if (spatialMappingPermissionGranted && spaceManagerPermissionGranted)
            {
                InitializeSystemsAfterPermissions();
                return;
            }

            Permissions.RequestPermissions(
                new[] { Permissions.SpatialMapping, Permissions.SpaceManager },
                OnPermissionGranted,
                OnPermissionDenied,
                OnPermissionDenied);
        }

        private void OnPermissionGranted(string permission)
        {
            if (permission == Permissions.SpatialMapping)
            {
                spatialMappingPermissionGranted = true;
            }
            else if (permission == Permissions.SpaceManager)
            {
                spaceManagerPermissionGranted = true;
            }

            InitializeSystemsAfterPermissions();
        }

        private void OnPermissionDenied(string permission)
        {
            if (permission == Permissions.SpatialMapping)
            {
                Debug.LogWarning("[MLSpace] Spatial mapping permission denied. Plane-backed ground detection will be limited.");
            }
            else if (permission == Permissions.SpaceManager)
            {
                Debug.LogWarning("[MLSpace] Space manager permission denied. Space localization cannot start.");
                status = SpaceLocalizationStatus.Failed;
            }
        }

        private void InitializeSystemsAfterPermissions()
        {
            EnsurePlaneManagers();

            if (spatialMappingPermissionGranted && planeManager != null)
            {
                planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal;
                planeManager.enabled = true;
            }

            if (spatialMappingPermissionGranted && raycastManager != null)
            {
                raycastManager.enabled = true;
            }

            if (spaceManagerPermissionGranted && !localizationInitialized)
            {
                InitializeLocalizationFeature();
                localizationInitialized = true;
            }
        }

        private void EnsurePlaneManagers()
        {
            if (raycastManager != null && planeManager != null)
            {
                return;
            }

            XROrigin xrOrigin = FindObjectOfType<XROrigin>();
            if (xrOrigin == null)
            {
                raycastManager = FindObjectOfType<ARRaycastManager>();
                planeManager = FindObjectOfType<ARPlaneManager>();
                return;
            }

            raycastManager = xrOrigin.GetComponent<ARRaycastManager>();
            if (raycastManager == null)
            {
                raycastManager = xrOrigin.gameObject.AddComponent<ARRaycastManager>();
            }

            planeManager = xrOrigin.GetComponent<ARPlaneManager>();
            if (planeManager == null)
            {
                planeManager = xrOrigin.gameObject.AddComponent<ARPlaneManager>();
            }
        }

        private void UpdatePlaneQuery()
        {
            if (!spatialMappingPermissionGranted || mainCamera == null || planeManager == null || !planeManager.enabled)
            {
                return;
            }

            MLXrPlaneSubsystem.Query = new MLXrPlaneSubsystem.PlanesQuery
            {
                Flags = MLXrPlaneSubsystem.MLPlanesQueryFlags.Horizontal | MLXrPlaneSubsystem.MLPlanesQueryFlags.SemanticAll,
                BoundsCenter = mainCamera.transform.position,
                BoundsRotation = mainCamera.transform.rotation,
                BoundsExtents = new Vector3(planeQueryRadius, planeQueryHeight, planeQueryRadius),
                MaxResults = planeQueryMaxResults,
                MinPlaneArea = minimumPlaneArea
            };
        }

        private bool TryResolveLocalizedGroundPose(out GroundPoseEstimate estimate)
        {
            List<GroundCandidate> candidates = new List<GroundCandidate>(4);

            GroundCandidate candidate;
            if (TryGetGroundCandidateFromViewRay(out candidate))
            {
                candidates.Add(candidate);
            }

            if (TryGetGroundCandidateFromDownRay(out candidate))
            {
                candidates.Add(candidate);
            }

            if (TryGetGroundCandidateFromTrackedPlanes(out candidate))
            {
                candidates.Add(candidate);
            }

            if (candidates.Count == 0)
            {
                estimate = default;
                return false;
            }

            candidates.Sort((left, right) => left.Position.y.CompareTo(right.Position.y));
            float medianY = candidates[candidates.Count / 2].Position.y;
            GroundCandidate bestCandidate = candidates[0];
            float bestScore = float.MaxValue;
            Vector3 normalAccumulator = Vector3.zero;

            for (int index = 0; index < candidates.Count; index++)
            {
                GroundCandidate current = candidates[index];
                float heightDifference = Mathf.Abs(current.Position.y - medianY);
                float score = heightDifference - (current.Confidence * 0.1f);

                if (score < bestScore)
                {
                    bestScore = score;
                    bestCandidate = current;
                }

                if (heightDifference <= 0.08f)
                {
                    normalAccumulator += current.Normal * current.Confidence;
                }
            }

            Vector3 resolvedNormal = normalAccumulator.sqrMagnitude > 0.001f
                ? normalAccumulator.normalized
                : bestCandidate.Normal;
            Vector3 projectedGroundPoint = ProjectPointOntoPlane(mainCamera.transform.position, bestCandidate.Position, resolvedNormal);
            float estimatedUserHeight = Mathf.Clamp(Vector3.Distance(mainCamera.transform.position, projectedGroundPoint), 1f, 2.25f);

            estimate = new GroundPoseEstimate
            {
                IsValid = true,
                Position = projectedGroundPoint,
                Normal = resolvedNormal,
                EstimatedUserHeight = estimatedUserHeight,
                Source = bestCandidate.Source,
                SampleCount = candidates.Count,
                Confidence = Mathf.Clamp01(bestCandidate.Confidence + ((candidates.Count - 1) * 0.08f))
            };
            return true;
        }

        private bool TryGetGroundCandidateFromViewRay(out GroundCandidate candidate)
        {
            candidate = default;

            if (raycastManager == null)
            {
                return false;
            }

            Vector2 screenCenter = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            if (!raycastManager.Raycast(screenCenter, RaycastHits, TrackableType.PlaneWithinPolygon))
            {
                return false;
            }

            for (int index = 0; index < RaycastHits.Count; index++)
            {
                ARRaycastHit hit = RaycastHits[index];
                if (!IsValidGroundHit(hit.pose.position, hit.pose.up))
                {
                    continue;
                }

                candidate = new GroundCandidate
                {
                    Position = hit.pose.position,
                    Normal = hit.pose.up.normalized,
                    Confidence = Mathf.Clamp01(0.95f - (index * 0.08f)),
                    Source = GroundPoseSource.LocalizedPlaneRaycast
                };
                return true;
            }

            return false;
        }

        private bool TryGetGroundCandidateFromDownRay(out GroundCandidate candidate)
        {
            candidate = default;

            if (raycastManager == null || mainCamera == null)
            {
                return false;
            }

            Ray downwardRay = new Ray(mainCamera.transform.position + (Vector3.up * 0.05f), Vector3.down);
            if (!raycastManager.Raycast(downwardRay, RaycastHits, TrackableType.PlaneWithinPolygon))
            {
                return false;
            }

            for (int index = 0; index < RaycastHits.Count; index++)
            {
                ARRaycastHit hit = RaycastHits[index];
                if (!IsValidGroundHit(hit.pose.position, hit.pose.up))
                {
                    continue;
                }

                candidate = new GroundCandidate
                {
                    Position = hit.pose.position,
                    Normal = hit.pose.up.normalized,
                    Confidence = Mathf.Clamp01(0.88f - (index * 0.06f)),
                    Source = GroundPoseSource.LocalizedPlaneRaycast
                };
                return true;
            }

            return false;
        }

        private bool TryGetGroundCandidateFromTrackedPlanes(out GroundCandidate candidate)
        {
            candidate = default;

            if (planeManager == null || mainCamera == null)
            {
                return false;
            }

            bool found = false;
            float bestArea = 0f;

            foreach (ARPlane plane in planeManager.trackables)
            {
                if (plane == null || plane.alignment != PlaneAlignment.HorizontalUp)
                {
                    continue;
                }

                if (!IsValidGroundHit(plane.center, plane.normal))
                {
                    continue;
                }

                float area = plane.size.x * plane.size.y;
                if (area < minimumPlaneArea || area < bestArea)
                {
                    continue;
                }

                bestArea = area;
                found = true;
                candidate = new GroundCandidate
                {
                    Position = plane.center,
                    Normal = plane.normal.normalized,
                    Confidence = Mathf.Clamp01(0.55f + (area * 0.08f)),
                    Source = GroundPoseSource.TrackedFloorPlane
                };
            }

            return found;
        }

        private bool IsValidGroundHit(Vector3 point, Vector3 normal)
        {
            if (mainCamera == null)
            {
                return false;
            }

            Vector3 normalizedNormal = normal.sqrMagnitude > 0.0001f ? normal.normalized : Vector3.up;
            if (Vector3.Dot(normalizedNormal, Vector3.up) < floorNormalThreshold)
            {
                return false;
            }

            float heightBelowHead = mainCamera.transform.position.y - point.y;
            if (heightBelowHead < minimumGroundDrop || heightBelowHead > maximumGroundDrop)
            {
                return false;
            }

            return true;
        }

        private GroundPoseEstimate BuildHeadHeightFallback()
        {
            Vector3 position = mainCamera != null
                ? mainCamera.transform.position + (Vector3.down * fallbackStandingHeight)
                : Vector3.zero;

            return new GroundPoseEstimate
            {
                IsValid = true,
                Position = position,
                Normal = Vector3.up,
                EstimatedUserHeight = fallbackStandingHeight,
                Source = GroundPoseSource.EstimatedFromHeadHeight,
                SampleCount = 1,
                Confidence = 0.25f
            };
        }

        private void InitializeLocalizationFeature()
        {
            if (OpenXRSettings.Instance == null)
            {
                Debug.LogError("[MLSpace] OpenXRSettings.Instance is null. OpenXR may not be initialized yet.");
                status = SpaceLocalizationStatus.Failed;
                return;
            }

            localizationMapFeature = OpenXRSettings.Instance.GetFeature<MagicLeapLocalizationMapFeature>();
            if (localizationMapFeature == null)
            {
                Debug.LogError("[MLSpace] MagicLeapLocalizationMapFeature is not available in the active OpenXR settings.");
                status = SpaceLocalizationStatus.Failed;
                return;
            }

            if (!localizationMapFeature.enabled)
            {
                Debug.LogError("[MLSpace] Localization Map Feature found but not enabled in OpenXR settings.");
                status = SpaceLocalizationStatus.Failed;
                return;
            }

            XrResult result = localizationMapFeature.EnableLocalizationEvents(true);
            if (result != XrResult.Success)
            {
                Debug.LogError($"[MLSpace] Failed to enable localization events: {result}");
                status = SpaceLocalizationStatus.Failed;
                return;
            }

            MagicLeapLocalizationMapFeature.OnLocalizationChangedEvent += OnLocalizationChanged;
            status = SpaceLocalizationStatus.WaitingForLocalization;

            Debug.Log("[MLSpace] Waiting for Magic Leap Space localization...");
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

                    Debug.Log($"[MLSpace] Localized to \"{localizedSpaceName}\" with confidence {data.Confidence}.");
                    Debug.Log("[MLSpace] Look at the ground and press trigger to capture a localized floor pose.");
                    break;

                case LocalizationMapState.LocalizationPending:
                    status = SpaceLocalizationStatus.WaitingForLocalization;
                    Debug.Log("[MLSpace] Localization pending...");
                    break;

                case LocalizationMapState.SleepingBeforeRetry:
                    Debug.Log($"[MLSpace] Localization failed, retrying. Errors: {data.Errors}");
                    break;

                case LocalizationMapState.NotLocalized:
                    status = SpaceLocalizationStatus.WaitingForLocalization;
                    localizedSpaceName = string.Empty;
                    Debug.Log("[MLSpace] Not localized to any Space.");
                    break;
            }
        }

        private static Vector3 ProjectPointOntoPlane(Vector3 point, Vector3 planePoint, Vector3 planeNormal)
        {
            Vector3 normalizedNormal = planeNormal.sqrMagnitude > 0.0001f ? planeNormal.normalized : Vector3.up;
            float distance = Vector3.Dot(point - planePoint, normalizedNormal);
            return point - (normalizedNormal * distance);
        }

        private struct GroundCandidate
        {
            public Vector3 Position;
            public Vector3 Normal;
            public float Confidence;
            public GroundPoseSource Source;
        }
    }
}
