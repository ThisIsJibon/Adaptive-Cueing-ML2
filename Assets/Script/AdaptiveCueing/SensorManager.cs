using UnityEngine;

namespace AdaptiveCueing
{
    [DisallowMultipleComponent]
    public class SensorManager : MonoBehaviour
    {
        private const float MinimumDeltaTime = 0.0001f;

        [Header("Source Selection")]
        [SerializeField] private SensorSourceMode sourceMode = SensorSourceMode.Auto;
        [SerializeField] private bool useHeadTrackingInEditor = false;
        [SerializeField] private Transform headTransform;
        [SerializeField] private Camera fallbackCamera;

        [Header("Mock Motion")]
        [SerializeField, Min(0.1f)] private float mockWalkingSpeed = 0.95f;
        [SerializeField, Range(0.5f, 3.0f)] private float mockStepFrequency = 1.8f;
        [SerializeField, Range(0.0f, 0.15f)] private float mockVerticalAmplitude = 0.035f;
        [SerializeField, Range(0.0f, 0.15f)] private float mockLateralAmplitude = 0.025f;
        [SerializeField, Min(1.0f)] private float scriptedWalkDuration = 8f;
        [SerializeField, Min(1.0f)] private float scriptedFreezeDuration = 4f;
        [SerializeField, Min(1.0f)] private float scriptedRecoveryDuration = 6f;
        [SerializeField] private Vector3 mockTravelDirection = Vector3.forward;

        private bool hasPreviousPose;
        private bool mockInitialized;
        private Vector3 previousPosition;
        private Quaternion previousRotation = Quaternion.identity;
        private Vector3 previousWorldVelocity;
        private float mockStartTime;
        private float mockDistance;
        private Vector3 mockOrigin;
        private SensorSourceMode activeSourceMode;

        public SensorFrame LatestFrame { get; private set; }

        public SensorSourceMode ActiveSourceMode => activeSourceMode;

        public Transform HeadTransform => ResolveHeadTransform();

        private void Awake()
        {
            ResolveHeadTransform();
        }

        private void OnEnable()
        {
            ResetState();
        }

        public void ResetState()
        {
            hasPreviousPose = false;
            mockInitialized = false;
            previousPosition = Vector3.zero;
            previousRotation = Quaternion.identity;
            previousWorldVelocity = Vector3.zero;
            mockDistance = 0f;
            mockStartTime = 0f;
            activeSourceMode = SensorSourceMode.Auto;
            LatestFrame = SensorFrame.Invalid(Time.time, activeSourceMode);
        }

        public SensorFrame SampleFrame(float currentTime, float deltaTime)
        {
            float safeDeltaTime = Mathf.Max(deltaTime, MinimumDeltaTime);
            SensorSourceMode nextSourceMode = ResolveActiveSourceMode();

            if (nextSourceMode != activeSourceMode)
            {
                ResetTrackingStateForSourceSwitch(currentTime, nextSourceMode);
                activeSourceMode = nextSourceMode;
            }

            LatestFrame = activeSourceMode == SensorSourceMode.HeadTracking
                ? SampleHeadTracking(currentTime, safeDeltaTime)
                : SampleMock(currentTime, safeDeltaTime, activeSourceMode);

            return LatestFrame;
        }

        private void ResetTrackingStateForSourceSwitch(float currentTime, SensorSourceMode nextSourceMode)
        {
            hasPreviousPose = false;
            previousWorldVelocity = Vector3.zero;

            if (nextSourceMode == SensorSourceMode.HeadTracking)
            {
                return;
            }

            mockInitialized = false;
            mockStartTime = currentTime;
            mockDistance = 0f;
        }

        private SensorSourceMode ResolveActiveSourceMode()
        {
            switch (sourceMode)
            {
                case SensorSourceMode.HeadTracking:
                    return ResolveHeadTransform() != null ? SensorSourceMode.HeadTracking : SensorSourceMode.MockScriptedDemo;

                case SensorSourceMode.MockWalking:
                case SensorSourceMode.MockFreezeRisk:
                case SensorSourceMode.MockScriptedDemo:
                    return sourceMode;

                case SensorSourceMode.Auto:
                default:
                    if (Application.isEditor && !useHeadTrackingInEditor)
                    {
                        return SensorSourceMode.MockScriptedDemo;
                    }

                    return ResolveHeadTransform() != null ? SensorSourceMode.HeadTracking : SensorSourceMode.MockScriptedDemo;
            }
        }

