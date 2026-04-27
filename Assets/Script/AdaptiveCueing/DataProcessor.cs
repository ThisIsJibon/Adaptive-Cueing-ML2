using System;
using System.Collections.Generic;
using UnityEngine;

namespace AdaptiveCueing
{
    [DisallowMultipleComponent]
    public class DataProcessor : MonoBehaviour
    {
        public event Action<StepEvent> StepDetected;

        [Header("Filtering")]
        [SerializeField, Range(1f, 20f)] private float signalSmoothing = 8f;
        [SerializeField, Range(0.1f, 3f)] private float baselineHeightTracking = 0.75f;
        [SerializeField, Range(3, 16)] private int maxTrackedStepIntervals = 8;
        [SerializeField, Range(0.002f, 0.1f)] private float minimumStepBobAmplitude = 0.01f;

        private readonly Queue<float> recentStepIntervals = new Queue<float>();

        private bool baselineInitialized;
        private float baselineHeight;
        private float smoothedForwardVelocity;
        private float smoothedLateralVelocity;
        private float smoothedVerticalVelocity;
        private float smoothedAcceleration;
        private float smoothedVerticalDisplacement;
        private float previousVerticalDisplacement;
        private float positiveDisplacementPeak;
        private float lastStepTimestamp = -1f;
        private Vector3 lastStepPosition;
        private bool hasLastStepPosition;
        private float lastStepLength;
        private int stepIndex;

        public ProcessedFrame LatestFrame { get; private set; }

        public void ResetState()
        {
            recentStepIntervals.Clear();
            baselineInitialized = false;
            baselineHeight = 0f;
            smoothedForwardVelocity = 0f;
            smoothedLateralVelocity = 0f;
            smoothedVerticalVelocity = 0f;
            smoothedAcceleration = 0f;
            smoothedVerticalDisplacement = 0f;
            previousVerticalDisplacement = 0f;
            positiveDisplacementPeak = 0f;
            lastStepTimestamp = -1f;
            lastStepPosition = Vector3.zero;
            hasLastStepPosition = false;
            lastStepLength = 0f;
            stepIndex = 0;
            LatestFrame = default;
        }

        public ProcessedFrame ProcessFrame(SensorFrame sensorFrame, float deltaTime)
        {
            if (!sensorFrame.IsValid)
            {
                LatestFrame = default;
                return LatestFrame;
            }

            float safeDeltaTime = Mathf.Max(deltaTime, 0.0001f);
            float smoothingAlpha = 1f - Mathf.Exp(-signalSmoothing * safeDeltaTime);
            float baselineAlpha = 1f - Mathf.Exp(-baselineHeightTracking * safeDeltaTime);

            Vector3 flatForward = Vector3.ProjectOnPlane(sensorFrame.Rotation * Vector3.forward, Vector3.up);

            if (flatForward.sqrMagnitude < 0.0001f)
            {
                flatForward = Vector3.forward;
            }
            else
            {
                flatForward.Normalize();
            }

            float rawForwardVelocity = Vector3.Dot(sensorFrame.WorldVelocity, flatForward);
            float rawLateralVelocity = Mathf.Abs(sensorFrame.LocalVelocity.x);
            float rawVerticalVelocity = sensorFrame.LocalVelocity.y;
            float rawAcceleration = sensorFrame.WorldAcceleration.magnitude;

            smoothedForwardVelocity = Mathf.Lerp(smoothedForwardVelocity, rawForwardVelocity, smoothingAlpha);
            smoothedLateralVelocity = Mathf.Lerp(smoothedLateralVelocity, rawLateralVelocity, smoothingAlpha);
            smoothedVerticalVelocity = Mathf.Lerp(smoothedVerticalVelocity, rawVerticalVelocity, smoothingAlpha);
            smoothedAcceleration = Mathf.Lerp(smoothedAcceleration, rawAcceleration, smoothingAlpha);

            if (!baselineInitialized)
            {
                baselineHeight = sensorFrame.Position.y;
                baselineInitialized = true;
            }

            baselineHeight = Mathf.Lerp(baselineHeight, sensorFrame.Position.y, baselineAlpha);

            float verticalDisplacement = sensorFrame.Position.y - baselineHeight;
            smoothedVerticalDisplacement = Mathf.Lerp(smoothedVerticalDisplacement, verticalDisplacement, smoothingAlpha);

            UpdateStepRhythm(sensorFrame.Timestamp, smoothedVerticalDisplacement, sensorFrame.Position);

            float meanStepInterval = CalculateMeanStepInterval();
            float stepFrequency = meanStepInterval > 0f ? 1f / meanStepInterval : 0f;
            float rhythmConsistency = CalculateRhythmConsistency();
            float verticalBobAmplitude = Mathf.Abs(smoothedVerticalDisplacement);
            float movementEffort = (Mathf.Abs(smoothedVerticalVelocity) * 0.35f)
                + (smoothedLateralVelocity * 0.25f)
                + (smoothedAcceleration * 0.25f)
                + (verticalBobAmplitude * 8f * 0.15f);

            LatestFrame = new ProcessedFrame
            {
                IsValid = true,
                Timestamp = sensorFrame.Timestamp,
                SourceMode = sensorFrame.ActiveSource,
                ForwardVelocity = Mathf.Max(0f, smoothedForwardVelocity),
                LateralSway = smoothedLateralVelocity,
                VerticalVelocity = smoothedVerticalVelocity,
                VerticalBobAmplitude = verticalBobAmplitude,
                RhythmConsistency = rhythmConsistency,
                StepFrequency = stepFrequency,
                CadenceStepsPerMinute = stepFrequency * 60f,
                LastStepLength = lastStepLength,
                MovementEffort = movementEffort,
                AccelerationMagnitude = smoothedAcceleration,
                DetectedStepCount = recentStepIntervals.Count
            };

            return LatestFrame;
        }

