using System;
using UnityEngine;

namespace AdaptiveCueing
{
    public enum SensorSourceMode
    {
        Auto,
        HeadTracking,
        MockWalking,
        MockFreezeRisk,
        MockScriptedDemo
    }

    [Serializable]
    public struct SensorFrame
    {
        public bool IsValid;
        public float Timestamp;
        public SensorSourceMode ActiveSource;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 WorldVelocity;
        public Vector3 LocalVelocity;
        public Vector3 WorldAcceleration;
        public Vector3 LocalAngularVelocity;
        public float SignalStrength;

        public static SensorFrame Invalid(float timestamp, SensorSourceMode source)
        {
            return new SensorFrame
            {
                IsValid = false,
                Timestamp = timestamp,
                ActiveSource = source,
                Position = Vector3.zero,
                Rotation = Quaternion.identity,
                WorldVelocity = Vector3.zero,
                LocalVelocity = Vector3.zero,
                WorldAcceleration = Vector3.zero,
                LocalAngularVelocity = Vector3.zero,
                SignalStrength = 0f
            };
        }
    }

    [Serializable]
    public struct ProcessedFrame
    {
        public bool IsValid;
        public float Timestamp;
        public SensorSourceMode SourceMode;
        public float ForwardVelocity;
        public float LateralSway;
        public float VerticalVelocity;
        public float VerticalBobAmplitude;
        public float RhythmConsistency;
        public float StepFrequency;
        public float MovementEffort;
        public float AccelerationMagnitude;
        public int DetectedStepCount;

        public float RhythmIrregularity => 1f - RhythmConsistency;
    }

    [Serializable]
    public struct FoGDetectionResult
    {
        public bool IsFoG;
        public float Timestamp;
        public float Confidence;
        public float LowVelocityScore;
        public float RhythmIrregularityScore;
        public float MovementConflictScore;
    }

    [Serializable]
    public struct CueState
    {
        public bool IsValid;
        public bool IsAssistive;
        public float Timestamp;
        public float AssistanceLevel;
        public float Spacing;
        public float Brightness;
        public float PulseRate;
        public float DistanceAhead;
        public float LateralOffset;
        public int CueCount;
        public Color Tint;
    }
}
