using System;
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

            if (logSamples)
            {
                samplesWriter = CreateWriter("samples.csv",
                    "wall_time,elapsed_s,source,pos_x,pos_y,pos_z,vel_world_mag,vel_forward,vel_lateral,vel_vertical," +
                    "accel_mag,ang_vel_y,rhythm_consistency,step_freq_hz,cadence_spm,last_step_length_m," +
                    "vertical_bob_m,movement_effort,fog_confidence,fog_active,assistance_level");
            }

            if (logSteps)
            {
                stepsWriter = CreateWriter("steps.csv",
                    "wall_time,elapsed_s,step_index,interval_s,step_length_m,step_freq_hz,cadence_spm,pos_x,pos_y,pos_z");
            }

            if (logEvents)
            {
                eventsWriter = CreateWriter("events.csv", "wall_time,elapsed_s,event_type,detail");
            }

            sessionStartTime = Time.time;
            sessionStartDateTime = DateTime.Now;
            nextSampleTime = 0f;
            nextFlushTime = Time.time + flushIntervalSeconds;
            isOpen = true;

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

            StringBuilder sb = scratch;
            sb.Clear();
            sb.Append(WallTime(ts)).Append(',');
            sb.Append(ts.ToString("F4", Inv)).Append(',');
            sb.Append(sensor.ActiveSource).Append(',');
            sb.Append(sensor.Position.x.ToString("F4", Inv)).Append(',');
            sb.Append(sensor.Position.y.ToString("F4", Inv)).Append(',');
            sb.Append(sensor.Position.z.ToString("F4", Inv)).Append(',');
            sb.Append(sensor.WorldVelocity.magnitude.ToString("F4", Inv)).Append(',');
            sb.Append(processed.ForwardVelocity.ToString("F4", Inv)).Append(',');
            sb.Append(processed.LateralSway.ToString("F4", Inv)).Append(',');
            sb.Append(processed.VerticalVelocity.ToString("F4", Inv)).Append(',');
            sb.Append(processed.AccelerationMagnitude.ToString("F4", Inv)).Append(',');
            sb.Append(sensor.LocalAngularVelocity.y.ToString("F4", Inv)).Append(',');
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
            eventsWriter.WriteLine(string.Format(Inv, "{0},{1:F4},{2},{3}", WallTime(ts), ts, eventType, safeDetail));
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
            stepsWriter.WriteLine(string.Format(Inv,
                "{0},{1:F4},{2},{3:F4},{4:F4},{5:F4},{6:F2},{7:F4},{8:F4},{9:F4}",
                WallTime(ts), ts, step.StepIndex, step.Interval, step.StepLength,
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

        private static readonly StringBuilder scratch = new StringBuilder(256);
    }
}
