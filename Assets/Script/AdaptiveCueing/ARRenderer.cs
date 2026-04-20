using System.Collections.Generic;
using UnityEngine;

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

        [Header("ML Space Detection")]
        [SerializeField] private MLSpaceGroundDetector mlSpaceDetector;

        [Header("Cue Appearance")]
        [SerializeField] private Vector3 cueScale = new Vector3(0.18f, 0.02f, 0.28f);
        [SerializeField, Min(0f)] private float hoverHeight = 0.015f;
        [SerializeField, Range(0f, 0.5f)] private float pulseDepth = 0f;
        [SerializeField] private bool alternateFeet = true;

        [Header("World Locking")]
        [SerializeField] private bool autoRecycleCues = false;
        [SerializeField, Min(0.1f)] private float recycleDistanceBehind = 0.3f;

        private readonly List<CueVisual> cueVisuals = new List<CueVisual>();
        private readonly List<Renderer> rendererBuffer = new List<Renderer>();
        private readonly List<float> groundSamples = new List<float>();

        private Material runtimeCueMaterial;
        private MaterialPropertyBlock propertyBlock;

        private GroundCalibrationState calibrationState = GroundCalibrationState.Calibrating;
        private float calibrationStartTime;
        private float lastSampleTime;
        private float calibratedGroundHeight;
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

        public CueState LatestCueState { get; private set; }
        public Vector3 LastPlacedCuePosition { get; private set; }
        public GroundCalibrationState CalibrationState => calibrationState;
        public float CalibrationProgress => manualCalibrationStarted ? Mathf.Clamp01((Time.time - calibrationStartTime) / calibrationDuration) : 0f;
        public int GroundSampleCount => groundSamples.Count;
        public float CalibratedGroundHeight => calibratedGroundHeight;
        public int PlacedCueCount => placedCueCount;
        public bool IsReadyToPlaceCues => calibrationState == GroundCalibrationState.Deployed;

        private void Awake()
        {
            propertyBlock = new MaterialPropertyBlock();
            ResolveCueAnchor();
        }

        private void OnEnable()
        {
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
                // Wait for ML Space to set ground via trigger press
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

            // On first cue, capture spacing values so they stay consistent
            if (placedCueCount == 0)
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
                    // Use sensible defaults if no valid state
                    capturedSpacing = 0.6f;
                    capturedDistanceAhead = 0.5f;
                    capturedLateralOffset = 0.15f;
                    capturedCueCount = 8;
                }

                Vector3 flatForward = Vector3.ProjectOnPlane(anchor.forward, Vector3.up).normalized;
                if (flatForward.sqrMagnitude < 0.001f)
                {
                    flatForward = Vector3.forward;
                }
                deploymentForward = flatForward;
                deploymentOrigin = new Vector3(anchor.position.x, calibratedGroundHeight, anchor.position.z);
                
                Debug.Log($"[AdaptiveCueing] Starting cue placement:");
                Debug.Log($"[AdaptiveCueing]   Head position: {anchor.position}");
                Debug.Log($"[AdaptiveCueing]   Calibrated ground Y: {calibratedGroundHeight:F3}m");
                Debug.Log($"[AdaptiveCueing]   Deployment origin: {deploymentOrigin}");
                Debug.Log($"[AdaptiveCueing]   Direction: {deploymentForward}");
                Debug.Log($"[AdaptiveCueing]   Spacing: {capturedSpacing:F2}m, Cue count: {capturedCueCount}");
            }

            if (placedCueCount >= capturedCueCount)
            {
                Debug.Log("[AdaptiveCueing] All cues already placed.");
                return false;
            }

            EnsureCueCount(placedCueCount + 1);

            CueVisual cueVisual = cueVisuals[placedCueCount];
            Vector3 right = Vector3.Cross(Vector3.up, deploymentForward).normalized;

            float side = alternateFeet ? (placedCueCount % 2 == 0 ? -1f : 1f) : 0f;
            float forwardDistance = capturedDistanceAhead + (capturedSpacing * placedCueCount);

            Vector3 targetPosition = deploymentOrigin
                + (deploymentForward * forwardDistance)
                + (right * side * capturedLateralOffset)
                + (Vector3.up * hoverHeight);

            Quaternion targetRotation = Quaternion.LookRotation(deploymentForward, Vector3.up);

            cueVisual.Transform.position = targetPosition;
            cueVisual.Transform.rotation = targetRotation;
            cueVisual.Transform.localScale = new Vector3(cueScale.x, cueScale.y, Mathf.Max(cueScale.z, capturedSpacing * 0.55f));
            cueVisual.WorldPosition = targetPosition;
            cueVisual.Initialized = true;
            cueVisual.Root.SetActive(true);

            placedCueCount++;
            LastPlacedCuePosition = targetPosition;

            Debug.Log($"[AdaptiveCueing] Placed cue {placedCueCount}/{capturedCueCount} at position: {targetPosition} (spacing: {capturedSpacing:F2}m)");
            return true;
        }

        public void ClearAllCues()
        {
            placedCueCount = 0;
            capturedSpacing = 0;
            capturedDistanceAhead = 0;
            capturedLateralOffset = 0;
            capturedCueCount = 0;
            SetCueVisibility(0);
            Debug.Log("[AdaptiveCueing] All cues cleared. Next placement will use new head pose and spacing.");
        }

        public void SetGroundHeightFromPlaneDetection(float groundHeight)
        {
            calibratedGroundHeight = groundHeight;
            calibrationState = GroundCalibrationState.Deployed;
            placedCueCount = 0;
            waitingForMLSpace = false;
            manualCalibrationStarted = false;

            Transform anchor = ResolveCueAnchor();
            if (anchor != null)
            {
                deploymentOrigin = new Vector3(anchor.position.x, calibratedGroundHeight, anchor.position.z);
                deploymentForward = Vector3.ProjectOnPlane(anchor.forward, Vector3.up).normalized;
                if (deploymentForward.sqrMagnitude < 0.001f)
                {
                    deploymentForward = Vector3.forward;
                }
            }

            Debug.Log($"[AdaptiveCueing] === GROUND SET from ML Space: Y={groundHeight:F2}m === Press trigger to place cues!");
        }

        public void SkipCalibration()
        {
            if (calibrationState == GroundCalibrationState.Calibrating)
            {
                FinalizeCalibrationAndDeploy();
            }
        }

        private void UpdateCalibration()
        {
            Transform anchor = ResolveCueAnchor();
            if (anchor == null) return;

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
            else
            {
                Vector3 downRayOrigin = anchor.position;
                if (Physics.Raycast(downRayOrigin, Vector3.down, out RaycastHit downHit, groundProbeDistance, groundLayers, QueryTriggerInteraction.Ignore))
                {
                    groundSamples.Add(downHit.point.y);
                }
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

            if (groundSamples.Count > 0)
            {
                groundSamples.Sort();
                int medianIndex = groundSamples.Count / 2;
                calibratedGroundHeight = groundSamples[medianIndex];
                Debug.Log($"[AdaptiveCueing] Ground detected from {groundSamples.Count} raycast samples.");
            }
            else
            {
                // Fallback: estimate ground from head height (assume 1.6m standing height)
                calibratedGroundHeight = anchor.position.y - 1.6f;
                Debug.LogWarning($"[AdaptiveCueing] No raycast hits. Using estimated ground (head height - 1.6m).");
            }

            deploymentOrigin = new Vector3(anchor.position.x, calibratedGroundHeight, anchor.position.z);
            deploymentForward = Vector3.ProjectOnPlane(anchor.forward, Vector3.up).normalized;
            if (deploymentForward.sqrMagnitude < 0.001f)
            {
                deploymentForward = Vector3.forward;
            }

            calibrationState = GroundCalibrationState.Deployed;
            placedCueCount = 0;

            Debug.Log($"[AdaptiveCueing] === CALIBRATION COMPLETE === Ground Y: {calibratedGroundHeight:F2}m. Press trigger to place cues!");
        }

        public void ResetCuePlacement()
        {
            placedCueCount = 0;
            capturedSpacing = 0;
            capturedDistanceAhead = 0;
            capturedLateralOffset = 0;
            capturedCueCount = 0;
            SetCueVisibility(0);
            foreach (var cue in cueVisuals)
            {
                cue.Initialized = false;
            }
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

        private void UpdatePlacedCuesVisuals(CueState cueState, float currentTime, Vector3 anchorPosition)
        {
            Vector3 right = Vector3.Cross(Vector3.up, deploymentForward).normalized;

            for (int index = 0; index < placedCueCount && index < cueVisuals.Count; index++)
            {
                CueVisual cueVisual = cueVisuals[index];

                if (!cueVisual.Root.activeSelf)
                {
                    continue;
                }

                if (autoRecycleCues)
                {
                    Vector3 userGroundPos = new Vector3(anchorPosition.x, calibratedGroundHeight, anchorPosition.z);
                    Vector3 cueGroundPos = new Vector3(
                        cueVisual.Transform.position.x,
                        calibratedGroundHeight,
                        cueVisual.Transform.position.z);

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
            for (int i = 0; i < placedCueCount && i < cueVisuals.Count; i++)
            {
                if (!cueVisuals[i].Root.activeSelf) continue;
                
                Vector3 cuePos = cueVisuals[i].Transform.position;
                Vector3 toOtherCue = cuePos - deploymentOrigin;
                float dist = Vector3.Dot(toOtherCue, deploymentForward);
                if (dist > maxDistance)
                {
                    maxDistance = dist;
                }
            }

            float side = alternateFeet ? (cueIndex % 2 == 0 ? -1f : 1f) : 0f;
            float newForwardDistance = maxDistance + cueState.Spacing;

            Vector3 targetPosition = deploymentOrigin
                + (deploymentForward * newForwardDistance)
                + (right * side * cueState.LateralOffset)
                + (Vector3.up * hoverHeight);

            Quaternion targetRotation = Quaternion.LookRotation(deploymentForward, Vector3.up);

            cueVisual.Transform.position = targetPosition;
            cueVisual.Transform.rotation = targetRotation;
            cueVisual.WorldPosition = targetPosition;
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

        private void UpdateCueVisual(CueVisual cueVisual, CueState cueState, float pulse)
        {
            if (cueVisual.Renderers == null)
            {
                return;
            }

            Color surfaceColor = cueState.Tint * (cueState.Brightness * pulse);
            Color emissionColor = cueState.Tint * Mathf.Max(0f, (cueState.Brightness * pulse) - 0.15f);

            foreach (Renderer rendererComponent in cueVisual.Renderers)
            {
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

        private sealed class CueVisual
        {
            public GameObject Root;
            public Transform Transform;
            public Renderer[] Renderers;
            public bool Initialized;
            public Vector3 WorldPosition;
        }

        private readonly struct GroundPose
        {
            public GroundPose(Vector3 position, Vector3 normal)
            {
                Position = position;
                Normal = normal.sqrMagnitude > 0.0001f ? normal.normalized : Vector3.up;
            }

            public Vector3 Position { get; }

            public Vector3 Normal { get; }
        }
    }
}
