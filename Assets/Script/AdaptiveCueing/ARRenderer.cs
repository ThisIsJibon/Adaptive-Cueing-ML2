using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace AdaptiveCueing
{
    public enum GroundCalibrationState
    {
        Calibrating,
        Deployed
    }

    [DisallowMultipleComponent]
    public class ARRenderer : MonoBehaviour
    {
        [Header("Anchor")]
        [SerializeField] private Transform cueAnchor;
        [SerializeField] private Camera fallbackCamera;
        [SerializeField] private GameObject cuePrefab;
        [SerializeField] private LayerMask groundLayers = -1;
        [SerializeField, Min(0.1f)] private float groundProbeDistance = 10f;

        [Header("Ground Calibration")]
        [SerializeField, Min(1f)] private float calibrationDuration = 30f;
        [SerializeField, Min(0.05f)] private float sampleInterval = 0.1f;
        [SerializeField, Min(3)] private int minimumSamplesRequired = 10;
        [SerializeField] private bool showCalibrationProgress = true;
        [SerializeField] private bool waitForMLSpaces = true;
        [SerializeField, Min(1f)] private float fallbackStandingHeight = 1.6f;

        [Header("ML Space Detection")]
        [SerializeField] private MLSpaceGroundDetector mlSpaceDetector;

        [Header("Cue Appearance")]
        [SerializeField] private CuePresetId defaultCuePreset = CuePresetId.SteppingStones2D;
        [SerializeField, Min(0f)] private float hoverHeight = 0.015f;
        [SerializeField, Range(0f, 0.5f)] private float pulseDepth = 0f;
        [SerializeField] private bool alternateFeet = true;

        [Header("Ground Snapping")]
        [SerializeField] private bool snapPlacedCuesToPlanes = true;
        [SerializeField, Min(0.05f)] private float cueGroundSnapStartHeight = 0.45f;
        [SerializeField, Min(0.2f)] private float cueGroundSnapDistance = 2.5f;
        [SerializeField, Range(0.5f, 1f)] private float floorNormalThreshold = 0.8f;

        [Header("World Locking")]
        [SerializeField] private bool autoRecycleCues = false;
        [SerializeField, Min(0.1f)] private float recycleDistanceBehind = 0.3f;

        private static readonly List<ARRaycastHit> GroundRaycastHits = new List<ARRaycastHit>();

        private readonly List<CueVisual> cueVisuals = new List<CueVisual>();
        private readonly List<Renderer> rendererBuffer = new List<Renderer>();
        private readonly List<float> groundSamples = new List<float>();

        private Material runtimeCueMaterial;
        private MaterialPropertyBlock propertyBlock;
        private CuePresetDefinition activeCuePreset;

        private GroundCalibrationState calibrationState = GroundCalibrationState.Calibrating;
        private float calibrationStartTime;
        private float lastSampleTime;
        private float calibratedGroundHeight;
        private Vector3 calibratedGroundPoint;
        private Vector3 calibratedGroundNormal = Vector3.up;
        private float estimatedUserHeight;
        private Vector3 deploymentOrigin;
        private Vector3 deploymentForward;
        private int placedCueCount;
        private bool waitingForMLSpace = true;
        private bool manualCalibrationStarted;

        // Captured at first cue placement to ensure consistent spacing
        private float capturedSpacing;
        private float capturedDistanceAhead;
        private float capturedLateralOffset;
        private int capturedCueCount;

        private ARRaycastManager raycastManager;
        private ARPlaneManager planeManager;

        public CueState LatestCueState { get; private set; }
        public Vector3 LastPlacedCuePosition { get; private set; }
        public GroundCalibrationState CalibrationState => calibrationState;
        public float CalibrationProgress => manualCalibrationStarted ? Mathf.Clamp01((Time.time - calibrationStartTime) / calibrationDuration) : 0f;
        public int GroundSampleCount => groundSamples.Count;
        public float CalibratedGroundHeight => calibratedGroundHeight;
        public int PlacedCueCount => placedCueCount;
        public bool IsReadyToPlaceCues => calibrationState == GroundCalibrationState.Deployed;
        public CuePresetId SelectedCuePreset => activeCuePreset.Id;

        private void Awake()
        {
            propertyBlock = new MaterialPropertyBlock();
            activeCuePreset = CuePresetLibrary.Get(defaultCuePreset);
            ResolveCueAnchor();
            ResolveGroundManagers();
            estimatedUserHeight = fallbackStandingHeight;
        }

        private void OnEnable()
        {
            ResolveGroundManagers();

            if (mlSpaceDetector == null)
            {
                mlSpaceDetector = FindObjectOfType<MLSpaceGroundDetector>();
            }

            if (waitForMLSpaces && mlSpaceDetector != null)
            {
                waitingForMLSpace = true;
                manualCalibrationStarted = false;
                Debug.Log("[AdaptiveCueing] Waiting for ML Space ground detection...");
            }
            else
            {
                waitingForMLSpace = false;
                StartCalibration();
            }
        }

        private void Update()
        {
            if (waitingForMLSpace && mlSpaceDetector != null)
            {
                if (mlSpaceDetector.IsGroundSet)
                {
                    waitingForMLSpace = false;
                    return;
                }

                if (mlSpaceDetector.Status == SpaceLocalizationStatus.Failed || mlSpaceDetector.HasTimedOut)
                {
                    Debug.Log("[AdaptiveCueing] ML Space not available or timed out. Starting manual ground calibration...");
                    waitingForMLSpace = false;
                    StartCalibration();
                }
                return;
            }

            if (calibrationState == GroundCalibrationState.Calibrating && manualCalibrationStarted)
            {
                UpdateCalibration();
            }
        }

        public void SetAnchor(Transform anchor)
        {
            cueAnchor = anchor;
        }

        public void SetCuePreset(CuePresetId presetId)
        {
            activeCuePreset = CuePresetLibrary.Get(presetId);
            ResetCuePlacement();
        }

        public void StartCalibration()
        {
            calibrationState = GroundCalibrationState.Calibrating;
            calibrationStartTime = Time.time;
            lastSampleTime = 0f;
            groundSamples.Clear();
            placedCueCount = 0;
            manualCalibrationStarted = true;
            SetCueVisibility(0);

            if (showCalibrationProgress)
            {
                Debug.Log("[AdaptiveCueing] Manual ground calibration started. Look at the ground to calibrate.");
            }
        }

        public void ForceDeployNow()
        {
            if (groundSamples.Count >= minimumSamplesRequired)
            {
                FinalizeCalibrationAndDeploy();
            }
            else
            {
                Debug.LogWarning($"[AdaptiveCueing] Cannot deploy yet. Need at least {minimumSamplesRequired} ground samples, have {groundSamples.Count}. Keep looking at the ground.");
            }
        }

        public bool PlaceNextCue()
        {
            if (calibrationState != GroundCalibrationState.Deployed)
            {
                Debug.LogWarning("[AdaptiveCueing] Cannot place cue - calibration not complete.");
                return false;
            }

            Transform anchor = ResolveCueAnchor();
            if (anchor == null)
            {
                Debug.LogWarning("[AdaptiveCueing] Cannot place cue - no anchor/camera found.");
                return false;
            }

            if (placedCueCount == 0)
            {
                CapturePlacementParameters(anchor);
            }

            if (placedCueCount >= capturedCueCount)
            {
                Debug.Log("[AdaptiveCueing] All cues already placed.");
                return false;
            }

            EnsureCueCount(placedCueCount + 1);

            CueVisual cueVisual = cueVisuals[placedCueCount];
            float lateralOffset = CuePresetLibrary.GetLateralOffset(activeCuePreset, capturedLateralOffset);
            Vector3 right = ResolveRightVector(deploymentForward, calibratedGroundNormal);

            float side = activeCuePreset.AlternateFeet && alternateFeet ? (placedCueCount % 2 == 0 ? -1f : 1f) : 0f;
            float forwardDistance = capturedDistanceAhead + (capturedSpacing * placedCueCount);
            Vector3 targetGroundPoint = deploymentOrigin
                + (deploymentForward * forwardDistance)
                + (right * side * lateralOffset);

            GroundPoseEstimate groundPose = ResolveGroundPoseForCue(targetGroundPoint);
            Vector3 cueForward = ResolveProjectedForward(deploymentForward, groundPose.Normal);
            Vector3 cueScale = CuePresetLibrary.GetCueScale(activeCuePreset, capturedSpacing);
            Vector3 targetPosition = groundPose.Position + (groundPose.Normal * CuePresetLibrary.GetVerticalOffset(activeCuePreset, hoverHeight));
            Quaternion targetRotation = Quaternion.LookRotation(cueForward, groundPose.Normal);

            cueVisual.Transform.position = targetPosition;
            cueVisual.Transform.rotation = targetRotation;
            cueVisual.Transform.localScale = cueScale;
            cueVisual.WorldPosition = targetPosition;
            cueVisual.BaseColor = CuePresetLibrary.GetCueColor(activeCuePreset, placedCueCount);
            cueVisual.Initialized = true;
            cueVisual.Root.SetActive(true);

            placedCueCount++;
            LastPlacedCuePosition = targetPosition;

            Debug.Log($"[AdaptiveCueing] Placed cue {placedCueCount}/{capturedCueCount} at position: {targetPosition} with preset {activeCuePreset.DisplayName}");
            return true;
        }

        public void ClearAllCues()
        {
            placedCueCount = 0;
            capturedSpacing = 0f;
            capturedDistanceAhead = 0f;
            capturedLateralOffset = 0f;
            capturedCueCount = 0;
            SetCueVisibility(0);

            foreach (CueVisual cue in cueVisuals)
            {
                cue.Initialized = false;
            }

            Debug.Log("[AdaptiveCueing] All cues cleared. Next placement will use new head pose and preset.");
        }

        public void SetGroundHeightFromPlaneDetection(float groundHeight)
        {
            Transform anchor = ResolveCueAnchor();
            Vector3 groundPoint = anchor != null
                ? new Vector3(anchor.position.x, groundHeight, anchor.position.z)
                : new Vector3(0f, groundHeight, 0f);

            SetGroundPose(new GroundPoseEstimate
            {
                IsValid = true,
                Position = groundPoint,
                Normal = Vector3.up,
                EstimatedUserHeight = anchor != null ? Mathf.Max(1f, anchor.position.y - groundHeight) : fallbackStandingHeight,
                Source = GroundPoseSource.LocalizedPlaneRaycast,
                SampleCount = 1,
                Confidence = 0.8f
            });
        }

        public void SetGroundPose(GroundPoseEstimate groundPoseEstimate)
        {
            if (!groundPoseEstimate.IsValid)
            {
                Debug.LogWarning("[AdaptiveCueing] Ignoring invalid ground pose.");
                return;
            }

            calibratedGroundPoint = groundPoseEstimate.Position;
            calibratedGroundNormal = groundPoseEstimate.Normal.sqrMagnitude > 0.0001f
                ? groundPoseEstimate.Normal.normalized
                : Vector3.up;
            calibratedGroundHeight = calibratedGroundPoint.y;
            estimatedUserHeight = groundPoseEstimate.EstimatedUserHeight > 0.1f
                ? groundPoseEstimate.EstimatedUserHeight
                : fallbackStandingHeight;
            calibrationState = GroundCalibrationState.Deployed;
            waitingForMLSpace = false;
            manualCalibrationStarted = false;
            ClearAllCues();

            Transform anchor = ResolveCueAnchor();
            if (anchor != null)
            {
                deploymentOrigin = ProjectPointOntoPlane(anchor.position, calibratedGroundPoint, calibratedGroundNormal);
                deploymentForward = ResolveProjectedForward(anchor.forward, calibratedGroundNormal);
            }

            Debug.Log($"[AdaptiveCueing] === GROUND SET === Y={calibratedGroundHeight:F2}m | Normal={calibratedGroundNormal} | User height={estimatedUserHeight:F2}m");
        }

        public void SkipCalibration()
        {
            if (calibrationState == GroundCalibrationState.Calibrating)
            {
                FinalizeCalibrationAndDeploy();
            }
        }

        public void ResetCuePlacement()
        {
            ClearAllCues();
        }

        public void RenderCueState(CueState cueState, float currentTime, float deltaTime)
        {
            LatestCueState = cueState;

            if (calibrationState != GroundCalibrationState.Deployed)
            {
                return;
            }

            if (!cueState.IsValid)
            {
                return;
            }

            Transform anchor = ResolveCueAnchor();
            if (anchor == null)
            {
                return;
            }

            UpdatePlacedCuesVisuals(cueState, currentTime, anchor.position);
        }

        private void CapturePlacementParameters(Transform anchor)
        {
            if (LatestCueState.IsValid)
            {
                capturedSpacing = LatestCueState.Spacing;
                capturedDistanceAhead = LatestCueState.DistanceAhead;
                capturedLateralOffset = LatestCueState.LateralOffset;
                capturedCueCount = LatestCueState.CueCount;
            }
            else
            {
                capturedSpacing = 0.6f;
                capturedDistanceAhead = 0.5f;
                capturedLateralOffset = 0.15f;
                capturedCueCount = 8;
            }

            deploymentOrigin = ProjectPointOntoPlane(anchor.position, calibratedGroundPoint, calibratedGroundNormal);
            deploymentForward = ResolveProjectedForward(anchor.forward, calibratedGroundNormal);
            estimatedUserHeight = Mathf.Clamp(Vector3.Distance(anchor.position, deploymentOrigin), 1f, 2.25f);

            Debug.Log($"[AdaptiveCueing] Starting cue placement from {deploymentOrigin} using {activeCuePreset.DisplayName}.");
            Debug.Log($"[AdaptiveCueing] Ground point: {calibratedGroundPoint} | Ground normal: {calibratedGroundNormal}");
            Debug.Log($"[AdaptiveCueing] Captured spacing {capturedSpacing:F2}m, count {capturedCueCount}, estimated user height {estimatedUserHeight:F2}m");
        }

        private void UpdateCalibration()
        {
            Transform anchor = ResolveCueAnchor();
            if (anchor == null)
            {
                return;
            }

            if (Time.time - lastSampleTime >= sampleInterval)
            {
                TryCollectGroundSample(anchor);
                lastSampleTime = Time.time;
            }

            float elapsed = Time.time - calibrationStartTime;
            bool hasEnoughSamples = groundSamples.Count >= minimumSamplesRequired;

            if (elapsed >= calibrationDuration && hasEnoughSamples)
            {
                FinalizeCalibrationAndDeploy();
            }

            if (showCalibrationProgress && Time.frameCount % 60 == 0)
            {
                string status = hasEnoughSamples ? "Ready to deploy!" : "Look at the ground...";
                Debug.Log($"[AdaptiveCueing] Calibrating... {CalibrationProgress * 100f:F0}% | Samples: {groundSamples.Count} | {status}");
            }
        }

        private void TryCollectGroundSample(Transform anchor)
        {
            Vector3 rayOrigin = anchor.position;
            Vector3 rayDirection = anchor.forward;

            if (Physics.Raycast(rayOrigin, rayDirection, out RaycastHit hit, groundProbeDistance, groundLayers, QueryTriggerInteraction.Ignore))
            {
                float hitHeightBelowHead = anchor.position.y - hit.point.y;
                if (hitHeightBelowHead > 0.3f)
                {
                    groundSamples.Add(hit.point.y);

                    if (showCalibrationProgress && groundSamples.Count <= 5)
                    {
                        Debug.Log($"[AdaptiveCueing] Ground sample collected at Y={hit.point.y:F2}");
                    }
                }
            }
            else if (Physics.Raycast(anchor.position, Vector3.down, out RaycastHit downHit, groundProbeDistance, groundLayers, QueryTriggerInteraction.Ignore))
            {
                groundSamples.Add(downHit.point.y);
            }
        }

        private void FinalizeCalibrationAndDeploy()
        {
            Transform anchor = ResolveCueAnchor();
            if (anchor == null)
            {
                Debug.LogError("[AdaptiveCueing] No camera/anchor found. Cannot deploy cues.");
                return;
            }

            calibratedGroundNormal = Vector3.up;

            if (groundSamples.Count > 0)
            {
                groundSamples.Sort();
                int medianIndex = groundSamples.Count / 2;
                calibratedGroundHeight = groundSamples[medianIndex];
                Debug.Log($"[AdaptiveCueing] Ground detected from {groundSamples.Count} raycast samples.");
            }
            else
            {
                calibratedGroundHeight = anchor.position.y - fallbackStandingHeight;
                Debug.LogWarning($"[AdaptiveCueing] No raycast hits. Using estimated ground (head height - {fallbackStandingHeight:F1}m).");
            }

            calibratedGroundPoint = new Vector3(anchor.position.x, calibratedGroundHeight, anchor.position.z);
            deploymentOrigin = ProjectPointOntoPlane(anchor.position, calibratedGroundPoint, calibratedGroundNormal);
            deploymentForward = ResolveProjectedForward(anchor.forward, calibratedGroundNormal);
            estimatedUserHeight = Mathf.Max(1f, anchor.position.y - calibratedGroundHeight);

            calibrationState = GroundCalibrationState.Deployed;
            placedCueCount = 0;

            Debug.Log($"[AdaptiveCueing] === CALIBRATION COMPLETE === Ground Y: {calibratedGroundHeight:F2}m. Press trigger to place cues!");
        }

        private void UpdatePlacedCuesVisuals(CueState cueState, float currentTime, Vector3 anchorPosition)
        {
            Vector3 right = ResolveRightVector(deploymentForward, calibratedGroundNormal);

            for (int index = 0; index < placedCueCount && index < cueVisuals.Count; index++)
            {
                CueVisual cueVisual = cueVisuals[index];

                if (!cueVisual.Root.activeSelf)
                {
                    continue;
                }

                if (autoRecycleCues)
                {
                    Vector3 userGroundPos = ProjectPointOntoPlane(anchorPosition, calibratedGroundPoint, calibratedGroundNormal);
                    Vector3 cueGroundPos = ProjectPointOntoPlane(cueVisual.Transform.position, calibratedGroundPoint, calibratedGroundNormal);
                    Vector3 toCue = cueGroundPos - userGroundPos;
                    float distanceAlongForward = Vector3.Dot(toCue, deploymentForward);

                    if (distanceAlongForward < -recycleDistanceBehind)
                    {
                        RecycleCueToFront(cueVisual, index, cueState, right);
                    }
                }

                float pulse = 1f + (pulseDepth * Mathf.Sin((currentTime + (index * 0.12f)) * cueState.PulseRate * Mathf.PI * 2f));
                UpdateCueVisual(cueVisual, cueState, pulse);
            }
        }

        private void RecycleCueToFront(CueVisual cueVisual, int cueIndex, CueState cueState, Vector3 right)
        {
            float maxDistance = 0f;
            for (int index = 0; index < placedCueCount && index < cueVisuals.Count; index++)
            {
                if (!cueVisuals[index].Root.activeSelf)
                {
                    continue;
                }

                Vector3 cuePos = cueVisuals[index].Transform.position;
                Vector3 toOtherCue = cuePos - deploymentOrigin;
                float distance = Vector3.Dot(toOtherCue, deploymentForward);
                if (distance > maxDistance)
                {
                    maxDistance = distance;
                }
            }

            float lateralOffset = CuePresetLibrary.GetLateralOffset(activeCuePreset, cueState.LateralOffset);
            float side = activeCuePreset.AlternateFeet && alternateFeet ? (cueIndex % 2 == 0 ? -1f : 1f) : 0f;
            float newForwardDistance = maxDistance + cueState.Spacing;
            Vector3 targetGroundPoint = deploymentOrigin
                + (deploymentForward * newForwardDistance)
                + (right * side * lateralOffset);

            GroundPoseEstimate groundPose = ResolveGroundPoseForCue(targetGroundPoint);
            Vector3 cueForward = ResolveProjectedForward(deploymentForward, groundPose.Normal);

            cueVisual.Transform.position = groundPose.Position + (groundPose.Normal * CuePresetLibrary.GetVerticalOffset(activeCuePreset, hoverHeight));
            cueVisual.Transform.rotation = Quaternion.LookRotation(cueForward, groundPose.Normal);
            cueVisual.Transform.localScale = CuePresetLibrary.GetCueScale(activeCuePreset, cueState.Spacing);
            cueVisual.WorldPosition = cueVisual.Transform.position;
        }

        private Transform ResolveCueAnchor()
        {
            if (cueAnchor != null)
            {
                return cueAnchor;
            }

            if (fallbackCamera == null)
            {
                fallbackCamera = Camera.main;

                if (fallbackCamera == null)
                {
                    Camera[] cameras = FindObjectsOfType<Camera>();

                    if (cameras.Length > 0)
                    {
                        fallbackCamera = cameras[0];
                    }
                }
            }

            cueAnchor = fallbackCamera != null ? fallbackCamera.transform : null;
            return cueAnchor;
        }

        private void ResolveGroundManagers()
        {
            if (raycastManager == null)
            {
                raycastManager = FindObjectOfType<ARRaycastManager>();
            }

            if (planeManager == null)
            {
                planeManager = FindObjectOfType<ARPlaneManager>();
            }
        }

        private void EnsureCueCount(int desiredCount)
        {
            while (cueVisuals.Count < desiredCount)
            {
                cueVisuals.Add(CreateCueVisual(cueVisuals.Count));
            }
        }

        private CueVisual CreateCueVisual(int index)
        {
            GameObject root;

            if (cuePrefab != null)
            {
                root = Instantiate(cuePrefab, transform);
                root.name = $"AdaptiveCue_{index:00}";
            }
            else
            {
                root = GameObject.CreatePrimitive(PrimitiveType.Cube);
                root.name = $"AdaptiveCue_{index:00}";
                root.transform.SetParent(transform, false);

                Collider collider = root.GetComponent<Collider>();
                if (collider != null)
                {
                    Destroy(collider);
                }

                Renderer primitiveRenderer = root.GetComponent<Renderer>();
                if (primitiveRenderer != null)
                {
                    primitiveRenderer.sharedMaterial = GetOrCreateRuntimeMaterial();
                }
            }

            rendererBuffer.Clear();
            rendererBuffer.AddRange(root.GetComponentsInChildren<Renderer>(true));

            return new CueVisual
            {
                Root = root,
                Transform = root.transform,
                Renderers = rendererBuffer.ToArray(),
                BaseColor = activeCuePreset.PrimaryColor,
                Initialized = false
            };
        }

        private Material GetOrCreateRuntimeMaterial()
        {
            if (runtimeCueMaterial != null)
            {
                return runtimeCueMaterial;
            }

            Shader shader = Shader.Find("Standard");
            if (shader == null)
            {
                shader = Shader.Find("Universal Render Pipeline/Lit");
            }
            if (shader == null)
            {
                shader = Shader.Find("Sprites/Default");
            }

            runtimeCueMaterial = new Material(shader)
            {
                name = "AdaptiveCueRuntimeMaterial"
            };

            if (runtimeCueMaterial.HasProperty("_Glossiness"))
            {
                runtimeCueMaterial.SetFloat("_Glossiness", 0f);
            }

            return runtimeCueMaterial;
        }

        private GroundPoseEstimate ResolveGroundPoseForCue(Vector3 targetGroundPoint)
        {
            ResolveGroundManagers();

            if (snapPlacedCuesToPlanes && raycastManager != null)
            {
                Vector3 probeOrigin = targetGroundPoint + (calibratedGroundNormal * cueGroundSnapStartHeight);
                Ray ray = new Ray(probeOrigin, -calibratedGroundNormal);

                if (raycastManager.Raycast(ray, GroundRaycastHits, TrackableType.PlaneWithinPolygon))
                {
                    for (int index = 0; index < GroundRaycastHits.Count; index++)
                    {
                        ARRaycastHit hit = GroundRaycastHits[index];
                        if (hit.distance > cueGroundSnapDistance)
                        {
                            continue;
                        }

                        Vector3 hitNormal = hit.pose.up.sqrMagnitude > 0.0001f ? hit.pose.up.normalized : Vector3.up;
                        if (Vector3.Dot(hitNormal, Vector3.up) < floorNormalThreshold)
                        {
                            continue;
                        }

                        return new GroundPoseEstimate
                        {
                            IsValid = true,
                            Position = hit.pose.position,
                            Normal = hitNormal,
                            EstimatedUserHeight = estimatedUserHeight,
                            Source = GroundPoseSource.LocalizedPlaneRaycast,
                            SampleCount = 1,
                            Confidence = 0.85f
                        };
                    }
                }
            }

            Vector3 fallbackPoint = ProjectPointOntoPlane(targetGroundPoint, calibratedGroundPoint, calibratedGroundNormal);
            return new GroundPoseEstimate
            {
                IsValid = true,
                Position = fallbackPoint,
                Normal = calibratedGroundNormal,
                EstimatedUserHeight = estimatedUserHeight,
                Source = GroundPoseSource.ManualCalibration,
                SampleCount = 1,
                Confidence = 0.55f
            };
        }

        private void UpdateCueVisual(CueVisual cueVisual, CueState cueState, float pulse)
        {
            if (cueVisual.Renderers == null)
            {
                return;
            }

            Color baseColor = cueVisual.BaseColor.a > 0f ? cueVisual.BaseColor : activeCuePreset.PrimaryColor;
            Color surfaceColor = baseColor * (cueState.Brightness * pulse);
            Color emissionColor = baseColor * Mathf.Max(0f, (cueState.Brightness * pulse) - 0.12f);

            for (int index = 0; index < cueVisual.Renderers.Length; index++)
            {
                Renderer rendererComponent = cueVisual.Renderers[index];
                if (rendererComponent == null)
                {
                    continue;
                }

                propertyBlock.Clear();
                propertyBlock.SetColor("_Color", surfaceColor);
                propertyBlock.SetColor("_BaseColor", surfaceColor);
                propertyBlock.SetColor("_EmissionColor", emissionColor);
                rendererComponent.SetPropertyBlock(propertyBlock);
            }
        }

        private void SetCueVisibility(int visibleCount)
        {
            for (int index = 0; index < cueVisuals.Count; index++)
            {
                cueVisuals[index].Root.SetActive(index < visibleCount);
            }
        }

        private static Vector3 ProjectPointOntoPlane(Vector3 point, Vector3 planePoint, Vector3 planeNormal)
        {
            Vector3 normalizedNormal = planeNormal.sqrMagnitude > 0.0001f ? planeNormal.normalized : Vector3.up;
            float distance = Vector3.Dot(point - planePoint, normalizedNormal);
            return point - (normalizedNormal * distance);
        }

        private static Vector3 ResolveProjectedForward(Vector3 forward, Vector3 normal)
        {
            Vector3 projectedForward = Vector3.ProjectOnPlane(forward, normal);
            if (projectedForward.sqrMagnitude < 0.001f)
            {
                projectedForward = Vector3.ProjectOnPlane(Vector3.forward, normal);
            }

            if (projectedForward.sqrMagnitude < 0.001f)
            {
                projectedForward = Vector3.forward;
            }

            return projectedForward.normalized;
        }

        private static Vector3 ResolveRightVector(Vector3 forward, Vector3 normal)
        {
            Vector3 right = Vector3.Cross(normal, forward);
            if (right.sqrMagnitude < 0.001f)
            {
                right = Vector3.Cross(Vector3.up, forward);
            }

            if (right.sqrMagnitude < 0.001f)
            {
                right = Vector3.right;
            }

            return right.normalized;
        }

        private sealed class CueVisual
        {
            public GameObject Root;
            public Transform Transform;
            public Renderer[] Renderers;
            public Color BaseColor;
            public bool Initialized;
            public Vector3 WorldPosition;
        }
    }
}
