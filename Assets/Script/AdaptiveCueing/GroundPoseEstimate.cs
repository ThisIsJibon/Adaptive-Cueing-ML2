using System;
using UnityEngine;

namespace AdaptiveCueing
{
    public enum GroundPoseSource
    {
        Unknown,
        LocalizedPlaneRaycast,
        TrackedFloorPlane,
        ManualCalibration,
        EstimatedFromHeadHeight
    }

    [Serializable]
    public struct GroundPoseEstimate
    {
        public bool IsValid;
        public Vector3 Position;
        public Vector3 Normal;
        public float EstimatedUserHeight;
        public GroundPoseSource Source;
        public int SampleCount;
        public float Confidence;

        public Pose Pose => new Pose(Position, Quaternion.LookRotation(Vector3.ProjectOnPlane(Vector3.forward, Normal), Normal));
    }
}
