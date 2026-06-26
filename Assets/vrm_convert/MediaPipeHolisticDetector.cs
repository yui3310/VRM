using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

using Mediapipe;
using Mediapipe.Unity;
using Mediapipe.Unity.Experimental;
using Mediapipe.Unity.Sample;
using Mediapipe.Tasks.Core;
using Mediapipe.Tasks.Vision.HolisticLandmarker;

using MpContainers = Mediapipe.Tasks.Components.Containers;
using VisionRunningMode = Mediapipe.Tasks.Vision.Core.RunningMode;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace CCMediaPipe
{
    /// <summary>
    /// In-Unity MediaPipe Holistic detector. Runs the MediaPipe Unity Plugin's
    /// HolisticLandmarker (Tasks API) directly against a webcam and exposes the
    /// results through <see cref="IMediaPipeReceiver"/>, so the existing
    /// <see cref="HolisticToVRMBoneMapper"/> can consume them unchanged.
    ///
    /// This is a drop-in replacement for the Python + UDP pipeline: no external
    /// process, no socket. Attach it (or let VRMHolisticController add it) to a
    /// GameObject and the detection runs entirely inside the Unity player.
    /// </summary>
    public class MediaPipeHolisticDetector : MonoBehaviour, IMediaPipeReceiver
    {
        [Header("Model")]
        [Tooltip("Holistic landmarker model bundled with the MediaPipe Unity package.")]
        public string modelFileName = "holistic_landmarker.bytes";

        [Header("WebCam")]
        [Tooltip("Preferred capture width; the closest available resolution is chosen.")]
        public int preferredWebCamWidth = 1280;
        [Tooltip("Index into the available webcam device list.")]
        public int webCamDeviceIndex = 0;

        [Header("Detection confidence")]
        [Range(0f, 1f)] public float minPoseDetectionConfidence = 0.5f;
        [Range(0f, 1f)] public float minPosePresenceConfidence = 0.5f;
        [Range(0f, 1f)] public float minFaceDetectionConfidence = 0.5f;
        [Range(0f, 1f)] public float minFaceLandmarksConfidence = 0.5f;
        [Range(0f, 1f)] public float minHandLandmarksConfidence = 0.5f;

        [Header("Debug")]
        [Tooltip("Log MediaPipe stderr output to the Unity console.")]
        public bool logMediaPipeOutput = false;

        public bool IsConnected => _isConnected;

        private volatile bool _isConnected;
        private volatile bool _running;

        private HolisticLandmarker _taskApi;
        private WebCamSource _imageSource;
        private TextureFramePool _framePool;
        private Coroutine _runCoroutine;
        private readonly Stopwatch _stopwatch = new Stopwatch();

        private HolisticFrame _latestFrame;
        private readonly object _frameLock = new object();

        // MediaPipe's global logging/resource setup must happen exactly once
        // per process. Guard it so multiple detectors (or a sample Bootstrap)
        // don't double-initialize the native libraries.
        private static bool _mediaPipeInitialized;

        // True only when THIS detector performed the one-time glog/log-handler
        // initialization (i.e. there was no sample Bootstrap to defer to). In
        // that case we own the matching teardown: the native glog library
        // aborts the process if InitGoogleLogging() runs twice, so every
        // Initialize() must be paired with a Shutdown() before the next Play.
        private bool _ownsGlogInit;

        // ---- IMediaPipeReceiver ----

        public void Start()
        {
            if (_running) return;
            _running = true;
            _runCoroutine = StartCoroutine(Run());
        }

        public void Stop()
        {
            _running = false;
            _isConnected = false;

            if (_runCoroutine != null)
            {
                StopCoroutine(_runCoroutine);
                _runCoroutine = null;
            }

            _imageSource?.Stop();

            try { _taskApi?.Close(); } catch { }
            _taskApi = null;

            _framePool?.Dispose();
            _framePool = null;

            lock (_frameLock) { _latestFrame = null; }

            // If we performed the one-time native init, we must tear it down so
            // the next Play session can call Glog.Initialize() again. Without
            // this, the native glog library is still initialized from the prior
            // session and InitGoogleLogging() aborts the process on second Play.
            if (_ownsGlogInit)
            {
                try { Protobuf.ResetLogHandler(); } catch { }
                try { Glog.Shutdown(); } catch { }
                _ownsGlogInit = false;
                _mediaPipeInitialized = false;
            }
        }

        public HolisticFrame TryGetLatestFrame()
        {
            lock (_frameLock)
            {
                if (_latestFrame == null) return null;
                var frame = _latestFrame;
                _latestFrame = null;
                return frame;
            }
        }

        public void Dispose() => Stop();

        private void OnDestroy() => Stop();
        private void OnApplicationQuit() => Stop();

        // ---- Main loop ----

        private IEnumerator Run()
        {
            yield return InitializeMediaPipe();

            // Stage the model file where the native resolver can read it.
            yield return AssetLoader.PrepareAssetAsync(modelFileName);

            var options = BuildOptions(OnHolisticDetectionOutput);
            _taskApi = HolisticLandmarker.CreateFromOptions(options, GpuManager.GpuResources);

            _imageSource = new WebCamSource(preferredWebCamWidth, DefaultWebCamResolutions());
            if (_imageSource.sourceCandidateNames != null && _imageSource.sourceCandidateNames.Length > webCamDeviceIndex)
            {
                _imageSource.SelectSource(webCamDeviceIndex);
            }

            yield return _imageSource.Play();

            if (!_imageSource.isPrepared)
            {
                Debug.LogError("[MediaPipeHolisticDetector] Failed to start the webcam. " +
                    "Check camera permissions and that a device is connected.");
                _running = false;
                yield break;
            }

            _framePool = new TextureFramePool(_imageSource.textureWidth, _imageSource.textureHeight, TextureFormat.RGBA32, 10);
            _isConnected = true;
            _stopwatch.Restart();

            var transformationOptions = _imageSource.GetTransformationOptions();
            var flipHorizontally = transformationOptions.flipHorizontally;
            var flipVertically = transformationOptions.flipVertically;

            AsyncGPUReadbackRequest req = default;
            var waitUntilReqDone = new WaitUntil(() => req.done);

            Debug.Log($"[MediaPipeHolisticDetector] Detection started ({_imageSource.textureWidth}x{_imageSource.textureHeight}). " +
                "Driving the VRM directly from Unity (no Python).");

            while (_running)
            {
                if (!_framePool.TryGetTextureFrame(out var textureFrame))
                {
                    yield return new WaitForEndOfFrame();
                    continue;
                }

                req = textureFrame.ReadTextureAsync(_imageSource.GetCurrentTexture(), flipHorizontally, flipVertically);
                yield return waitUntilReqDone;

                if (req.hasError)
                {
                    Debug.LogWarning("[MediaPipeHolisticDetector] Failed to read a frame from the webcam.");
                    textureFrame.Release();
                    continue;
                }

                var image = textureFrame.BuildCPUImage();
                textureFrame.Release();

                // LIVE_STREAM mode: results arrive asynchronously on the
                // configured result callback. Timestamps must increase.
                _taskApi.DetectAsync(image, GetCurrentTimestampMillisec());
            }
        }

        private long GetCurrentTimestampMillisec()
            => _stopwatch.IsRunning ? _stopwatch.ElapsedTicks / TimeSpan.TicksPerMillisecond : 0;

        private HolisticLandmarkerOptions BuildOptions(HolisticLandmarkerOptions.ResultCallback resultCallback)
        {
            // Windows/macOS editors and standalone builds only ship the CPU
            // delegate, matching the MediaPipe Unity sample behavior.
            var delegateType =
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN || UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
                BaseOptions.Delegate.CPU;
#else
                BaseOptions.Delegate.CPU;
#endif

            return new HolisticLandmarkerOptions(
                new BaseOptions(delegateType, modelAssetPath: modelFileName),
                runningMode: VisionRunningMode.LIVE_STREAM,
                minFaceDetectionConfidence: minFaceDetectionConfidence,
                minFaceLandmarksConfidence: minFaceLandmarksConfidence,
                minPoseDetectionConfidence: minPoseDetectionConfidence,
                minPoseLandmarksConfidence: minPosePresenceConfidence,
                minHandLandmarksConfidence: minHandLandmarksConfidence,
                outputFaceBlendshapes: false,
                outputSegmentationMask: false,
                resultCallback: resultCallback
            );
        }

        private IEnumerator InitializeMediaPipe()
        {
            // The MediaPipe native library uses glog, which aborts the process
            // if InitGoogleLogging() is called twice. The sample Bootstrap (if
            // present in the scene) already initializes glog and the resource
            // manager, so defer to it instead of initializing a second time.
            var bootstrap = FindObjectOfType<Bootstrap>();
            if (bootstrap != null)
            {
                yield return new WaitUntil(() => bootstrap.isFinished);
                _mediaPipeInitialized = true;
                yield break;
            }

            // No Bootstrap in the scene: do the one-time global init ourselves,
            // guarded so multiple detectors don't double-initialize.
            if (!_mediaPipeInitialized)
            {
                Glog.Logtostderr = logMediaPipeOutput;
                Protobuf.SetLogHandler(Protobuf.DefaultLogHandler);
                Glog.Initialize("MediaPipeUnityPlugin");

#if UNITY_EDITOR
                AssetLoader.Provide(new LocalResourceManager());
#else
                AssetLoader.Provide(new StreamingAssetsResourceManager());
#endif
                _mediaPipeInitialized = true;
                // We own this init, so we are responsible for the paired
                // Glog.Shutdown()/ResetLogHandler() in Stop().
                _ownsGlogInit = true;
            }
        }

        private static ImageSource.ResolutionStruct[] DefaultWebCamResolutions()
        {
            return new[]
            {
                new ImageSource.ResolutionStruct(640, 480, 30),
                new ImageSource.ResolutionStruct(848, 480, 30),
                new ImageSource.ResolutionStruct(960, 540, 30),
                new ImageSource.ResolutionStruct(1280, 720, 30),
                new ImageSource.ResolutionStruct(1920, 1080, 30),
            };
        }

        // ---- Result conversion (worker thread) ----

        private void OnHolisticDetectionOutput(in HolisticLandmarkerResult result, Image image, long timestamp)
        {
            var frame = ConvertResult(result, timestamp);
            lock (_frameLock) { _latestFrame = frame; }
        }

        private static HolisticFrame ConvertResult(in HolisticLandmarkerResult result, long timestamp)
        {
            var frame = new HolisticFrame { timestamp = timestamp };

            var poseNorm = result.poseLandmarks.landmarks;
            var poseWorld = result.poseWorldLandmarks.landmarks;

            if (HasCount(poseNorm, HolisticFrame.PoseLandmarkCount))
            {
                frame.poseLandmarks = ToArrayNormalized(poseNorm, HolisticFrame.PoseLandmarkCount);
            }

            if (HasCount(poseWorld, HolisticFrame.PoseLandmarkCount))
            {
                // World landmarks may not carry visibility, but the mapper gates
                // on it. Borrow visibility from the normalized pose landmarks so
                // the gating matches the previous Python pipeline.
                frame.poseWorldLandmarks = ToArrayWorld(
                    poseWorld,
                    HasCount(poseNorm, HolisticFrame.PoseLandmarkCount) ? poseNorm : null,
                    HolisticFrame.PoseLandmarkCount);
            }

            var faceNorm = result.faceLandmarks.landmarks;
            if (HasCount(faceNorm, HolisticFrame.FaceLandmarkCount))
            {
                frame.faceLandmarks = ToArrayNormalized(faceNorm, HolisticFrame.FaceLandmarkCount);
            }

            var leftHand = result.leftHandLandmarks.landmarks;
            if (HasCount(leftHand, HolisticFrame.HandLandmarkCount))
            {
                frame.leftHandLandmarks = ToArrayNormalized(leftHand, HolisticFrame.HandLandmarkCount);
            }

            // World hand landmarks are metric 3D coordinates. The bone mapper
            // prefers these (BestLeftHand/BestRightHand) and aims the finger
            // bones in 3D, where curl is largely a depth (z) change. Image-space
            // normalized landmarks have poor z, so a fist read from them lands at
            // the wrong angle. Provide the world landmarks so curl is correct.
            var leftHandWorld = result.leftHandWorldLandmarks.landmarks;
            if (HasCount(leftHandWorld, HolisticFrame.HandLandmarkCount))
            {
                frame.leftHandWorldLandmarks = ToArrayWorld(leftHandWorld, leftHand, HolisticFrame.HandLandmarkCount);
            }

            var rightHand = result.rightHandLandmarks.landmarks;
            if (HasCount(rightHand, HolisticFrame.HandLandmarkCount))
            {
                frame.rightHandLandmarks = ToArrayNormalized(rightHand, HolisticFrame.HandLandmarkCount);
            }

            var rightHandWorld = result.rightHandWorldLandmarks.landmarks;
            if (HasCount(rightHandWorld, HolisticFrame.HandLandmarkCount))
            {
                frame.rightHandWorldLandmarks = ToArrayWorld(rightHandWorld, rightHand, HolisticFrame.HandLandmarkCount);
            }

            return frame;
        }

        private static bool HasCount<T>(List<T> list, int count) => list != null && list.Count >= count;

        private static Landmark[] ToArrayNormalized(List<MpContainers.NormalizedLandmark> src, int count)
        {
            var arr = new Landmark[count];
            for (int i = 0; i < count; i++)
            {
                var lm = src[i];
                arr[i] = new Landmark
                {
                    x = lm.x,
                    y = lm.y,
                    z = lm.z,
                    visibility = lm.visibility ?? lm.presence ?? 1f,
                };
            }
            return arr;
        }

        private static Landmark[] ToArrayWorld(List<MpContainers.Landmark> src, List<MpContainers.NormalizedLandmark> visibilitySource, int count)
        {
            var arr = new Landmark[count];
            for (int i = 0; i < count; i++)
            {
                var lm = src[i];
                float visibility = lm.visibility ?? lm.presence ?? 1f;
                if (visibilitySource != null)
                {
                    var v = visibilitySource[i];
                    visibility = v.visibility ?? v.presence ?? visibility;
                }
                arr[i] = new Landmark
                {
                    x = lm.x,
                    y = lm.y,
                    z = lm.z,
                    visibility = visibility,
                };
            }
            return arr;
        }
    }
}
