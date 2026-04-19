using AdaptiveCueing;
using NUnit.Framework;
using UnityEngine;

public class AdaptiveCueingEditModeTests
{
    [Test]
    public void DataProcessor_ExtractsStableWalkingFeatures()
    {
        GameObject processorObject = new GameObject("DataProcessorTest");
        DataProcessor processor = processorObject.AddComponent<DataProcessor>();
        processor.ResetState();

        float timestamp = 0f;
        float deltaTime = 0.1f;
        Vector3 previousPosition = Vector3.zero;
        Vector3 previousVelocity = Vector3.zero;
        ProcessedFrame result = default;

        for (int index = 0; index < 90; index++)
        {
            timestamp += deltaTime;
            float forwardDistance = 0.9f * timestamp;
            float verticalBob = 0.035f * Mathf.Sin(timestamp * Mathf.PI * 2f * 1.8f);
            Vector3 position = new Vector3(0f, 1.6f + verticalBob, forwardDistance);
            Vector3 velocity = index == 0 ? Vector3.zero : (position - previousPosition) / deltaTime;
            Vector3 acceleration = index == 0 ? Vector3.zero : (velocity - previousVelocity) / deltaTime;

            SensorFrame frame = new SensorFrame
            {
                IsValid = true,
                Timestamp = timestamp,
                ActiveSource = SensorSourceMode.MockWalking,
                Position = position,
                Rotation = Quaternion.identity,
                WorldVelocity = velocity,
                LocalVelocity = velocity,
                WorldAcceleration = acceleration,
                LocalAngularVelocity = Vector3.zero,
                SignalStrength = 1f
            };

            result = processor.ProcessFrame(frame, deltaTime);
            previousPosition = position;
            previousVelocity = velocity;
        }

        Assert.That(result.ForwardVelocity, Is.GreaterThan(0.5f));
        Assert.That(result.StepFrequency, Is.GreaterThan(1.2f));
        Assert.That(result.RhythmConsistency, Is.GreaterThan(0.7f));

        Object.DestroyImmediate(processorObject);
    }

    [Test]
    public void FoGDetector_FlagsSustainedFreezePattern()
    {
        GameObject detectorObject = new GameObject("FoGDetectorTest");
        FoGDetector detector = detectorObject.AddComponent<FoGDetector>();
        detector.ResetState();

        FoGDetectionResult result = default;
        float timestamp = 0f;
        float deltaTime = 0.1f;

        for (int index = 0; index < 8; index++)
        {
            timestamp += deltaTime;
            ProcessedFrame processedFrame = new ProcessedFrame
            {
                IsValid = true,
                Timestamp = timestamp,
                SourceMode = SensorSourceMode.MockFreezeRisk,
                ForwardVelocity = 0.04f,
                LateralSway = 0.09f,
                VerticalVelocity = 0.02f,
                VerticalBobAmplitude = 0.012f,
                RhythmConsistency = 0.20f,
                StepFrequency = 1.7f,
                MovementEffort = 0.30f,
                AccelerationMagnitude = 0.22f,
                DetectedStepCount = 6
            };

            result = detector.Evaluate(processedFrame, deltaTime);
        }

        Assert.That(result.Confidence, Is.GreaterThan(0.6f));
        Assert.That(result.IsFoG, Is.True);

        Object.DestroyImmediate(detectorObject);
    }

    [Test]
    public void CueController_TightensCueSpacingDuringAssistiveState()
    {
        GameObject cueObject = new GameObject("CueControllerTest");
        CueController controller = cueObject.AddComponent<CueController>();
        controller.ResetState();

        CueState baseline = default;
        CueState assistive = default;
        float timestamp = 0f;
        float deltaTime = 0.1f;

        for (int index = 0; index < 5; index++)
        {
            timestamp += deltaTime;
            baseline = controller.UpdateCueState(
                new ProcessedFrame
                {
                    IsValid = true,
                    Timestamp = timestamp,
                    StepFrequency = 1.7f
                },
                new FoGDetectionResult
                {
                    Timestamp = timestamp,
                    Confidence = 0.1f,
                    IsFoG = false
                },
                deltaTime);
        }

        for (int index = 0; index < 8; index++)
        {
            timestamp += deltaTime;
            assistive = controller.UpdateCueState(
                new ProcessedFrame
                {
                    IsValid = true,
                    Timestamp = timestamp,
                    StepFrequency = 1.7f
                },
                new FoGDetectionResult
                {
                    Timestamp = timestamp,
                    Confidence = 0.9f,
                    IsFoG = true
                },
                deltaTime);
        }

        Assert.That(assistive.Spacing, Is.LessThan(baseline.Spacing));
        Assert.That(assistive.Brightness, Is.GreaterThan(baseline.Brightness));
        Assert.That(assistive.CueCount, Is.GreaterThanOrEqualTo(baseline.CueCount));

        Object.DestroyImmediate(cueObject);
    }

    [Test]
    public void CuePresetLibrary_ProvidesPaperInspiredCueOptionsWithPreviewTextures()
    {
        CuePresetDefinition[] presets = CuePresetLibrary.GetBuiltInPresets();

        Assert.That(presets.Length, Is.GreaterThanOrEqualTo(5));
        Assert.That(presets[0].DisplayName, Is.Not.Empty);

        Texture2D preview = CuePresetLibrary.CreatePreviewTexture(presets[0], 96, 56);

        Assert.That(preview.width, Is.EqualTo(96));
        Assert.That(preview.height, Is.EqualTo(56));

        Object.DestroyImmediate(preview);
    }
}
