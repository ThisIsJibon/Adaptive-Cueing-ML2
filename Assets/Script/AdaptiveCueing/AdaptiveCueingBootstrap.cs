using UnityEngine;

namespace AdaptiveCueing
{
    public static class AdaptiveCueingBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsurePipelineExists()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            AdaptiveCueingPipeline existingPipeline = Object.FindObjectOfType<AdaptiveCueingPipeline>();

            if (existingPipeline != null)
            {
                return;
            }

            GameObject root = new GameObject("Adaptive Cueing Prototype");
            root.AddComponent<AdaptiveCueingPipeline>();
        }
    }
}
