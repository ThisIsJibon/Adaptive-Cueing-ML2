using UnityEngine;

namespace AdaptiveCueing
{
    [DisallowMultipleComponent]
    public class AdaptiveCueingPipeline : MonoBehaviour
    {
        [Header("Runtime")]
        [SerializeField] private bool runPipeline = true;
        [SerializeField] private bool logStateTransitions = true;

        [Header("Modules")]
        [SerializeField] private SensorManager sensorManager;
        [SerializeField] private DataProcessor dataProcessor;
        [SerializeField] private FoGDetector foGDetector;
        [SerializeField] private CueController cueController;
        [SerializeField] private ARRenderer arRenderer;

        private bool lastFoGState;

        public SensorFrame LatestSensorFrame { get; private set; }

        public ProcessedFrame LatestProcessedFrame { get; private set; }

        public FoGDetectionResult LatestDetectionResult { get; private set; }

        public CueState LatestCueState { get; private set; }

        private void Reset()
        {
            EnsureModules();
            ConfigureReferences();
        }

        private void Awake()
        {
            EnsureModules();
            ConfigureReferences();
        }

        private void OnEnable()
        {
            ResetRuntimeState();
        }

        public void ResetRuntimeState()
        {
            sensorManager?.ResetState();
            dataProcessor?.ResetState();
            foGDetector?.ResetState();
            cueController?.ResetState();
            lastFoGState = false;
            LatestSensorFrame = default;
            LatestProcessedFrame = default;
            LatestDetectionResult = default;
            LatestCueState = default;
        }

        private void Update()
        {
            if (!runPipeline)
            {
                return;
            }

            float deltaTime = Mathf.Max(Time.deltaTime, 0.0001f);

            LatestSensorFrame = sensorManager.SampleFrame(Time.time, deltaTime);

            if (!LatestSensorFrame.IsValid)
            {
                LatestCueState = default;
                arRenderer.RenderCueState(LatestCueState, Time.time, deltaTime);
                return;
            }

            LatestProcessedFrame = dataProcessor.ProcessFrame(LatestSensorFrame, deltaTime);
            LatestDetectionResult = foGDetector.Evaluate(LatestProcessedFrame, deltaTime);
            LatestCueState = cueController.UpdateCueState(LatestProcessedFrame, LatestDetectionResult, deltaTime);
            arRenderer.RenderCueState(LatestCueState, Time.time, deltaTime);

            if (logStateTransitions && LatestDetectionResult.IsFoG != lastFoGState)
            {
                Debug.Log($"Adaptive cueing FoG state changed: {LatestDetectionResult.IsFoG} (confidence {LatestDetectionResult.Confidence:F2})");
                lastFoGState = LatestDetectionResult.IsFoG;
            }
        }

        private void EnsureModules()
        {
            sensorManager = GetOrAddComponent(sensorManager);
            dataProcessor = GetOrAddComponent(dataProcessor);
            foGDetector = GetOrAddComponent(foGDetector);
            cueController = GetOrAddComponent(cueController);
            arRenderer = GetOrAddComponent(arRenderer);
        }

        private void ConfigureReferences()
        {
            if (arRenderer != null)
            {
                arRenderer.SetAnchor(sensorManager != null ? sensorManager.HeadTransform : null);
            }
        }

        private T GetOrAddComponent<T>(T component) where T : Component
        {
            if (component != null)
            {
                return component;
            }

            T resolvedComponent = GetComponent<T>();
            return resolvedComponent != null ? resolvedComponent : gameObject.AddComponent<T>();
        }
    }
}