        private Transform ResolveHeadTransform()
        {
            if (headTransform != null)
            {
                return headTransform;
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

            headTransform = fallbackCamera != null ? fallbackCamera.transform : null;
            return headTransform;
        }

        private SensorFrame SampleHeadTracking(float currentTime, float deltaTime)
        {
            Transform trackedHead = ResolveHeadTransform();

            if (trackedHead == null)
            {
                return SensorFrame.Invalid(currentTime, SensorSourceMode.HeadTracking);
            }

            return BuildFrameFromPose(
                trackedHead.position,
                trackedHead.rotation,
                currentTime,
                deltaTime,
                SensorSourceMode.HeadTracking,
                trackedHead.position.magnitude);
        }

        private SensorFrame SampleMock(float currentTime, float deltaTime, SensorSourceMode mockMode)
        {
            if (!mockInitialized)
            {
                Transform trackedHead = ResolveHeadTransform();
                mockOrigin = trackedHead != null ? trackedHead.position : new Vector3(0f, 1.6f, 0f);
                mockStartTime = currentTime;
                mockDistance = 0f;
                mockInitialized = true;
            }

            float scenarioTime = currentTime - mockStartTime;
            MockMotionParameters motion = ResolveMockMotion(mockMode, scenarioTime);

            Vector3 travelForward = Vector3.ProjectOnPlane(
                mockTravelDirection.sqrMagnitude > 0.001f ? mockTravelDirection : Vector3.forward,
                Vector3.up);

            if (travelForward.sqrMagnitude < 0.001f)
            {
                travelForward = Vector3.forward;
            }

            travelForward.Normalize();

            Vector3 travelRight = Vector3.Cross(Vector3.up, travelForward).normalized;
            float gaitPhase = scenarioTime * motion.StepFrequency * Mathf.PI * 2f;
            float irregularPhase = motion.Irregularity * (
                0.35f * Mathf.Sin(scenarioTime * 3.7f) +
                0.20f * Mathf.Sin(scenarioTime * 7.1f + 0.6f));

            mockDistance += motion.ForwardSpeed * deltaTime;

            float forwardJitter = motion.Irregularity * 0.03f * Mathf.Sin(scenarioTime * 5.2f);
            float verticalOffset = motion.VerticalAmplitude * Mathf.Sin(gaitPhase + irregularPhase);
            float lateralOffset = motion.LateralAmplitude * Mathf.Sin(gaitPhase * 0.5f + irregularPhase * 0.85f);

            Vector3 position = mockOrigin
                + (travelForward * (mockDistance + forwardJitter))
                + (travelRight * lateralOffset)
                + (Vector3.up * verticalOffset);

            Quaternion rotation = ResolveMockRotation(travelForward, motion.Irregularity, scenarioTime);

            return BuildFrameFromPose(position, rotation, currentTime, deltaTime, mockMode, motion.ForwardSpeed);
        }

        private Quaternion ResolveMockRotation(Vector3 travelForward, float irregularity, float scenarioTime)
        {
            Transform trackedHead = ResolveHeadTransform();
            Quaternion baseRotation = trackedHead != null
                ? trackedHead.rotation
                : Quaternion.LookRotation(travelForward, Vector3.up);

            float yawSway = irregularity * 7f * Mathf.Sin(scenarioTime * 1.6f);
            return baseRotation * Quaternion.Euler(0f, yawSway, 0f);
        }

        private SensorFrame BuildFrameFromPose(
            Vector3 position,
            Quaternion rotation,
            float timestamp,
            float deltaTime,
            SensorSourceMode activeSource,
            float signalStrength)
        {
            Vector3 worldVelocity = Vector3.zero;
            Vector3 worldAcceleration = Vector3.zero;
            Vector3 localAngularVelocity = Vector3.zero;

            if (hasPreviousPose)
            {
                worldVelocity = (position - previousPosition) / deltaTime;
                worldAcceleration = (worldVelocity - previousWorldVelocity) / deltaTime;

                Quaternion deltaRotation = rotation * Quaternion.Inverse(previousRotation);
                deltaRotation.ToAngleAxis(out float angleDegrees, out Vector3 axis);

                if (float.IsNaN(axis.x))
                {
                    axis = Vector3.up;
                }

                if (angleDegrees > 180f)
                {
                    angleDegrees -= 360f;
                }

                Vector3 angularVelocityWorld = axis.normalized * angleDegrees * Mathf.Deg2Rad / deltaTime;
                localAngularVelocity = Quaternion.Inverse(rotation) * angularVelocityWorld;
            }

            Vector3 localVelocity = Quaternion.Inverse(rotation) * worldVelocity;

            previousPosition = position;
            previousRotation = rotation;
            previousWorldVelocity = worldVelocity;
            hasPreviousPose = true;

            return new SensorFrame
            {
                IsValid = true,
                Timestamp = timestamp,
                ActiveSource = activeSource,
                Position = position,
                Rotation = rotation,
                WorldVelocity = worldVelocity,
                LocalVelocity = localVelocity,
                WorldAcceleration = worldAcceleration,
                LocalAngularVelocity = localAngularVelocity,
                SignalStrength = signalStrength
            };
        }

        private MockMotionParameters ResolveMockMotion(SensorSourceMode mockMode, float scenarioTime)
        {
            switch (mockMode)
            {
                case SensorSourceMode.MockWalking:
                    return CreateWalkingParameters(1f, scenarioTime);

                case SensorSourceMode.MockFreezeRisk:
                    return CreateFreezeParameters(scenarioTime);

                case SensorSourceMode.MockScriptedDemo:
                default:
                    float cycleDuration = scriptedWalkDuration + scriptedFreezeDuration + scriptedRecoveryDuration;
                    float cycleTime = cycleDuration > 0f ? Mathf.Repeat(scenarioTime, cycleDuration) : scenarioTime;

                    if (cycleTime < scriptedWalkDuration)
                    {
                        return CreateWalkingParameters(1f, cycleTime);
                    }

                    if (cycleTime < scriptedWalkDuration + scriptedFreezeDuration)
                    {
                        return CreateFreezeParameters(cycleTime);
                    }

                    float recoveryTime = cycleTime - (scriptedWalkDuration + scriptedFreezeDuration);
                    float recoveryFactor = scriptedRecoveryDuration > 0f
                        ? Mathf.Clamp01(recoveryTime / scriptedRecoveryDuration)
                        : 1f;

                    return CreateWalkingParameters(Mathf.Lerp(0.55f, 0.9f, recoveryFactor), cycleTime);
            }
        }

        private MockMotionParameters CreateWalkingParameters(float intensity, float scenarioTime)
        {
            return new MockMotionParameters
            {
                ForwardSpeed = mockWalkingSpeed * intensity,
                StepFrequency = Mathf.Lerp(mockStepFrequency * 0.9f, mockStepFrequency, intensity),
                VerticalAmplitude = mockVerticalAmplitude * intensity,
                LateralAmplitude = mockLateralAmplitude * intensity,
                Irregularity = 0.05f + (0.03f * Mathf.Sin(scenarioTime * 0.75f))
            };
        }

        private MockMotionParameters CreateFreezeParameters(float scenarioTime)
        {
            return new MockMotionParameters
            {
                ForwardSpeed = 0.04f + (0.02f * (0.5f + (0.5f * Mathf.Sin(scenarioTime * 2.3f)))),
                StepFrequency = mockStepFrequency * 1.1f,
                VerticalAmplitude = mockVerticalAmplitude * 0.7f,
                LateralAmplitude = mockLateralAmplitude * 1.9f,
                Irregularity = 0.75f
            };
        }

        private struct MockMotionParameters
        {
            public float ForwardSpeed;
            public float StepFrequency;
            public float VerticalAmplitude;
            public float LateralAmplitude;
            public float Irregularity;
        }
    }
}
