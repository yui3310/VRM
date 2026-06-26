using UnityEngine;
using UniVRM10;

namespace CCMediaPipe
{
    /// <summary>
    /// Main controller: receives MediaPipe Holistic landmarks via UDP
    /// and drives a VRM model's bone transforms.
    ///
    /// Setup:
    /// 1. Attach this to a GameObject in the scene.
    /// 2. Drag the VRM model (the root with Animator) into the VrmModel field.
    /// 3. Ensure the Python MediaPipe sender is running on the same port.
    /// 4. Press Play.
    /// </summary>
    public class VRMHolisticController : MonoBehaviour
    {
        [Header("VRM Target")]
        [Tooltip("Root GameObject of the VRM model (must have an Animator).")]
        public GameObject vrmModel;

        [Header("Detection Source")]
        [Tooltip("Where landmark data comes from. UnityMediaPipe runs detection " +
            "inside Unity (no Python). Udp listens for the Python sender.")]
        public DetectionSource detectionSource = DetectionSource.UnityMediaPipe;
        [Tooltip("UDP port matching the Python sender (only used when Source = Udp).")]
        public int udpPort = 9999;
        [Tooltip("Index of the webcam device to use (only used when Source = UnityMediaPipe).")]
        public int webCamDeviceIndex = 0;

        [Header("Coordinate Mapping")]
        [Tooltip("Mirror MediaPipe X axis to Unity X.")]
        public bool mirrorX = true;
        [Tooltip("Invert MediaPipe Y (down) to Unity Y (up). Required to keep the model upright.")]
        public bool invertY = true;
        [Tooltip("Invert MediaPipe Z to Unity Z.")]
        public bool invertZ = true;
        [Tooltip("Scale factor for world landmarks → Unity world units.")]
        public float landmarkScale = 1.0f;

        [Header("Smoothing")]
        [Range(0f, 1f)]
        [Tooltip("Body rotation smoothing (0=instant, 0.9=heavy smoothing).")]
        public float bodySmoothing = 0.7f;
        [Range(0f, 1f)]
        [Tooltip("Hand/finger rotation smoothing.")]
        public float handSmoothing = 0.8f;

        [Header("Features")]
        [Tooltip("Drive VRM blend shapes from face landmarks (blink, etc.).")]
        public bool enableFaceBlendShapes = true;
        [Tooltip("Drive hand finger bones.")]
        public bool enableHands = true;

        private IMediaPipeReceiver receiver;
        private HolisticToVRMBoneMapper mapper;
        private Animator animator;
        private Vrm10Instance vrm10Instance;

        /// <summary>Selects how landmark frames are produced.</summary>
        public enum DetectionSource
        {
            /// <summary>Run MediaPipe Holistic inside Unity via the MediaPipe Unity Plugin.</summary>
            UnityMediaPipe,
            /// <summary>Receive landmarks over UDP from the external Python sender.</summary>
            Udp,
        }

        private void Start()
        {
            if (vrmModel == null)
            {
                Debug.LogError("[VRMHolisticController] VRM Model is not assigned.");
                enabled = false;
                return;
            }

            animator = vrmModel.GetComponent<Animator>();
            if (animator == null)
            {
                Debug.LogError("[VRMHolisticController] VRM Model has no Animator component.");
                enabled = false;
                return;
            }

            // Check if the model is humanoid
            if (!animator.isHuman)
            {
                Debug.LogWarning("[VRMHolisticController] Animator is not Humanoid. " +
                    "Bone mapping requires a Humanoid rig. Make sure the VRM was imported " +
                    "with Animation Type set to Humanoid.");
            }

            mapper = new HolisticToVRMBoneMapper(animator)
            {
                mirrorX = this.mirrorX,
                invertY = this.invertY,
                invertZ = this.invertZ,
                scale = this.landmarkScale,
                bodySmoothing = 1f - Mathf.Pow(0.01f, 1f - this.bodySmoothing),
                handSmoothing = 1f - Mathf.Pow(0.01f, 1f - this.handSmoothing),
            };

            receiver = CreateReceiver();

            // ---- Wire face expressions to VRM 1.0 ----
            if (enableFaceBlendShapes)
                TryWireVRMExpressions();

            receiver.Start();

            Debug.Log("[VRMHolisticController] Started. Waiting for MediaPipe data...");
        }

        private IMediaPipeReceiver CreateReceiver()
        {
            switch (detectionSource)
            {
                case DetectionSource.UnityMediaPipe:
                    var detector = gameObject.GetComponent<MediaPipeHolisticDetector>();
                    if (detector == null)
                        detector = gameObject.AddComponent<MediaPipeHolisticDetector>();
                    detector.webCamDeviceIndex = webCamDeviceIndex;
                    return detector;
                case DetectionSource.Udp:
                default:
                    return new UdpMediaPipeReceiver(udpPort);
            }
        }

        private void Update()
        {
            if (receiver == null || mapper == null) return;

            var frame = receiver.TryGetLatestFrame();
            if (frame == null) return;

            mapper.ApplyBodyPose(frame);

            if (enableHands)
                mapper.ApplyHands(frame);

            if (enableFaceBlendShapes)
                mapper.ApplyFace(frame);
        }

        private void OnDestroy()
        {
            if (mapper != null)
            {
                mapper.OnBlinkUpdated -= SetBlink;
                mapper.OnMouthOpenUpdated -= SetMouthOpen;
            }
            receiver?.Dispose();
        }

        private void OnApplicationQuit()
        {
            receiver?.Dispose();
        }

        // ---- VRM 1.0 expression bridge (UniVRM10) ----

        private void TryWireVRMExpressions()
        {
            vrm10Instance = vrmModel.GetComponent<Vrm10Instance>();
            if (vrm10Instance == null)
                vrm10Instance = vrmModel.GetComponentInChildren<Vrm10Instance>();

            if (vrm10Instance == null)
            {
                Debug.LogWarning("[VRMHolisticController] No Vrm10Instance found. " +
                    "Face expressions disabled. This controller targets VRM 1.0 (UniVRM10).");
                return;
            }

            mapper.OnBlinkUpdated += SetBlink;
            mapper.OnMouthOpenUpdated += SetMouthOpen;
            Debug.Log("[VRMHolisticController] VRM 1.0 expressions wired (blink + mouth).");
        }

        private void SetBlink(float value)
        {
            if (vrm10Instance == null) return;
            vrm10Instance.Runtime.Expression.SetWeight(ExpressionKey.Blink, value);
        }

        private void SetMouthOpen(float value)
        {
            if (vrm10Instance == null) return;
            vrm10Instance.Runtime.Expression.SetWeight(ExpressionKey.Aa, value);
        }
    }
}
