using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveCueing
{
    [DisallowMultipleComponent]
    public class ARRenderer : MonoBehaviour
    {
        [Header("Anchor")]
        [SerializeField] private Transform cueAnchor;
        [SerializeField] private Camera fallbackCamera;
        [SerializeField] private GameObject cuePrefab;
        [SerializeField] private bool usePhysicsGrounding = true;
        [SerializeField] private LayerMask groundLayers = -1;
        [SerializeField] private float groundHeight = 0f;
        [SerializeField, Min(0.1f)] private float groundProbeHeight = 1.5f;
        [SerializeField, Min(0.1f)] private float groundProbeDistance = 4f;

        [Header("Cue Appearance")]
        [SerializeField] private Vector3 cueScale = new Vector3(0.18f, 0.02f, 0.28f);
        [SerializeField, Min(0f)] private float hoverHeight = 0.015f;
        [SerializeField, Range(1f, 30f)] private float transformSmoothing = 12f;
        [SerializeField, Range(0f, 0.5f)] private float pulseDepth = 0.25f;
        [SerializeField] private bool alternateFeet = true;

        private readonly List<CueVisual> cueVisuals = new List<CueVisual>();
        private readonly List<Renderer> rendererBuffer = new List<Renderer>();

        private Material runtimeCueMaterial;
        private MaterialPropertyBlock propertyBlock;
        private Vector3 lastForward = Vector3.forward;

        public CueState LatestCueState { get; private set; }

        private void Awake()
        {
            propertyBlock = new MaterialPropertyBlock();
            ResolveCueAnchor();
        }

        public void SetAnchor(Transform anchor)
        {
            cueAnchor = anchor;
        }

        public void RenderCueState(CueState cueState, float currentTime, float deltaTime)
        {
            LatestCueState = cueState;

            if (!cueState.IsValid)
            {
                SetCueVisibility(0);
                return;
            }

            Transform anchor = ResolveCueAnchor();

            if (anchor == null)
            {
                SetCueVisibility(0);
                return;
            }

            EnsureCueCount(Mathf.Max(1, cueState.CueCount));

            GroundPose baseGroundPose = ResolveGroundPose(anchor.position);
            Vector3 flatForward = Vector3.ProjectOnPlane(anchor.forward, baseGroundPose.Normal);

            if (flatForward.sqrMagnitude < 0.0001f)
            {
                flatForward = lastForward;
            }
            else
            {
                flatForward.Normalize();
                lastForward = flatForward;
            }

            Vector3 right = Vector3.Cross(baseGroundPose.Normal, flatForward).normalized;
            float followAlpha = 1f - Mathf.Exp(-transformSmoothing * Mathf.Max(deltaTime, 0.0001f));

            for (int index = 0; index < cueVisuals.Count; index++)
            {
                CueVisual cueVisual = cueVisuals[index];
                bool shouldDisplay = index < cueState.CueCount;
                cueVisual.Root.SetActive(shouldDisplay);

                if (!shouldDisplay)
                {
                    continue;
                }

                float side = alternateFeet ? (index % 2 == 0 ? -1f : 1f) : 0f;
                float forwardDistance = cueState.DistanceAhead + (cueState.Spacing * index);

                Vector3 targetPosition = baseGroundPose.Position
                    + (flatForward * forwardDistance)
                    + (right * side * cueState.LateralOffset);

                GroundPose groundedCuePose = RefineGroundPose(targetPosition, baseGroundPose);
                targetPosition = groundedCuePose.Position + (groundedCuePose.Normal * hoverHeight);

                Quaternion targetRotation = Quaternion.LookRotation(flatForward, groundedCuePose.Normal);

                if (!cueVisual.Initialized)
                {
                    cueVisual.Transform.position = targetPosition;
                    cueVisual.Transform.rotation = targetRotation;
                    cueVisual.Initialized = true;
                }
                else
                {
                    cueVisual.Transform.position = Vector3.Lerp(cueVisual.Transform.position, targetPosition, followAlpha);
                    cueVisual.Transform.rotation = Quaternion.Slerp(cueVisual.Transform.rotation, targetRotation, followAlpha);
                }

                cueVisual.Transform.localScale = new Vector3(cueScale.x, cueScale.y, Mathf.Max(cueScale.z, cueState.Spacing * 0.55f));

                float pulse = 1f + (pulseDepth * Mathf.Sin((currentTime + (index * 0.12f)) * cueState.PulseRate * Mathf.PI * 2f));
                UpdateCueVisual(cueVisual, cueState, pulse);
            }
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

        private GroundPose ResolveGroundPose(Vector3 origin)
        {
            if (usePhysicsGrounding)
            {
                Vector3 probeOrigin = origin + (Vector3.up * groundProbeHeight);

                if (Physics.Raycast(
                    probeOrigin,
                    Vector3.down,
                    out RaycastHit hit,
                    groundProbeHeight + groundProbeDistance,
                    groundLayers,
                    QueryTriggerInteraction.Ignore))
                {
                    return new GroundPose(hit.point, hit.normal);
                }
            }

            return new GroundPose(new Vector3(origin.x, groundHeight, origin.z), Vector3.up);
        }

        private GroundPose RefineGroundPose(Vector3 targetPosition, GroundPose fallback)
        {
            if (usePhysicsGrounding)
            {
                Vector3 probeOrigin = targetPosition + (fallback.Normal * groundProbeHeight);

                if (Physics.Raycast(
                    probeOrigin,
                    -fallback.Normal,
                    out RaycastHit hit,
                    groundProbeHeight + groundProbeDistance,
                    groundLayers,
                    QueryTriggerInteraction.Ignore))
                {
                    return new GroundPose(hit.point, hit.normal);
                }
            }

            return new GroundPose(new Vector3(targetPosition.x, fallback.Position.y, targetPosition.z), fallback.Normal);
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
