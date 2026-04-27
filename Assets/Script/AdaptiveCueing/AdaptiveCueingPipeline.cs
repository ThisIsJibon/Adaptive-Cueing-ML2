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
        [SerializeField] private AdaptiveCueMenuController menuController;
        [SerializeField] private TelemetryLogger telemetryLogger;

        private bool lastFoGState;

        public SensorFrame LatestSensorFrame { get; private set; }

        public ProcessedFrame LatestProcessedFrame { get; private set; }

        public FoGDetectionResult LatestDetectionResult { get; private set; }

        public CueState LatestCueState { get; private set; }

        public TelemetryLogger TelemetryLogger => telemetryLogger;

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

            telemetryLogger?.SampleFrame(LatestSensorFrame, LatestProcessedFrame, LatestDetectionResult, LatestCueState);

            if (LatestDetectionResult.IsFoG != lastFoGState)
            {
                if (telemetryLogger != null)
                {
                    telemetryLogger.LogEvent(
                        LatestDetectionResult.IsFoG ? "fog_onset" : "fog_end",
                        string.Format(System.Globalization.CultureInfo.InvariantCulture,
                            "confidence={0:F2}", LatestDetectionResult.Confidence));
                }

                if (logStateTransitions)
                {
                    Debug.Log($"Adaptive cueing FoG state changed: {LatestDetectionResult.IsFoG} (confidence {LatestDetectionResult.Confidence:F2})");
                }

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
            menuController = GetOrAddComponent(menuController);
            telemetryLogger = GetOrAddComponent(telemetryLogger);
            telemetryLogger.Bind(dataProcessor);
        }

        private void ConfigureReferences()
        {
            if (arRenderer != null)
            {
                arRenderer.SetAnchor(sensorManager != null ? sensorManager.HeadTransform : null);
            }

            if (menuController != null)
            {
                menuController.Bind(arRenderer, sensorManager != null ? sensorManager.HeadTransform : null);
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
