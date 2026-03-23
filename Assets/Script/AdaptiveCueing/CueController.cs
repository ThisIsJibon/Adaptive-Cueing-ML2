using UnityEngine;

namespace AdaptiveCueing
{
    [DisallowMultipleComponent]
    public class CueController : MonoBehaviour
    {
        [Header("Baseline Cueing")]
        [SerializeField, Min(0.1f)] private float normalSpacing = 0.65f;
        [SerializeField, Range(0f, 3f)] private float normalBrightness = 0.55f;
        [SerializeField, Min(0.1f)] private float normalPulseRate = 1.6f;
        [SerializeField, Min(0.1f)] private float normalDistanceAhead = 0.8f;
        [SerializeField, Min(0f)] private float normalLateralOffset = 0.18f;
        [SerializeField, Range(1, 12)] private int normalCueCount = 6;
        [SerializeField, ColorUsage(false, true)] private Color normalCueColor = new Color(0.10f, 0.85f, 1.00f, 1.00f);

        [Header("Assistive Cueing")]
        [SerializeField, Min(0.1f)] private float fogSpacing = 0.38f;
        [SerializeField, Range(0f, 3f)] private float fogBrightness = 1.25f;
        [SerializeField, Min(0.1f)] private float fogPulseRate = 2.2f;
        [SerializeField, Min(0.1f)] private float fogDistanceAhead = 0.45f;
        [SerializeField, Min(0f)] private float fogLateralOffset = 0.16f;
        [SerializeField, Range(1, 12)] private int fogCueCount = 8;
        [SerializeField, ColorUsage(false, true)] private Color fogCueColor = new Color(1.00f, 0.82f, 0.20f, 1.00f);

        [Header("Response")]
        [SerializeField, Range(1f, 20f)] private float adaptationSpeed = 5f;
        [SerializeField, Range(0f, 1f)] private float predictiveAssistWeight = 0.35f;

        private float assistanceLevel;

        public CueState LatestCueState { get; private set; }

        public void ResetState()
        {
            assistanceLevel = 0f;
            LatestCueState = default;
        }

        public CueState UpdateCueState(ProcessedFrame processedFrame, FoGDetectionResult detectionResult, float deltaTime)
        {
            if (!processedFrame.IsValid)
            {
                LatestCueState = default;
                return LatestCueState;
            }

            float targetAssistance = detectionResult.IsFoG
                ? Mathf.Max(detectionResult.Confidence, 0.7f)
                : detectionResult.Confidence * predictiveAssistWeight;

            float blendAlpha = 1f - Mathf.Exp(-adaptationSpeed * Mathf.Max(deltaTime, 0.0001f));
            assistanceLevel = Mathf.Lerp(assistanceLevel, targetAssistance, blendAlpha);

            float paceInfluence = Mathf.Clamp01(processedFrame.StepFrequency / 2.5f);

            LatestCueState = new CueState
            {
                IsValid = true,
                IsAssistive = detectionResult.IsFoG,
                Timestamp = processedFrame.Timestamp,
                AssistanceLevel = assistanceLevel,
                Spacing = Mathf.Lerp(normalSpacing, fogSpacing, assistanceLevel),
                Brightness = Mathf.Lerp(normalBrightness, fogBrightness, assistanceLevel),
                PulseRate = Mathf.Lerp(normalPulseRate, fogPulseRate, assistanceLevel) + (paceInfluence * 0.2f),
                DistanceAhead = Mathf.Lerp(normalDistanceAhead, fogDistanceAhead, assistanceLevel),
                LateralOffset = Mathf.Lerp(normalLateralOffset, fogLateralOffset, assistanceLevel),
                CueCount = Mathf.RoundToInt(Mathf.Lerp(normalCueCount, fogCueCount, assistanceLevel)),
                Tint = Color.Lerp(normalCueColor, fogCueColor, assistanceLevel)
            };

            return LatestCueState;
        }
    }
}