        private void UpdateStepRhythm(float timestamp, float verticalDisplacement, Vector3 currentPosition)
        {
            if (verticalDisplacement > 0f)
            {
                positiveDisplacementPeak = Mathf.Max(positiveDisplacementPeak, verticalDisplacement);
            }

            bool stepBoundaryDetected = previousVerticalDisplacement > 0f
                && verticalDisplacement <= 0f
                && positiveDisplacementPeak >= minimumStepBobAmplitude;

            if (stepBoundaryDetected)
            {
                float interval = timestamp - lastStepTimestamp;

                if (lastStepTimestamp >= 0f && interval > 0.25f && interval < 1.5f)
                {
                    recentStepIntervals.Enqueue(interval);

                    while (recentStepIntervals.Count > maxTrackedStepIntervals)
                    {
                        recentStepIntervals.Dequeue();
                    }

                    if (hasLastStepPosition)
                    {
                        Vector2 horizontalDelta = new Vector2(
                            currentPosition.x - lastStepPosition.x,
                            currentPosition.z - lastStepPosition.z);
                        lastStepLength = horizontalDelta.magnitude;
                    }

                    stepIndex++;
                    float cadence = interval > 0f ? 60f / interval : 0f;
                    StepDetected?.Invoke(new StepEvent
                    {
                        Timestamp = timestamp,
                        StepIndex = stepIndex,
                        Interval = interval,
                        StepLength = lastStepLength,
                        StepFrequency = interval > 0f ? 1f / interval : 0f,
                        CadenceStepsPerMinute = cadence,
                        Position = currentPosition
                    });
                }

                lastStepTimestamp = timestamp;
                lastStepPosition = currentPosition;
                hasLastStepPosition = true;
                positiveDisplacementPeak = 0f;
            }

            if (verticalDisplacement < 0f)
            {
                positiveDisplacementPeak *= 0.9f;
            }

            previousVerticalDisplacement = verticalDisplacement;
        }

        private float CalculateMeanStepInterval()
        {
            if (recentStepIntervals.Count == 0)
            {
                return 0f;
            }

            float total = 0f;

            foreach (float interval in recentStepIntervals)
            {
                total += interval;
            }

            return total / recentStepIntervals.Count;
        }

        private float CalculateRhythmConsistency()
        {
            if (recentStepIntervals.Count < 2)
            {
                return 0.7f;
            }

            float mean = CalculateMeanStepInterval();

            if (mean <= 0f)
            {
                return 0f;
            }

            float variance = 0f;

            foreach (float interval in recentStepIntervals)
            {
                float diff = interval - mean;
                variance += diff * diff;
            }

            variance /= recentStepIntervals.Count;

            float standardDeviation = Mathf.Sqrt(variance);
            float coefficientOfVariation = standardDeviation / mean;

            return 1f - Mathf.InverseLerp(0.08f, 0.45f, coefficientOfVariation);
        }
    }
}
