using UnityEngine;

namespace AdaptiveCueing
{
    [DisallowMultipleComponent]
    public class FoGDetector : MonoBehaviour
    {
        [Header("Detection Thresholds")]
        [SerializeField, Min(0.01f)] private float lowVelocityThreshold = 0.18f;
        [SerializeField, Range(0f, 1f)] private float rhythmConsistencyThreshold = 0.55f;
        [SerializeField, Min(0f)] private float minimumMovementEffort = 0.12f;
        [SerializeField, Min(0.05f)] private float requiredCandidateDuration = 0.35f;
        [SerializeField, Range(0f, 1f)] private float fogOnConfidence = 0.65f;
        [SerializeField, Range(0f, 1f)] private float fogOffConfidence = 0.45f;
        [SerializeField, Range(1f, 20f)] private float confidenceSmoothing = 6f;

        private bool isFoGActive;
        private float smoothedConfidence;
        private float candidateDuration;

        public FoGDetectionResult LatestResult { get; private set; }

        public void ResetState()
        {
            isFoGActive = false;
            smoothedConfidence = 0f;
            candidateDuration = 0f;
            LatestResult = default;
        }

        public FoGDetectionResult Evaluate(ProcessedFrame processedFrame, float deltaTime)
        {
            if (!processedFrame.IsValid)
            {
                LatestResult = default;
                return LatestResult;
            }

            float lowVelocityScore = 1f - Mathf.InverseLerp(
                lowVelocityThreshold,
                lowVelocityThreshold * 3f,
                processedFrame.ForwardVelocity);

            float rhythmIrregularityScore = 1f - Mathf.InverseLerp(
                rhythmConsistencyThreshold,
                1f,
                processedFrame.RhythmConsistency);

            float effortScore = Mathf.InverseLerp(
                minimumMovementEffort,
                minimumMovementEffort * 3f,
                processedFrame.MovementEffort);

            float lateralConflictScore = Mathf.InverseLerp(0.02f, 0.12f, processedFrame.LateralSway);
            float movementConflictScore = Mathf.Clamp01((effortScore * 0.65f) + (lateralConflictScore * 0.35f)) * lowVelocityScore;

            float rawConfidence = Mathf.Clamp01(
                (lowVelocityScore * 0.45f) +
                (rhythmIrregularityScore * 0.35f) +
                (movementConflictScore * 0.20f));

            float smoothingAlpha = 1f - Mathf.Exp(-confidenceSmoothing * Mathf.Max(deltaTime, 0.0001f));
            smoothedConfidence = Mathf.Lerp(smoothedConfidence, rawConfidence, smoothingAlpha);

            bool isCandidate = lowVelocityScore > 0.55f
                && (rhythmIrregularityScore > 0.35f || movementConflictScore > 0.35f);

            if (isCandidate)
            {
                candidateDuration += deltaTime;
            }
            else
            {
                candidateDuration = Mathf.Max(0f, candidateDuration - (deltaTime * 2f));
            }

            if (!isFoGActive && smoothedConfidence >= fogOnConfidence && candidateDuration >= requiredCandidateDuration)
            {
                isFoGActive = true;
            }
            else if (isFoGActive && smoothedConfidence <= fogOffConfidence)
            {
                isFoGActive = false;
            }

            LatestResult = new FoGDetectionResult
            {
                IsFoG = isFoGActive,
                Timestamp = processedFrame.Timestamp,
                Confidence = smoothedConfidence,
                LowVelocityScore = lowVelocityScore,
                RhythmIrregularityScore = rhythmIrregularityScore,
                MovementConflictScore = movementConflictScore
            };

            return LatestResult;
        }
    }
}
