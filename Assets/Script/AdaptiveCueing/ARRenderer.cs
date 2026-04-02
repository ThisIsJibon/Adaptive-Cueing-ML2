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

        [Header("Cue Appearance")]
        [SerializeField] private Vector3 cueScale = new Vector3(0.18f, 0.02f, 0.28f);
        [SerializeField, Min(0f)] private float hoverHeight = 0.015f;
        [SerializeField, Range(0f, 0.5f)] private float pulseDepth = 0.25f;
        [SerializeField] private bool alternateFeet = true;

        [Header("World Locking")]
        [SerializeField, Min(0.1f)] private float recycleDistanceBehind = 0.3f;

        private readonly List<CueVisual> cueVisuals = new List<CueVisual>();
        private readonly List<Renderer> rendererBuffer = new List<Renderer>();
        private readonly List<float> groundSamples = new List<float>();

        private Material runtimeCueMaterial;
        private MaterialPropertyBlock propertyBlock;
        private Vector3 lastForward = Vector3.forward;
        private Vector3 lastPlacementForward;
        private bool cuesPlaced;

        private GroundCalibrationState calibrationState = GroundCalibrationState.Calibrating;
        private float calibrationStartTime;
        private float lastSampleTime;
        private float calibratedGroundHeight;
        private Vector3 deploymentOrigin;
        private Vector3 deploymentForward;

        public CueState LatestCueState { get; private set; }
        public GroundCalibrationState CalibrationState => calibrationState;
        public float CalibrationProgress => Mathf.Clamp01((Time.time - calibrationStartTime) / calibrationDuration);
        public int GroundSampleCount => groundSamples.Count;
        public float CalibratedGroundHeight => calibratedGroundHeight;

        private void Awake()
        {
            propertyBlock = new MaterialPropertyBlock();
            ResolveCueAnchor();
        }

        private void OnEnable()
        {
            StartCalibration();
        }

        private void Update()
        {
            if (calibrationState == GroundCalibrationState.Calibrating)
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
            cuesPlaced = false;
            SetCueVisibility(0);

            if (showCalibrationProgress)
            {
                Debug.Log("[AdaptiveCueing] Ground calibration started. Look at the ground to calibrate.");
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

            if (groundSamples.Count == 0)
            {
                Debug.LogError("[AdaptiveCueing] No ground samples collected. Make sure there's a plane with a collider and look at the ground.");
                return;
            }

            groundSamples.Sort();
            int medianIndex = groundSamples.Count / 2;
            calibratedGroundHeight = groundSamples[medianIndex];

            deploymentOrigin = new Vector3(anchor.position.x, calibratedGroundHeight, anchor.position.z);
            deploymentForward = Vector3.ProjectOnPlane(anchor.forward, Vector3.up).normalized;
            if (deploymentForward.sqrMagnitude < 0.001f)
            {
                deploymentForward = Vector3.forward;
            }

            calibrationState = GroundCalibrationState.Deployed;
            cuesPlaced = false;

            Debug.Log($"[AdaptiveCueing] Calibration complete! Ground height: {calibratedGroundHeight:F2}m from {groundSamples.Count} samples.");
        }

        public void ResetCuePlacement()
        {
            cuesPlaced = false;
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
                SetCueVisibility(0);
                return;
            }

            if (!cueState.IsValid)
            {
                SetCueVisibility(0);
                cuesPlaced = false;
                return;
            }

            Transform anchor = ResolveCueAnchor();

            if (anchor == null)
            {
                SetCueVisibility(0);
                cuesPlaced = false;
                return;
            }

            EnsureCueCount(Mathf.Max(1, cueState.CueCount));

            GroundPose baseGroundPose = new GroundPose(
                new Vector3(anchor.position.x, calibratedGroundHeight, anchor.position.z),
                Vector3.up);

            Vector3 right = Vector3.Cross(Vector3.up, deploymentForward).normalized;

            RenderWorldLockedCues(cueState, currentTime, anchor.position, baseGroundPose, deploymentForward, right);
        }

        private void RenderWorldLockedCues(CueState cueState, float currentTime, Vector3 anchorPosition,
            GroundPose baseGroundPose, Vector3 flatForward, Vector3 right)
        {
            if (!cuesPlaced)
            {
                PlaceAllCuesAhead(cueState, baseGroundPose, flatForward, right);
                lastPlacementForward = flatForward;
                cuesPlaced = true;
            }

            Vector3 userGroundPos = new Vector3(anchorPosition.x, baseGroundPose.Position.y, anchorPosition.z);

            for (int index = 0; index < cueVisuals.Count; index++)
            {
                CueVisual cueVisual = cueVisuals[index];
                bool shouldDisplay = index < cueState.CueCount;
                cueVisual.Root.SetActive(shouldDisplay);

                if (!shouldDisplay)
                {
                    continue;
                }

                Vector3 cueGroundPos = new Vector3(
                    cueVisual.Transform.position.x,
                    baseGroundPose.Position.y,
                    cueVisual.Transform.position.z);

                Vector3 toCue = cueGroundPos - userGroundPos;
                float distanceAlongForward = Vector3.Dot(toCue, flatForward);

                if (distanceAlongForward < -recycleDistanceBehind)
                {
                    RecycleCueToFront(cueVisual, index, cueState, baseGroundPose, flatForward, right);
                }

                cueVisual.Transform.localScale = new Vector3(cueScale.x, cueScale.y, Mathf.Max(cueScale.z, cueState.Spacing * 0.55f));

                float pulse = 1f + (pulseDepth * Mathf.Sin((currentTime + (index * 0.12f)) * cueState.PulseRate * Mathf.PI * 2f));
                UpdateCueVisual(cueVisual, cueState, pulse);
            }
        }

        private void PlaceAllCuesAhead(CueState cueState, GroundPose baseGroundPose, Vector3 flatForward, Vector3 right)
        {
            for (int index = 0; index < cueVisuals.Count; index++)
            {
                CueVisual cueVisual = cueVisuals[index];

                if (index >= cueState.CueCount)
                {
                    cueVisual.Root.SetActive(false);
                    continue;
                }

                PlaceCueAtIndex(cueVisual, index, cueState, baseGroundPose, flatForward, right);
                cueVisual.Root.SetActive(true);
            }
        }

        private void PlaceCueAtIndex(CueVisual cueVisual, int index, CueState cueState,
            GroundPose baseGroundPose, Vector3 flatForward, Vector3 right)
        {
            float side = alternateFeet ? (index % 2 == 0 ? -1f : 1f) : 0f;
            float forwardDistance = cueState.DistanceAhead + (cueState.Spacing * index);

            Vector3 targetPosition = deploymentOrigin
                + (flatForward * forwardDistance)
                + (right * side * cueState.LateralOffset)
                + (Vector3.up * hoverHeight);

            Quaternion targetRotation = Quaternion.LookRotation(flatForward, Vector3.up);

            cueVisual.Transform.position = targetPosition;
            cueVisual.Transform.rotation = targetRotation;
            cueVisual.WorldPosition = targetPosition;
            cueVisual.Initialized = true;
        }

        private void RecycleCueToFront(CueVisual cueVisual, int cueIndex, CueState cueState,
            GroundPose baseGroundPose, Vector3 flatForward, Vector3 right)
        {
            float maxDistance = 0f;
            for (int i = 0; i < cueVisuals.Count && i < cueState.CueCount; i++)
            {
                Vector3 cuePos = cueVisuals[i].Transform.position;
                Vector3 toOtherCue = cuePos - deploymentOrigin;
                float dist = Vector3.Dot(toOtherCue, flatForward);
                if (dist > maxDistance)
                {
                    maxDistance = dist;
                }
            }

            float side = alternateFeet ? (cueIndex % 2 == 0 ? -1f : 1f) : 0f;
            float newForwardDistance = maxDistance + cueState.Spacing;

            Vector3 targetPosition = deploymentOrigin
                + (flatForward * newForwardDistance)
                + (right * side * cueState.LateralOffset)
                + (Vector3.up * hoverHeight);

            Quaternion targetRotation = Quaternion.LookRotation(flatForward, Vector3.up);

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
