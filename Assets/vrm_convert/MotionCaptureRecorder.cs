using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CCMediaPipe
{
    /// <summary>Records and replays Holistic frames without baking them to a specific VRM rig.</summary>
    public class MotionCaptureRecorder : MonoBehaviour
    {
        [Tooltip("Controller whose live frames are recorded and whose model receives playback frames.")]
        public VRMHolisticController controller;
        [Tooltip("Maximum number of captured frames per second.")]
        [Range(1f, 60f)] public float captureFps = 30f;
        [Tooltip("Playback rate. 1 is real time, 0.5 is half speed, 2 is double speed.")]
        [Range(0.05f, 4f)] public float playbackSpeed = 1f;

        public bool IsRecording { get; private set; }
        public bool IsPlaying { get; private set; }
        public string CurrentPath { get; private set; }
        public float PlaybackTime { get; private set; }
        public float Duration { get; private set; }

        private readonly List<MotionCaptureFrame> frames = new List<MotionCaptureFrame>();
        private float recordClock;
        private float nextCapture;
        private float recordStartRealtime;
        private int playbackIndex;

        private void Awake()
        {
            if (controller == null)
                controller = FindObjectOfType<VRMHolisticController>();
            Directory.CreateDirectory(CaptureDirectory);
        }

        private string CaptureDirectory => Path.Combine(Application.persistentDataPath, "MotionCaptures");

        private void Update()
        {
            if (IsPlaying)
                TickPlayback();
        }

        /// <summary>Called by VRMHolisticController after a live frame is received.</summary>
        public void ObserveLiveFrame(HolisticFrame frame)
        {
            if (!IsRecording || frame == null)
                return;

            recordClock = Time.realtimeSinceStartup - recordStartRealtime;
            float interval = 1f / Mathf.Max(1f, captureFps);
            if (recordClock + 0.0001f < nextCapture)
                return;

            frames.Add(MotionCaptureFrame.From(frame, recordClock));
            nextCapture += interval;
            while (nextCapture <= recordClock)
                nextCapture += interval;
        }

        public void StartRecording()
        {
            StopPlayback();
            frames.Clear();
            recordClock = 0f;
            nextCapture = 0f;
            recordStartRealtime = Time.realtimeSinceStartup;
            CurrentPath = string.Empty;
            IsRecording = true;
        }

        public string StopRecordingAndSave(string optionalName = null)
        {
            if (!IsRecording)
                return CurrentPath;

            IsRecording = false;
            Duration = frames.Count > 0 ? frames[frames.Count - 1].time : 0f;
            string name = string.IsNullOrWhiteSpace(optionalName)
                ? "motion_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                : Path.GetFileNameWithoutExtension(optionalName);
            CurrentPath = Path.Combine(CaptureDirectory, name + ".json");
            int suffix = 2;
            while (File.Exists(CurrentPath))
            {
                CurrentPath = Path.Combine(CaptureDirectory, name + "_" + suffix + ".json");
                suffix++;
            }
            var clip = new MotionCaptureClip { captureFps = captureFps, frames = frames.ToArray() };
            File.WriteAllText(CurrentPath, JsonUtility.ToJson(clip));
            return CurrentPath;
        }

        public void LoadAndPlay(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;

            try
            {
                var clip = JsonUtility.FromJson<MotionCaptureClip>(File.ReadAllText(path));
                if (clip == null || clip.frames == null || clip.frames.Length == 0)
                    throw new InvalidDataException("The motion file contains no frames.");

                StopRecording();
                frames.Clear();
                frames.AddRange(clip.frames);
                Duration = frames[frames.Count - 1].time;
                CurrentPath = path;
                PlaybackTime = 0f;
                playbackIndex = 0;
                IsPlaying = true;
                if (Duration <= 0f)
                    Duration = 0.0001f;
                controller?.SetPlaybackOverride(true);
            }
            catch (Exception e)
            {
                Debug.LogError($"[MotionCaptureRecorder] Could not load '{path}': {e.Message}");
            }
        }

        public void StopPlayback()
        {
            IsPlaying = false;
            PlaybackTime = 0f;
            playbackIndex = 0;
            controller?.SetPlaybackOverride(false);
        }

        public void StopRecording()
        {
            IsRecording = false;
        }

        private void TickPlayback()
        {
            if (frames.Count == 0 || controller == null)
            {
                StopPlayback();
                return;
            }

            PlaybackTime += Time.deltaTime * Mathf.Max(0.05f, playbackSpeed);
            if (PlaybackTime >= Duration)
            {
                PlaybackTime = Duration;
                IsPlaying = false;
            }

            while (playbackIndex < frames.Count - 2 && frames[playbackIndex + 1].time <= PlaybackTime)
                playbackIndex++;

            var a = frames[playbackIndex];
            var b = frames[Mathf.Min(playbackIndex + 1, frames.Count - 1)];
            float span = b.time - a.time;
            float t = span > 0.0001f ? Mathf.Clamp01((PlaybackTime - a.time) / span) : 0f;
            controller.ApplyExternalFrame(MotionCaptureFrame.Lerp(a, b, t).ToHolisticFrame());

            if (!IsPlaying)
                controller.SetPlaybackOverride(false);
        }

        public static string GetCaptureDirectory()
        {
            return Path.Combine(Application.persistentDataPath, "MotionCaptures");
        }

        [Serializable] private class MotionCaptureClip
        {
            public float captureFps;
            public MotionCaptureFrame[] frames;
        }

        [Serializable]
        private class MotionCaptureFrame
        {
            public float time;
            public Landmark[] poseWorldLandmarks, poseLandmarks, faceLandmarks;
            public Landmark[] leftHandWorldLandmarks, rightHandWorldLandmarks;
            public Landmark[] leftHandLandmarks, rightHandLandmarks;

            public static MotionCaptureFrame From(HolisticFrame f, float time)
            {
                return new MotionCaptureFrame
                {
                    time = time,
                    poseWorldLandmarks = f.poseWorldLandmarks,
                    poseLandmarks = f.poseLandmarks,
                    faceLandmarks = f.faceLandmarks,
                    leftHandWorldLandmarks = f.leftHandWorldLandmarks,
                    rightHandWorldLandmarks = f.rightHandWorldLandmarks,
                    leftHandLandmarks = f.leftHandLandmarks,
                    rightHandLandmarks = f.rightHandLandmarks,
                    faceBlendshapeNames = GetBlendshapeNames(f),
                    faceBlendshapeValues = GetBlendshapeValues(f),
                };
            }

            public HolisticFrame ToHolisticFrame()
            {
                return new HolisticFrame
                {
                    timestamp = (long)(time * 1000f),
                    receivedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    poseWorldLandmarks = poseWorldLandmarks,
                    poseLandmarks = poseLandmarks,
                    faceLandmarks = faceLandmarks,
                    leftHandWorldLandmarks = leftHandWorldLandmarks,
                    rightHandWorldLandmarks = rightHandWorldLandmarks,
                    leftHandLandmarks = leftHandLandmarks,
                    rightHandLandmarks = rightHandLandmarks,
                    faceBlendshapes = BuildBlendshapes(faceBlendshapeNames, faceBlendshapeValues),
                };
            }

            public static MotionCaptureFrame Lerp(MotionCaptureFrame a, MotionCaptureFrame b, float t)
            {
                return new MotionCaptureFrame
                {
                    time = Mathf.Lerp(a.time, b.time, t),
                    poseWorldLandmarks = LerpArray(a.poseWorldLandmarks, b.poseWorldLandmarks, t),
                    poseLandmarks = LerpArray(a.poseLandmarks, b.poseLandmarks, t),
                    faceLandmarks = LerpArray(a.faceLandmarks, b.faceLandmarks, t),
                    leftHandWorldLandmarks = LerpArray(a.leftHandWorldLandmarks, b.leftHandWorldLandmarks, t),
                    rightHandWorldLandmarks = LerpArray(a.rightHandWorldLandmarks, b.rightHandWorldLandmarks, t),
                    leftHandLandmarks = LerpArray(a.leftHandLandmarks, b.leftHandLandmarks, t),
                    rightHandLandmarks = LerpArray(a.rightHandLandmarks, b.rightHandLandmarks, t),
                    faceBlendshapeNames = a.faceBlendshapeNames,
                    faceBlendshapeValues = LerpBlendshapeValues(a, b, t),
                };
            }

            private static string[] GetBlendshapeNames(HolisticFrame f)
            {
                if (!f.HasFaceBlendshapes) return null;
                var names = new string[f.faceBlendshapes.Count];
                f.faceBlendshapes.Keys.CopyTo(names, 0);
                return names;
            }

            private static float[] GetBlendshapeValues(HolisticFrame f)
            {
                if (!f.HasFaceBlendshapes) return null;
                var values = new float[f.faceBlendshapes.Count];
                int i = 0;
                foreach (var value in f.faceBlendshapes.Values) values[i++] = value;
                return values;
            }

            private static Dictionary<string, float> BuildBlendshapes(string[] names, float[] values)
            {
                if (names == null || values == null) return null;
                var result = new Dictionary<string, float>(names.Length);
                for (int i = 0; i < Mathf.Min(names.Length, values.Length); i++)
                    result[names[i]] = values[i];
                return result;
            }

            private static float[] LerpBlendshapeValues(MotionCaptureFrame a, MotionCaptureFrame b, float t)
            {
                if (a.faceBlendshapeNames == null || a.faceBlendshapeValues == null)
                    return b.faceBlendshapeValues;
                if (b.faceBlendshapeNames == null || b.faceBlendshapeValues == null)
                    return a.faceBlendshapeValues;

                var values = new float[a.faceBlendshapeNames.Length];
                for (int i = 0; i < values.Length; i++)
                {
                    float next = a.faceBlendshapeValues[i];
                    for (int j = 0; j < b.faceBlendshapeNames.Length; j++)
                    {
                        if (b.faceBlendshapeNames[j] == a.faceBlendshapeNames[i] &&
                            j < b.faceBlendshapeValues.Length)
                        {
                            next = b.faceBlendshapeValues[j];
                            break;
                        }
                    }
                    values[i] = Mathf.Lerp(a.faceBlendshapeValues[i], next, t);
                }
                return values;
            }

            private static Landmark[] LerpArray(Landmark[] a, Landmark[] b, float t)
            {
                if (a == null || a.Length == 0) return b;
                if (b == null || b.Length != a.Length) return a;
                var result = new Landmark[a.Length];
                for (int i = 0; i < result.Length; i++) result[i] = Landmark.Lerp(a[i], b[i], t);
                return result;
            }

            public string[] faceBlendshapeNames;
            public float[] faceBlendshapeValues;
        }
    }
}
