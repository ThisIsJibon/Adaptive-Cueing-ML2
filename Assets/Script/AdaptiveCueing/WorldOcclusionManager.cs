using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;

namespace AdaptiveCueing
{
    /// <summary>
    /// Drives Magic Leap 2 world-mesh occlusion: it makes sure an
    /// <see cref="ARMeshManager"/> exists on the XR Origin, and configures it
    /// to spawn invisible "depth only" chunks so real world geometry (floor,
    /// feet, shoes, legs) occludes opaque virtual content such as the stepping
    /// cues, without altering the cue prefabs or their materials.
    /// </summary>
    [DisallowMultipleComponent]
    public class WorldOcclusionManager : MonoBehaviour
    {
        private const string DepthOccluderShaderName = "AdaptiveCueing/DepthOccluder";

        [Header("Meshing")]
        [SerializeField] private ARMeshManager meshManager;
        [SerializeField] private bool enableMeshingOnStart = true;

        [Header("Occluder Prefab")]
        [Tooltip("Layer to assign to the instantiated occluder chunks. Must be rendered by the main AR camera.")]
        [SerializeField] private int occluderLayer = 0;
        [SerializeField] private bool logSetup = true;

        private Material occluderMaterial;
        private MeshFilter occluderPrefabTemplate;
        private GameObject occluderPrefabTemplateGO;
        private bool isConfigured;

        public bool IsConfigured => isConfigured;
        public ARMeshManager MeshManager => meshManager;

        private void Awake()
        {
            TryConfigureOcclusion();
        }

        private void OnEnable()
        {
            TryConfigureOcclusion();

            if (enableMeshingOnStart && meshManager != null)
            {
                meshManager.enabled = true;
            }
        }

        private void OnDestroy()
        {
            if (occluderPrefabTemplateGO != null)
            {
                Destroy(occluderPrefabTemplateGO);
                occluderPrefabTemplateGO = null;
                occluderPrefabTemplate = null;
            }

            if (occluderMaterial != null)
            {
                Destroy(occluderMaterial);
                occluderMaterial = null;
            }
        }

        public void TryConfigureOcclusion()
        {
            if (isConfigured)
            {
                return;
            }

            if (!EnsureMeshManager())
            {
                return;
            }

            if (!EnsureOccluderMaterial())
            {
                return;
            }

            EnsureOccluderPrefab();
            ApplyOccluderPrefabToMeshManager();

            isConfigured = true;

            if (logSetup)
            {
                Debug.Log("[WorldOcclusion] World-mesh depth occlusion enabled. " +
                          "Real-world geometry (including feet when meshed) will now occlude cues.");
            }
        }

        private bool EnsureMeshManager()
        {
            if (meshManager != null)
            {
                return true;
            }

            meshManager = FindObjectOfType<ARMeshManager>();
            if (meshManager != null)
            {
                return true;
            }

            XROrigin xrOrigin = FindObjectOfType<XROrigin>();
            if (xrOrigin == null)
            {
                if (logSetup)
                {
                    Debug.LogWarning("[WorldOcclusion] No XROrigin found yet. Occlusion setup will retry.");
                }
                return false;
            }

            Transform parent = xrOrigin.TrackablesParent != null
                ? xrOrigin.TrackablesParent
                : xrOrigin.transform;

            GameObject meshManagerGO = new GameObject("AR Mesh Manager (Occlusion)");
            meshManagerGO.transform.SetParent(parent, false);
            meshManager = meshManagerGO.AddComponent<ARMeshManager>();

            if (logSetup)
            {
                Debug.Log("[WorldOcclusion] Created ARMeshManager on XR Origin for occlusion.");
            }

            return true;
        }

        private bool EnsureOccluderMaterial()
        {
            if (occluderMaterial != null)
            {
                return true;
            }

            Shader shader = Shader.Find(DepthOccluderShaderName);
            if (shader == null)
            {
                Debug.LogError($"[WorldOcclusion] Shader '{DepthOccluderShaderName}' not found. " +
                               "Ensure DepthOccluder.shader exists under Assets/Shaders/ and is included in the build.");
                return false;
            }

            occluderMaterial = new Material(shader)
            {
                name = "WorldOccluderMaterial",
                hideFlags = HideFlags.HideAndDontSave
            };

            return true;
        }

        private void EnsureOccluderPrefab()
        {
            if (occluderPrefabTemplate != null)
            {
                return;
            }

            occluderPrefabTemplateGO = new GameObject("ARMeshOccluder_Template");
            occluderPrefabTemplateGO.transform.SetParent(transform, false);
            occluderPrefabTemplateGO.hideFlags = HideFlags.HideAndDontSave;
            occluderPrefabTemplateGO.layer = Mathf.Clamp(occluderLayer, 0, 31);

            MeshFilter filter = occluderPrefabTemplateGO.AddComponent<MeshFilter>();
            MeshRenderer renderer = occluderPrefabTemplateGO.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = occluderMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            renderer.allowOcclusionWhenDynamic = false;

            // Template itself should never render anything - it has no mesh assigned.
            occluderPrefabTemplate = filter;
        }

        private void ApplyOccluderPrefabToMeshManager()
        {
            if (meshManager == null || occluderPrefabTemplate == null)
            {
                return;
            }

            meshManager.meshPrefab = occluderPrefabTemplate;
        }
    }
}
