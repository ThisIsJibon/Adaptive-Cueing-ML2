using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace AdaptiveCueing
{
    [DisallowMultipleComponent]
    public class TelemetryLogger : MonoBehaviour
    {
        [Header("Output")]
        [SerializeField] private string outputSubfolder = "Logs";
        [SerializeField] private bool logSamples = true;
        [SerializeField] private bool logSteps = true;
        [SerializeField] private bool logEvents = true;

        [Header("Sampling")]
        [SerializeField, Range(1f, 120f)] private float sampleRateHz = 30f;
        [SerializeField, Min(0.1f)] private float flushIntervalSeconds = 2f;
        [SerializeField, Min(0.5f)] private float featureWindowSeconds = 2.5f;
        [SerializeField, Min(0.1f)] private float movementSpeedThreshold = 0.08f;
        [SerializeField, Min(0.1f)] private float turningRateThresholdDegPerSec = 22f;
        [SerializeField, Min(0.1f)] private float hesitationSpeedThreshold = 0.05f;
        [SerializeField, Min(0.1f)] private float hesitationJerkThreshold = 0.45f;

        [Header("Session Metadata")]
        [SerializeField] private string participantId = "unknown";
        [SerializeField] private string trialType = "default";
        [SerializeField] private string notes = "";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private StreamWriter samplesWriter;
        private StreamWriter stepsWriter;
        private StreamWriter eventsWriter;

        private DataProcessor subscribedProcessor;
        private string sessionFolder;
        private float sessionStartTime;
        private DateTime sessionStartDateTime;
        private float nextSampleTime;
        private float nextFlushTime;
        private bool isOpen;
        private string sessionId;

        private bool hasPreviousSamplePose;
        private Vector3 previousSamplePosition;
        private Vector3 previousSampleAcceleration;
        private float previousSampleYaw;
        private float previousSamplePitch;
        private float previousSampleRoll;
        private float cumulativeDistance;
        private bool isStationaryState;
        private float stationaryStartElapsed = -1f;
        private float stopDuration;
        private int pauseCount;
        private bool isTurningState;
        private float turningStartElapsed = -1f;
        private float turnDuration;
        private float turnAngle;

        private readonly Queue<float> windowSpeed = new Queue<float>();
        private readonly Queue<float> windowAcceleration = new Queue<float>();
        private readonly Queue<float> windowJerk = new Queue<float>();
        private readonly Queue<float> windowYawRate = new Queue<float>();
        private readonly Dictionary<string, float> activeEventStarts = new Dictionary<string, float>();

        public string SessionFolder => sessionFolder;

        public void Bind(DataProcessor dataProcessor)
        {
            if (subscribedProcessor == dataProcessor)
            {
                return;
            }

            if (subscribedProcessor != null)
            {
                subscribedProcessor.StepDetected -= HandleStepDetected;
            }

            subscribedProcessor = dataProcessor;

            if (subscribedProcessor != null)
            {
                subscribedProcessor.StepDetected += HandleStepDetected;
            }
        }

        private void OnEnable()
        {
            OpenSession();
        }

        private void OnDisable()
        {
            CloseSession();
        }

        private void OnApplicationQuit()
        {
            CloseSession();
        }

        private void OpenSession()
        {
            if (isOpen)
            {
                return;
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv);
            sessionFolder = Path.Combine(Application.persistentDataPath, outputSubfolder, $"session_{timestamp}");
            Directory.CreateDirectory(sessionFolder);
            sessionId = $"session_{timestamp}";

            if (logSamples)
            {
                samplesWriter = CreateWriter("samples.csv",
                    "wall_time,timestamp_ms,elapsed_s,session_id,participant_id,trial_type,notes,source," +
                    "pos_x,pos_y,pos_z,rot_qx,rot_qy,rot_qz,rot_qw,yaw,pitch,roll," +
                    "vel_x,vel_y,vel_z,speed,vel_forward,vel_lateral,vel_vertical," +
                    "accel_x,accel_y,accel_z,accel_mag,jerk,yaw_rate,pitch_rate,roll_rate,ang_vel_y," +
                    "is_moving,is_stationary,is_turning,possible_hesitation,distance_traveled,stop_duration,movement_variability,pause_count,turn_duration,turn_angle," +
                    "mean_speed,std_speed,mean_acceleration,std_acceleration,rms_acceleration,jerk_mean,jerk_std,zero_crossing_rate," +
                    "dominant_frequency,locomotor_band_power,freeze_band_power,freeze_index," +
                    "rhythm_consistency,step_freq_hz,cadence_spm,last_step_length_m,vertical_bob_m,movement_effort,fog_confidence,fog_active,assistance_level");
            }

            if (logSteps)
            {
                stepsWriter = CreateWriter("steps.csv",
                    "wall_time,timestamp_ms,elapsed_s,session_id,participant_id,trial_type,notes,step_index,interval_s,step_length_m,step_freq_hz,cadence_spm,pos_x,pos_y,pos_z");
            }

            if (logEvents)
            {
                eventsWriter = CreateWriter("events.csv",
                    "wall_time,event_timestamp_ms,elapsed_s,session_id,participant_id,trial_type,event_type,event_duration,detail");
            }

            sessionStartTime = Time.time;
            sessionStartDateTime = DateTime.Now;
            nextSampleTime = 0f;
            nextFlushTime = Time.time + flushIntervalSeconds;
            isOpen = true;
            ResetDerivedFeatureState();

            LogEvent("session_start", $"folder={sessionFolder}");
            Debug.Log($"[TelemetryLogger] Logging to {sessionFolder}");
        }

        private void CloseSession()
        {
            if (!isOpen)
            {
                return;
            }

            LogEvent("session_end", "");

            if (subscribedProcessor != null)
            {
                subscribedProcessor.StepDetected -= HandleStepDetected;
                subscribedProcessor = null;
            }

            SafeClose(ref samplesWriter);
            SafeClose(ref stepsWriter);
            SafeClose(ref eventsWriter);
            isOpen = false;
        }

        public void SampleFrame(SensorFrame sensor, ProcessedFrame processed, FoGDetectionResult detection, CueState cue)
        {
            if (!isOpen || !logSamples || samplesWriter == null)
            {
                return;
            }

            if (!sensor.IsValid)
            {
                return;
            }

            float now = Time.time;
            if (now < nextSampleTime)
            {
                MaybeFlush(now);
                return;
            }

            float period = sampleRateHz > 0f ? 1f / sampleRateHz : 0.033f;
            nextSampleTime = now + period;

            float ts = sensor.Timestamp - sessionStartTime;
            DateTime wall = sessionStartDateTime.AddSeconds(ts);
            long timestampMs = ToUnixMilliseconds(wall);
            Vector3 euler = sensor.Rotation.eulerAngles;
            float yaw = NormalizeSignedAngle(euler.y);
            float pitch = NormalizeSignedAngle(euler.x);
            float roll = NormalizeSignedAngle(euler.z);

            float speed = sensor.WorldVelocity.magnitude;
            float accelMagnitude = sensor.WorldAcceleration.magnitude;
            float jerk = hasPreviousSamplePose
                ? (sensor.WorldAcceleration - previousSampleAcceleration).magnitude / Mathf.Max(period, 0.0001f)
                : 0f;

            float yawRate = hasPreviousSamplePose ? DeltaAngle(yaw, previousSampleYaw) / Mathf.Max(period, 0.0001f) : 0f;
            float pitchRate = hasPreviousSamplePose ? DeltaAngle(pitch, previousSamplePitch) / Mathf.Max(period, 0.0001f) : 0f;
            float rollRate = hasPreviousSamplePose ? DeltaAngle(roll, previousSampleRoll) / Mathf.Max(period, 0.0001f) : 0f;

            cumulativeDistance += hasPreviousSamplePose
                ? Vector3.Distance(sensor.Position, previousSamplePosition)
                : 0f;

            bool isMoving = speed >= movementSpeedThreshold;
            bool isStationary = !isMoving;
            bool isTurning = Mathf.Abs(yawRate) >= turningRateThresholdDegPerSec;
            bool possibleHesitation = speed <= hesitationSpeedThreshold && jerk >= hesitationJerkThreshold;

            if (isStationary)
            {
                if (!isStationaryState)
                {
                    isStationaryState = true;
                    stationaryStartElapsed = ts;
                    pauseCount++;
                }
                stopDuration = Mathf.Max(0f, ts - stationaryStartElapsed);
            }
            else
            {
                isStationaryState = false;
                stationaryStartElapsed = -1f;
                stopDuration = 0f;
            }

            if (isTurning)
            {
                if (!isTurningState)
                {
                    isTurningState = true;
                    turningStartElapsed = ts;
                    turnAngle = 0f;
                }

                turnDuration = Mathf.Max(0f, ts - turningStartElapsed);
                turnAngle += Mathf.Abs(yawRate) * period;
            }
            else
            {
                isTurningState = false;
                turningStartElapsed = -1f;
                turnDuration = 0f;
                turnAngle = 0f;
            }

            windowSpeed.Enqueue(speed);
            windowAcceleration.Enqueue(accelMagnitude);
            windowJerk.Enqueue(jerk);
            windowYawRate.Enqueue(yawRate);
            TrimWindow();

            float meanSpeed = Mean(windowSpeed);
            float stdSpeed = StdDev(windowSpeed, meanSpeed);
            float meanAcceleration = Mean(windowAcceleration);
            float stdAcceleration = StdDev(windowAcceleration, meanAcceleration);
            float rmsAcceleration = Rms(windowAcceleration);
            float jerkMean = Mean(windowJerk);
            float jerkStd = StdDev(windowJerk, jerkMean);
            float zeroCrossingRate = ZeroCrossingRate(windowYawRate);
            ComputeFrequencyFeatures(windowAcceleration, out float dominantFrequency, out float locomotorBandPower, out float freezeBandPower, out float freezeIndex);

            previousSamplePosition = sensor.Position;
            previousSampleAcceleration = sensor.WorldAcceleration;
            previousSampleYaw = yaw;
            previousSamplePitch = pitch;
            previousSampleRoll = roll;
            hasPreviousSamplePose = true;

            StringBuilder sb = scratch;
            sb.Clear();
            sb.Append(WallTime(ts)).Append(',');
            sb.Append(timestampMs).Append(',');
            sb.Append(ts.ToString("F4", Inv)).Append(',');
            sb.Append(SanitizeCsv(sessionId)).Append(',');
            sb.Append(SanitizeCsv(participantId)).Append(',');
            sb.Append(SanitizeCsv(trialType)).Append(',');
            sb.Append(SanitizeCsv(notes)).Append(',');
            sb.Append(sensor.ActiveSource).Append(',');
            sb.Append(sensor.Position.x.ToString("F4", Inv)).Append(',');
            sb.Append(sensor.Position.y.ToString("F4", Inv)).Append(',');
            sb.Append(sensor.Position.z.ToString("F4", Inv)).Append(',');
            sb.Append(sensor.Rotation.x.ToString("F6", Inv)).Append(',');
            sb.Append(sensor.Rotation.y.ToString("F6", Inv)).Append(',');
            sb.Append(sensor.Rotation.z.ToString("F6", Inv)).Append(',');
            sb.Append(sensor.Rotation.w.ToString("F6", Inv)).Append(',');
            sb.Append(yaw.ToString("F4", Inv)).Append(',');
            sb.Append(pitch.ToString("F4", Inv)).Append(',');
            sb.Append(roll.ToString("F4", Inv)).Append(',');
            sb.Append(sensor.WorldVelocity.x.ToString("F4", Inv)).Append(',');
            sb.Append(sensor.WorldVelocity.y.ToString("F4", Inv)).Append(',');
            sb.Append(sensor.WorldVelocity.z.ToString("F4", Inv)).Append(',');
            sb.Append(speed.ToString("F4", Inv)).Append(',');
            sb.Append(processed.ForwardVelocity.ToString("F4", Inv)).Append(',');
            sb.Append(processed.LateralSway.ToString("F4", Inv)).Append(',');
            sb.Append(processed.VerticalVelocity.ToString("F4", Inv)).Append(',');
            sb.Append(sensor.WorldAcceleration.x.ToString("F4", Inv)).Append(',');
            sb.Append(sensor.WorldAcceleration.y.ToString("F4", Inv)).Append(',');
            sb.Append(sensor.WorldAcceleration.z.ToString("F4", Inv)).Append(',');
            sb.Append(accelMagnitude.ToString("F4", Inv)).Append(',');
            sb.Append(jerk.ToString("F4", Inv)).Append(',');
            sb.Append(yawRate.ToString("F4", Inv)).Append(',');
            sb.Append(pitchRate.ToString("F4", Inv)).Append(',');
            sb.Append(rollRate.ToString("F4", Inv)).Append(',');
            sb.Append(sensor.LocalAngularVelocity.y.ToString("F4", Inv)).Append(',');
            sb.Append(isMoving ? 1 : 0).Append(',');
            sb.Append(isStationary ? 1 : 0).Append(',');
            sb.Append(isTurning ? 1 : 0).Append(',');
            sb.Append(possibleHesitation ? 1 : 0).Append(',');
            sb.Append(cumulativeDistance.ToString("F4", Inv)).Append(',');
            sb.Append(stopDuration.ToString("F4", Inv)).Append(',');
            sb.Append(stdSpeed.ToString("F4", Inv)).Append(',');
            sb.Append(pauseCount).Append(',');
            sb.Append(turnDuration.ToString("F4", Inv)).Append(',');
            sb.Append(turnAngle.ToString("F4", Inv)).Append(',');
            sb.Append(meanSpeed.ToString("F4", Inv)).Append(',');
            sb.Append(stdSpeed.ToString("F4", Inv)).Append(',');
            sb.Append(meanAcceleration.ToString("F4", Inv)).Append(',');
            sb.Append(stdAcceleration.ToString("F4", Inv)).Append(',');
            sb.Append(rmsAcceleration.ToString("F4", Inv)).Append(',');
            sb.Append(jerkMean.ToString("F4", Inv)).Append(',');
            sb.Append(jerkStd.ToString("F4", Inv)).Append(',');
            sb.Append(zeroCrossingRate.ToString("F4", Inv)).Append(',');
            sb.Append(dominantFrequency.ToString("F4", Inv)).Append(',');
            sb.Append(locomotorBandPower.ToString("F4", Inv)).Append(',');
            sb.Append(freezeBandPower.ToString("F4", Inv)).Append(',');
            sb.Append(freezeIndex.ToString("F4", Inv)).Append(',');
            sb.Append(processed.RhythmConsistency.ToString("F4", Inv)).Append(',');
            sb.Append(processed.StepFrequency.ToString("F4", Inv)).Append(',');
            sb.Append(processed.CadenceStepsPerMinute.ToString("F2", Inv)).Append(',');
            sb.Append(processed.LastStepLength.ToString("F4", Inv)).Append(',');
            sb.Append(processed.VerticalBobAmplitude.ToString("F4", Inv)).Append(',');
            sb.Append(processed.MovementEffort.ToString("F4", Inv)).Append(',');
            sb.Append(detection.Confidence.ToString("F4", Inv)).Append(',');
            sb.Append(detection.IsFoG ? 1 : 0).Append(',');
            sb.Append(cue.AssistanceLevel.ToString("F4", Inv));

            samplesWriter.WriteLine(sb.ToString());
            MaybeFlush(now);
        }

        public void LogEvent(string eventType, string detail)
        {
            if (!isOpen || !logEvents || eventsWriter == null)
            {
                return;
            }

            float ts = Time.time - sessionStartTime;
            string safeDetail = string.IsNullOrEmpty(detail) ? "" : detail.Replace(',', ';').Replace('\n', ' ');
            DateTime wall = sessionStartDateTime.AddSeconds(ts);
            long timestampMs = ToUnixMilliseconds(wall);
            float eventDuration = ResolveEventDuration(eventType, ts);
            eventsWriter.WriteLine(string.Format(Inv, "{0},{1},{2:F4},{3},{4},{5},{6},{7:F4},{8}",
                WallTime(ts), timestampMs, ts,
                SanitizeCsv(sessionId),
                SanitizeCsv(participantId),
                SanitizeCsv(trialType),
                eventType,
                eventDuration,
                safeDetail));
        }

        public void LogCuePlaced(int cueIndex, CueState state, Vector3 position)
        {
            string cueType = state.IsAssistive ? "assistive" : "normal";
            string detail = string.Format(Inv,
                "index={0};type={1};assist={2:F2};spacing={3:F2};brightness={4:F2};pos=({5:F3};{6:F3};{7:F3})",
                cueIndex, cueType, state.AssistanceLevel, state.Spacing, state.Brightness,
                position.x, position.y, position.z);
            LogEvent("cue_placed", detail);
        }

        private void HandleStepDetected(StepEvent step)
        {
            if (!isOpen || !logSteps || stepsWriter == null)
            {
                return;
            }

            float ts = step.Timestamp - sessionStartTime;
            DateTime wall = sessionStartDateTime.AddSeconds(ts);
            long timestampMs = ToUnixMilliseconds(wall);
            stepsWriter.WriteLine(string.Format(Inv,
                "{0},{1},{2:F4},{3},{4},{5},{6},{7:F4},{8:F4},{9:F4},{10:F2},{11:F4},{12:F4},{13:F4}",
                WallTime(ts), timestampMs, ts,
                SanitizeCsv(sessionId), SanitizeCsv(participantId), SanitizeCsv(trialType), SanitizeCsv(notes),
                step.StepIndex, step.Interval, step.StepLength,
                step.StepFrequency, step.CadenceStepsPerMinute,
                step.Position.x, step.Position.y, step.Position.z));
        }

        private StreamWriter CreateWriter(string fileName, string header)
        {
            string path = Path.Combine(sessionFolder, fileName);
            StreamWriter writer = new StreamWriter(path, false, Encoding.UTF8);
            writer.WriteLine(header);
            return writer;
        }

        private void MaybeFlush(float now)
        {
            if (now < nextFlushTime)
            {
                return;
            }

            samplesWriter?.Flush();
            stepsWriter?.Flush();
            eventsWriter?.Flush();
            nextFlushTime = now + flushIntervalSeconds;
        }

        private static void SafeClose(ref StreamWriter writer)
        {
            if (writer == null)
            {
                return;
            }

            try
            {
                writer.Flush();
                writer.Dispose();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[TelemetryLogger] Error closing writer: {exception.Message}");
            }

            writer = null;
        }

        private string WallTime(float elapsed)
        {
            return sessionStartDateTime.AddSeconds(elapsed).ToString("yyyy-MM-dd HH:mm:ss.fff", Inv);
        }

        private void ResetDerivedFeatureState()
        {
            hasPreviousSamplePose = false;
            previousSamplePosition = Vector3.zero;
            previousSampleAcceleration = Vector3.zero;
            previousSampleYaw = 0f;
            previousSamplePitch = 0f;
            previousSampleRoll = 0f;
            cumulativeDistance = 0f;
            isStationaryState = false;
            stationaryStartElapsed = -1f;
            stopDuration = 0f;
            pauseCount = 0;
            isTurningState = false;
            turningStartElapsed = -1f;
            turnDuration = 0f;
            turnAngle = 0f;
            windowSpeed.Clear();
            windowAcceleration.Clear();
            windowJerk.Clear();
            windowYawRate.Clear();
            activeEventStarts.Clear();
        }

        private void TrimWindow()
        {
            int maxWindowSamples = Mathf.Max(4, Mathf.RoundToInt(featureWindowSeconds * sampleRateHz));
            while (windowSpeed.Count > maxWindowSamples)
            {
                windowSpeed.Dequeue();
            }

            while (windowAcceleration.Count > maxWindowSamples)
            {
                windowAcceleration.Dequeue();
            }

            while (windowJerk.Count > maxWindowSamples)
            {
                windowJerk.Dequeue();
            }

            while (windowYawRate.Count > maxWindowSamples)
            {
                windowYawRate.Dequeue();
            }
        }

        private static float Mean(IEnumerable<float> values)
        {
            float total = 0f;
            int count = 0;
            foreach (float value in values)
            {
                total += value;
                count++;
            }

            return count > 0 ? total / count : 0f;
        }

        private static float StdDev(IEnumerable<float> values, float mean)
        {
            float total = 0f;
            int count = 0;
            foreach (float value in values)
            {
                float diff = value - mean;
                total += diff * diff;
                count++;
            }

            return count > 1 ? Mathf.Sqrt(total / count) : 0f;
        }

        private static float Rms(IEnumerable<float> values)
        {
            float total = 0f;
            int count = 0;
            foreach (float value in values)
            {
                total += value * value;
                count++;
            }

            return count > 0 ? Mathf.Sqrt(total / count) : 0f;
        }

        private static float ZeroCrossingRate(IEnumerable<float> signal)
        {
            float[] values = ToArray(signal);
            if (values.Length < 2)
            {
                return 0f;
            }

            float mean = 0f;
            for (int i = 0; i < values.Length; i++)
            {
                mean += values[i];
            }
            mean /= values.Length;

            int crossings = 0;
            float prev = values[0] - mean;
            for (int i = 1; i < values.Length; i++)
            {
                float current = values[i] - mean;
                if ((prev > 0f && current < 0f) || (prev < 0f && current > 0f))
                {
                    crossings++;
                }
                prev = current;
            }

            return (float)crossings / (values.Length - 1);
        }

        private void ComputeFrequencyFeatures(
            IEnumerable<float> signal,
            out float dominantFrequency,
            out float locomotorBandPower,
            out float freezeBandPower,
            out float freezeIndex)
        {
            float[] values = ToArray(signal);
            dominantFrequency = 0f;
            locomotorBandPower = 0f;
            freezeBandPower = 0f;
            freezeIndex = 0f;

            if (values.Length < 8 || sampleRateHz <= 0f)
            {
                return;
            }

            float mean = 0f;
            for (int i = 0; i < values.Length; i++)
            {
                mean += values[i];
            }
            mean /= values.Length;

            for (int i = 0; i < values.Length; i++)
            {
                values[i] -= mean;
            }

            int halfN = values.Length / 2;
            float maxPower = 0f;
            for (int k = 1; k <= halfN; k++)
            {
                float frequency = (k * sampleRateHz) / values.Length;
                float real = 0f;
                float imag = 0f;
                for (int n = 0; n < values.Length; n++)
                {
                    float angle = (2f * Mathf.PI * k * n) / values.Length;
                    real += values[n] * Mathf.Cos(angle);
                    imag -= values[n] * Mathf.Sin(angle);
                }

                float power = (real * real + imag * imag) / values.Length;
                if (power > maxPower)
                {
                    maxPower = power;
                    dominantFrequency = frequency;
                }

                if (frequency >= 0.5f && frequency <= 3f)
                {
                    locomotorBandPower += power;
                }
                else if (frequency > 3f && frequency <= 8f)
                {
                    freezeBandPower += power;
                }
            }

            freezeIndex = freezeBandPower / Mathf.Max(locomotorBandPower, 0.0001f);
        }

        private static float[] ToArray(IEnumerable<float> values)
        {
            if (values is Queue<float> queue)
            {
                return queue.ToArray();
            }

            List<float> list = new List<float>();
            foreach (float value in values)
            {
                list.Add(value);
            }

            return list.ToArray();
        }

        private static string SanitizeCsv(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            return value.Replace(',', ';').Replace('\n', ' ').Replace('\r', ' ');
        }

        private static float NormalizeSignedAngle(float angle)
        {
            return Mathf.DeltaAngle(0f, angle);
        }

        private static float DeltaAngle(float from, float to)
        {
            return Mathf.DeltaAngle(from, to);
        }

        private float ResolveEventDuration(string eventType, float elapsed)
        {
            if (eventType.EndsWith("_onset", StringComparison.Ordinal))
            {
                string key = eventType.Substring(0, eventType.Length - "_onset".Length);
                activeEventStarts[key] = elapsed;
                return 0f;
            }

            if (eventType.EndsWith("_end", StringComparison.Ordinal))
            {
                string key = eventType.Substring(0, eventType.Length - "_end".Length);
                if (activeEventStarts.TryGetValue(key, out float startedAt))
                {
                    float duration = Mathf.Max(0f, elapsed - startedAt);
                    activeEventStarts.Remove(key);
                    return duration;
                }
            }

            return 0f;
        }

        private static long ToUnixMilliseconds(DateTime dateTime)
        {
            DateTimeOffset dto = new DateTimeOffset(dateTime);
            return dto.ToUnixTimeMilliseconds();
        }

        private static readonly StringBuilder scratch = new StringBuilder(256);
    }
}
