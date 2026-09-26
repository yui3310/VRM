using System;
using System.Collections.Generic;
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
        [Range(1f, 3f)]
        [Tooltip("Amplify MediaPipe blink input before it is sent to VRM expressions.")]
        public float blinkStrength = 1.4f;
        [Range(0.5f, 1f)]
        [Tooltip("Blink values above this become fully closed after amplification.")]
        public float blinkFullThreshold = 1f;
        [Range(0f, 1f)]
        [Tooltip("How much combined Blink is layered on top when both per-eye blinks are active.")]
        public float combinedBlinkLayer = 0f;
        [Tooltip("Drive hand finger bones.")]
        public bool enableHands = true;
        [Tooltip("Keep hips, spine and legs in their rest pose so the avatar " +
            "stays upright instead of leaning with the tracked torso. Arms, " +
            "head and face still track.")]
        public bool lockLowerBody = true;
        [Range(-45f, 45f)]
        [Tooltip("Extra head pitch in degrees. Increase if the mapped head sits " +
            "too low; positive lifts the head up.")]
        public float headPitchOffset = 0f;
        [Range(0f, 2f)]
        [Tooltip("How strongly head nodding (up/down pitch) is applied. 0 = no " +
            "nod (left/right only), 1 = full tracked nod.")]
        public float headPitchScale = 0.8f;

        [Header("Bone Mapping")]
        [Tooltip("Editable role-to-target Humanoid bone table for adapting different VRM rigs.")]
        public List<HolisticToVRMBoneMapper.BoneMapping> boneMappings =
            HolisticToVRMBoneMapper.CreateDefaultBoneMappings();
        [Tooltip("Show an on-screen runtime editor for changing Humanoid bone mappings while playing.")]
        public bool showRuntimeBoneMappingEditor = true;
        [Tooltip("Screen offset from the top-right corner for the runtime bone mapping editor (pixels).")]
        public Vector2 boneMappingEditorMargin = new Vector2(12f, 188f);

        [Header("Debug Skeleton")]
        [Tooltip("Draw the detected MediaPipe pose and hands over the VRM model.")]
        public bool showDetectedSkeleton = true;
        [Tooltip("Scale the debug skeleton to the model's approximate height so it fits the avatar size better.")]
        public bool autoScaleSkeletonToModel = true;
        [Range(0.5f, 2.5f)]
        [Tooltip("Fallback human height in meters used to convert the model's height into a display scale.")]
        public float skeletonHumanHeightReference = 1.70f;
        [Tooltip("Draw body pose landmarks in the debug skeleton.")]
        public bool showDetectedPose = true;
        [Tooltip("Draw hand landmarks in the debug skeleton.")]
        public bool showDetectedHands = true;
        [Range(0.001f, 0.05f)]
        public float skeletonJointRadius = 0.012f;
        [Range(0.001f, 0.03f)]
        public float skeletonLineWidth = 0.006f;
        [Range(0f, 1f)]
        public float skeletonAlpha = 0.9f;

        [Header("AI Tracking Monitor")]
        [Tooltip("Show pose/face/hand confidence, dropped frames and latency on screen.")]
        public bool showAITrackingMonitor = true;
        [Tooltip("Screen offset from the top-right corner for the AI tracking monitor (pixels).")]
        public Vector2 aiMonitorMargin = new Vector2(12f, 12f);
        [Range(1f, 60f)]
        [Tooltip("Expected tracking frame rate used to estimate dropped frames.")]
        public float expectedTrackingFps = 30f;
        [Range(0f, 1f)]
        [Tooltip("Confidence below this value is shown as warning-level tracking.")]
        public float trackingConfidenceWarning = 0.5f;
        [Range(0f, 1f)]
        [Tooltip("Confidence below this value is shown as poor tracking.")]
        public float trackingConfidencePoor = 0.25f;

        [Header("Camera Preview")]
        [Tooltip("Show the webcam image and 2D AI skeleton in the runtime settings UI.")]
        public bool showCameraPreview = true;
        [Tooltip("Draw body pose landmarks over the camera preview.")]
        public bool previewShowPose = true;
        [Tooltip("Draw hand landmarks over the camera preview.")]
        public bool previewShowHands = true;
        [Tooltip("Draw face landmarks over the camera preview.")]
        public bool previewShowFace = true;

        [Header("Debug")]
        [Tooltip("Enable the runtime AI tracking monitor and detected skeleton.")]
        public bool debugEnabled = true;

        private IMediaPipeReceiver receiver;
        private HolisticToVRMBoneMapper mapper;
        private Animator animator;
        private Vrm10Instance vrm10Instance;
        private HolisticSkeletonVisualizer skeletonVisualizer;
        private HolisticFrame latestFrame;
        private bool hasPerEyeBlink;
        private bool hasCombinedBlink;
        private bool expressionsWired;
        private int wiredExpressionCount;
        private string expressionStatus = "Not wired";
        private float nextExpressionWireAttemptRealtime;
        private float currentBlinkLeft;
        private float currentBlinkRight;
        private float skeletonModelScale = 1f;

        private TrackingStats trackingStats;
        private GUIStyle monitorPanelStyle;
        private GUIStyle monitorHeaderStyle;
        private GUIStyle monitorLabelStyle;
        private GUIStyle monitorValueStyle;
        private GUIStyle monitorGoodStyle;
        private GUIStyle monitorWarnStyle;
        private GUIStyle monitorPoorStyle;
        private GUIStyle monitorButtonStyle;
        private GUIStyle monitorPrimaryButtonStyle;
        private GUIStyle monitorSecondaryButtonStyle;
        private GUIStyle monitorMiniButtonStyle;
        private GUIStyle monitorToggleStyle;
        private GUIStyle monitorSubHeaderStyle;
        private GUIStyle monitorTinyStyle;
        private GUIStyle monitorRowStyle;
        private GUIStyle monitorRowActiveStyle;
        private GUIStyle monitorChipStyle;
        private Texture2D monitorBgTexture;
        private bool monitorStylesReady;
        private Vector2 boneMappingScroll;
        private Vector2 settingsScroll;
        private HumanBodyBones[] boneMappingEnumValues;
        private bool runtimeBoneMappingExpanded;
        private bool settingsExpanded;
        private float settingsPanelHeight = SettingsExpandedHeight;
        private float boneMappingPanelHeight = 470f;
        private float aiTrackingPanelHeight = 204f;
        private int resizingRightPanel;
        private float resizeStartMouseY;
        private float resizeStartPanelHeight;
        private bool playbackOverride;
        private MotionCaptureRecorder motionRecorder;

        private const float MonitorWidth = 260f;
        private const float SettingsPanelWidth = 228f;
        private const float SettingsCollapsedHeight = 40f;
        private const float SettingsExpandedHeight = 380f;
        private const float RightPanelGap = 8f;
        private const float CameraPreviewWidth = 440f;
        private const float CameraPreviewHeight = 340f;

        private static readonly Color UiBorder = new Color(1f, 1f, 1f, 0.07f);
        private static readonly Color UiRow = new Color(1f, 1f, 1f, 0.04f);
        private static readonly Color UiRowHover = new Color(1f, 1f, 1f, 0.10f);
        private static readonly Color UiAccent = new Color(0.31f, 0.64f, 1f, 1f);
        private static readonly Color UiAccentDim = new Color(0.31f, 0.64f, 1f, 0.18f);
        private static readonly Color UiAccentHover = new Color(0.42f, 0.71f, 1f, 1f);
        private static readonly Color UiText = new Color(0.92f, 0.93f, 0.96f, 1f);
        private static readonly Color UiTextDim = new Color(0.60f, 0.63f, 0.70f, 1f);

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
            motionRecorder = GetComponent<MotionCaptureRecorder>();
            if (motionRecorder == null)
                motionRecorder = gameObject.AddComponent<MotionCaptureRecorder>();
            motionRecorder.controller = this;
            // The receiver runs independent of which model is bound, so start it
            // once. Detection keeps producing frames even while no model is set.
            receiver = CreateReceiver();
            receiver.Start();

            if (vrmModel != null)
                BindModel(vrmModel);
            else
                Debug.Log("[VRMHolisticController] No model assigned yet. Drop a VRM or pick one from the UI.");

            Debug.Log("[VRMHolisticController] Started. Waiting for MediaPipe data...");
        }

        /// <summary>
        /// Point the controller at a new model at runtime. Re-resolves the
        /// Animator, rebuilds the bone mapper, and rewires VRM expressions.
        /// Safe to call repeatedly (e.g. when switching models from the UI).
        /// </summary>
        public void BindModel(GameObject model)
        {
            if (model == null)
            {
                Debug.LogWarning("[VRMHolisticController] BindModel called with null.");
                return;
            }

            // Detach expression callbacks from the previous model so the old one
            // stops receiving updates.
            UnwireMapperCallbacks();

            vrmModel = model;
            vrm10Instance = null;
            expressionsWired = false;
            wiredExpressionCount = 0;
            expressionStatus = "Waiting for VRM runtime";
            nextExpressionWireAttemptRealtime = 0f;
            currentBlinkLeft = 0f;
            currentBlinkRight = 0f;

            animator = vrmModel.GetComponent<Animator>();
            if (animator == null)
                animator = vrmModel.GetComponentInChildren<Animator>();
            if (animator == null)
            {
                Debug.LogError("[VRMHolisticController] Model has no Animator component.");
                mapper = null;
                return;
            }

            if (!animator.isHuman)
            {
                Debug.LogWarning("[VRMHolisticController] Animator is not Humanoid. " +
                    "Bone mapping requires a Humanoid rig.");
            }

            EnsureBoneMappings();
            mapper = new HolisticToVRMBoneMapper(animator, boneMappings)
            {
                mirrorX = this.mirrorX,
                invertY = this.invertY,
                invertZ = this.invertZ,
                scale = this.landmarkScale,
                // HolisticToVRMBoneMapper uses these values directly as the
                // retained fraction: 0 = instant, 1 = heavy smoothing.
                // Do not remap them here; the previous exponential conversion
                // inverted the Inspector behavior, so 0.9 remained very
                // responsive and exposed landmark jitter.
                bodySmoothing = this.bodySmoothing,
                handSmoothing = this.handSmoothing,
                lockLowerBody = this.lockLowerBody,
                headPitchOffset = this.headPitchOffset,
                headPitchScale = this.headPitchScale,
            };

            ConfigureSkeletonVisualizer();
            UpdateSkeletonModelScale();

            if (enableFaceBlendShapes)
                EnsureVRMExpressionsWired();

            Debug.Log($"[VRMHolisticController] Bound model '{vrmModel.name}'.");
        }

        public void ResetBoneMappingsToDefault()
        {
            boneMappings = HolisticToVRMBoneMapper.CreateDefaultBoneMappings();
            ApplyBoneMappingsToMapper();
        }

        public void EnsureBoneMappings()
        {
            if (boneMappings == null)
                boneMappings = new List<HolisticToVRMBoneMapper.BoneMapping>();

            var defaults = HolisticToVRMBoneMapper.CreateDefaultBoneMappings();
            foreach (var defaultMapping in defaults)
            {
                bool exists = false;
                for (int i = 0; i < boneMappings.Count; i++)
                {
                    if (boneMappings[i].role == defaultMapping.role)
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                    boneMappings.Add(defaultMapping);
            }
        }

        public void ApplyBoneMappingsToMapper()
        {
            EnsureBoneMappings();
            // A newly selected target needs its own rest-pose direction cache;
            // otherwise the direction-based mapper has no reference and skips it.
            mapper?.ApplyBoneMappings(boneMappings, true);
        }

        private void Reset()
        {
            ResetBoneMappingsToDefault();
        }

        private void OnValidate()
        {
            EnsureBoneMappings();
            if (Application.isPlaying)
                mapper?.ApplyBoneMappings(boneMappings, true);
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
            if (receiver == null) return;

            if (playbackOverride) return;

            RefreshTrackingFrameAge();

            var frame = receiver.TryGetLatestFrame();
            if (frame == null) return;

            latestFrame = frame;
            motionRecorder?.ObserveLiveFrame(frame);

            ApplyFrame(frame);
        }

        public void SetPlaybackOverride(bool enabled)
        {
            playbackOverride = enabled;
        }

        public void ApplyExternalFrame(HolisticFrame frame)
        {
            if (frame == null) return;
            latestFrame = frame;
            ApplyFrame(frame);
        }

        private void ApplyFrame(HolisticFrame frame)
        {

            UpdateTrackingStats(frame);

            // Keep the debug skeleton independent from the bone mapper. A
            // runtime-loaded VRM can briefly have no mapper while its Animator
            // is being resolved, but the tracking frame is already valid and
            // should still be visible on the model.
            if (mapper != null)
            {
                // Keep runtime-tunable knobs live so Inspector sliders take
                // effect while playing.
                mapper.lockLowerBody = lockLowerBody;
                mapper.headPitchOffset = headPitchOffset;
                mapper.headPitchScale = headPitchScale;

                mapper.ApplyBodyPose(frame);

                if (enableHands)
                    mapper.ApplyHands(frame);

                if (enableFaceBlendShapes)
                {
                    EnsureVRMExpressionsWired();
                    mapper.ApplyFace(frame);
                }
            }

            UpdateSkeletonVisualizer(frame);
        }

        private void OnGUI()
        {
            EnsureMonitorStyles();

            if (debugEnabled && showAITrackingMonitor)
                DrawAITrackingMonitor();
            if (debugEnabled && showRuntimeBoneMappingEditor)
                DrawRuntimeBoneMappingEditor();
            if (showCameraPreview)
                DrawCameraPreview();
            DrawSettingsPanel();
        }

        private void DrawAITrackingMonitor()
        {
            float width = MonitorWidth;
            float height = 204f;
            Rect panelRect = GetAITrackingRect(width, height);

            GUILayout.BeginArea(panelRect, GUIContent.none, monitorPanelStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label("AI Tracking", monitorHeaderStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label("LIVE", monitorChipStyle);
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);
            DrawConfidenceRow("Pose", trackingStats.poseConfidence);
            DrawConfidenceRow("Face", trackingStats.faceConfidence);
            DrawConfidenceRow("Hand", trackingStats.handConfidence);
            DrawMetricRow("FPS", trackingStats.actualFps > 0f ? trackingStats.actualFps.ToString("0.0") : "--");
            DrawMetricRow("Dropped", trackingStats.droppedFrames.ToString());
            DrawMetricRow("Latency", trackingStats.latencyMs >= 0f ? trackingStats.latencyMs.ToString("0") + " ms" : "--");
            DrawMetricRow("Last frame", trackingStats.lastFrameAgeMs >= 0f ? trackingStats.lastFrameAgeMs.ToString("0") + " ms" : "--");
            DrawMetricRow("Blend", latestFrame != null && latestFrame.HasFaceBlendshapes
                ? latestFrame.faceBlendshapes.Count.ToString()
                : "--");
            DrawMetricRow("Expr", expressionStatus);
            GUILayout.EndArea();
        }

        private void DrawRuntimeBoneMappingEditor()
        {
            EnsureBoneMappingEnumCache();
            EnsureBoneMappings();

            const float width = MonitorWidth;
            Rect panelRect = GetBoneMappingEditorRect(width);

            GUILayout.BeginArea(panelRect,
                GUIContent.none, monitorPanelStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(runtimeBoneMappingExpanded ? "v" : ">", monitorMiniButtonStyle, GUILayout.Width(28f)))
                runtimeBoneMappingExpanded = !runtimeBoneMappingExpanded;
            GUILayout.Label("Live Bone Mapping", monitorHeaderStyle);
            GUILayout.EndHorizontal();

            if (!runtimeBoneMappingExpanded)
            {
                GUILayout.EndArea();
                return;
            }

            GUILayout.Label("Change a target and it applies immediately.", monitorLabelStyle);

            float scrollHeight = Mathf.Max(60f, panelRect.height - 105f);
            boneMappingScroll = GUILayout.BeginScrollView(
                boneMappingScroll, false, true, GUILayout.Height(scrollHeight));
            for (int i = 0; i < boneMappings.Count; i++)
            {
                var mapping = boneMappings[i];
                GUILayout.BeginHorizontal(monitorRowStyle);
                GUILayout.Label(mapping.role.ToString(), monitorLabelStyle, GUILayout.Width(64f));

                if (GUILayout.Button("<", monitorMiniButtonStyle, GUILayout.Width(22f), GUILayout.Height(24f)))
                    CycleBoneMapping(i, -1);
                GUILayout.Label(mapping.target.ToString(), monitorValueStyle, GUILayout.Width(68f));
                if (GUILayout.Button(">", monitorMiniButtonStyle, GUILayout.Width(22f), GUILayout.Height(24f)))
                    CycleBoneMapping(i, 1);
                if (GUILayout.Button("Disable", monitorSecondaryButtonStyle, GUILayout.Width(58f), GUILayout.Height(24f)))
                    SetBoneMappingTarget(i, HumanBodyBones.LastBone);
                GUILayout.EndHorizontal();
                GUILayout.Space(2f);
            }
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset Defaults", monitorSecondaryButtonStyle, GUILayout.Height(26f)))
                ResetBoneMappingsToDefault();
            if (GUILayout.Button("Apply", monitorPrimaryButtonStyle, GUILayout.Height(26f)))
            {
                EnsureBoneMappings();
                ApplyBoneMappingsToMapper();
            }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        /// <summary>
        /// Returns true while the mouse is over the runtime bone mapping panel.
        /// ModelViewerCamera uses this to prevent IMGUI scroll input from
        /// leaking through to camera zoom.
        /// </summary>
        public bool IsPointerOverBoneMappingUI()
        {
            if (!debugEnabled || !showRuntimeBoneMappingEditor || !runtimeBoneMappingExpanded)
                return false;

            Vector3 mouse = Input.mousePosition;
            Vector2 guiPosition = new Vector2(mouse.x, Screen.height - mouse.y);
            return GetBoneMappingEditorRect(MonitorWidth).Contains(guiPosition);
        }

        private Rect GetBoneMappingEditorRect(float width)
        {
            const float collapsedHeight = 42f;
            const float expandedHeight = 470f;
            Rect settingsRect = GetSettingsPanelRect();
            float x = Mathf.Max(0f, Screen.width - width - aiMonitorMargin.x);
            float bottom = settingsRect.y - RightPanelGap;
            float minimumTop = Mathf.Max(0f, aiMonitorMargin.y + 204f + RightPanelGap);
            float availableHeight = bottom - minimumTop;
            float height = runtimeBoneMappingExpanded
                ? Mathf.Clamp(availableHeight, collapsedHeight, expandedHeight)
                : collapsedHeight;
            float y = Mathf.Max(0f, bottom - height);
            return new Rect(x, y, width, height);
        }

        private Rect GetAITrackingRect(float width, float height)
        {
            float bottom;
            if (debugEnabled && showRuntimeBoneMappingEditor)
                bottom = GetBoneMappingEditorRect(MonitorWidth).y - RightPanelGap;
            else
                bottom = GetSettingsPanelRect().y - RightPanelGap;

            float x = Mathf.Max(0f, Screen.width - width - aiMonitorMargin.x);
            float y = Mathf.Max(aiMonitorMargin.y, bottom - height);
            return new Rect(x, y, width, height);
        }

        private void DrawSettingsPanel()
        {
            Rect panelRect = GetSettingsPanelRect();

            GUILayout.BeginArea(panelRect, GUIContent.none, monitorPanelStyle);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Runtime Settings", monitorHeaderStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(settingsExpanded ? "-" : "+", monitorMiniButtonStyle, GUILayout.Width(24f), GUILayout.Height(22f)))
                settingsExpanded = !settingsExpanded;
            GUILayout.EndHorizontal();

            if (settingsExpanded)
            {
                float contentHeight = Mathf.Max(48f, panelRect.height - 38f);
                settingsScroll = GUILayout.BeginScrollView(
                    settingsScroll, false, true, GUILayout.Height(contentHeight));

                GUILayout.Space(4f);
                GUILayout.Label(debugEnabled ? "Tracking overlays are active" : "Tracking overlays are hidden", monitorTinyStyle);
                GUILayout.Space(5f);

                bool nextDebugEnabled = DrawSettingsToggle(debugEnabled, "Debug", "Tracking monitor and mapping tools");
                if (nextDebugEnabled != debugEnabled)
                    SetDebugEnabled(nextDebugEnabled);

                bool fullBodyEnabled = DrawSettingsToggle(
                    !lockLowerBody, "Full Body", "Hips, spine and legs follow tracking");
                bool previousLockLowerBody = lockLowerBody;
                lockLowerBody = !fullBodyEnabled;
                if (lockLowerBody && !previousLockLowerBody)
                    mapper?.ResetLowerBodyPose();

                showDetectedSkeleton = DrawSettingsToggle(
                    showDetectedSkeleton, "Model Skeleton", "AI skeleton over the avatar");

                bool nextPreview = DrawSettingsToggle(
                    showCameraPreview, "Camera Preview", "Live camera image and landmarks");
                if (nextPreview != showCameraPreview)
                    showCameraPreview = nextPreview;

                if (showCameraPreview)
                {
                    GUILayout.Space(3f);
                    GUILayout.Label("Preview Layers", monitorSubHeaderStyle);
                    previewShowPose = DrawSettingsToggle(previewShowPose, "Pose", "Body landmarks");
                    previewShowHands = DrawSettingsToggle(previewShowHands, "Hands", "Hand landmarks");
                    previewShowFace = DrawSettingsToggle(previewShowFace, "Face", "Face landmarks");
                }

                GUILayout.EndScrollView();
            }
            GUILayout.EndArea();
        }

        private bool DrawSettingsToggle(bool value, string title, string detail)
        {
            GUILayout.BeginHorizontal(value ? monitorRowActiveStyle : monitorRowStyle);
            GUILayout.BeginVertical();
            GUILayout.Label(title, monitorLabelStyle);
            GUILayout.Label(detail, monitorTinyStyle);
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            bool next = GUILayout.Toggle(value, value ? "ON" : "OFF", monitorToggleStyle,
                GUILayout.Width(48f), GUILayout.Height(28f));
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);
            return next;
        }

        private Rect GetSettingsPanelRect()
        {
            float height = CurrentSettingsPanelHeight();
            if (settingsExpanded)
            {
                // Keep enough room for the other right-column panels. The
                // settings content may become shorter on very small windows,
                // but panels must never be drawn on top of one another.
                float reserved = aiMonitorMargin.y + 204f +
                    RightPanelGap * 3f + 42f;
                height = Mathf.Min(height, Mathf.Max(SettingsCollapsedHeight,
                    Screen.height - 12f - reserved));
            }
            float x = Mathf.Max(0f, Screen.width - SettingsPanelWidth - aiMonitorMargin.x);
            float y = Mathf.Max(0f, Screen.height - height - 12f);
            return new Rect(x, y, SettingsPanelWidth, height);
        }

        private void DrawCameraPreview()
        {
            const float margin = 12f;
            float x = margin;
            float y = Mathf.Max(margin, Screen.height - CameraPreviewHeight - margin);
            var detector = receiver as MediaPipeHolisticDetector;
            Texture texture = detector != null ? detector.CameraTexture : null;

            GUILayout.BeginArea(new Rect(x, y, CameraPreviewWidth, CameraPreviewHeight),
                GUIContent.none, monitorPanelStyle);
            GUILayout.Label("Camera Preview", monitorHeaderStyle);

            Rect content = GUILayoutUtility.GetRect(
                CameraPreviewWidth - 24f, CameraPreviewHeight - 58f,
                GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            if (texture == null)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.12f);
                GUI.DrawTexture(content, Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(content, detector == null ? "Preview requires UnityMediaPipe source" : "Waiting for camera...", monitorLabelStyle);
                GUILayout.EndArea();
                return;
            }

            float aspect = texture.height > 0 ? (float)texture.width / texture.height : 4f / 3f;
            Rect imageRect = FitRect(content, aspect);
            // The detector's vertical transform is for the inference input. The
            // webcam texture is already presented in Unity's display orientation;
            // applying that flag again makes the visible preview upside down.
            // Keep the display vertically upright and use the same UV for the
            // landmark overlay so the skeleton remains aligned.
            Rect uv = new Rect(
                detector.CameraFlipHorizontally ? 1f : 0f,
                0f,
                detector.CameraFlipHorizontally ? -1f : 1f,
                1f);
            GUI.DrawTextureWithTexCoords(imageRect, texture, uv);
            DrawPreviewSkeleton(imageRect, uv);
            if (latestFrame != null)
            {
                string blend = latestFrame.HasFaceBlendshapes
                    ? "Blendshape " + latestFrame.faceBlendshapes.Count
                    : "Blendshape unavailable";
                GUI.Label(new Rect(imageRect.x + 8f, imageRect.y + 8f, imageRect.width - 16f, 22f),
                    blend + " | " + expressionStatus, monitorTinyStyle);
            }
            GUILayout.EndArea();
        }

        private Rect FitRect(Rect bounds, float aspect)
        {
            float width = bounds.width;
            float height = width / Mathf.Max(0.01f, aspect);
            if (height > bounds.height)
            {
                height = bounds.height;
                width = height * aspect;
            }
            return new Rect(
                bounds.x + (bounds.width - width) * 0.5f,
                bounds.y + (bounds.height - height) * 0.5f,
                width, height);
        }

        private void DrawPreviewSkeleton(Rect imageRect, Rect uv)
        {
            if (latestFrame == null) return;

            var pose = latestFrame.poseLandmarks;
            if (previewShowPose && pose != null && pose.Length == HolisticFrame.PoseLandmarkCount)
            {
                for (int i = 0; i < PreviewPoseConnections.GetLength(0); i++)
                    DrawPreviewLine(imageRect, pose[PreviewPoseConnections[i, 0]], pose[PreviewPoseConnections[i, 1]],
                        new Color(0.15f, 0.9f, 1f, 0.95f), uv);
                for (int i = 0; i < pose.Length; i++)
                    DrawPreviewJoint(imageRect, pose[i], new Color(0.15f, 0.9f, 1f, 0.95f), uv);
            }

            if (previewShowHands)
            {
                DrawPreviewHand(imageRect, latestFrame.leftHandLandmarks, new Color(0.35f, 1f, 0.35f, 0.95f), uv);
                DrawPreviewHand(imageRect, latestFrame.rightHandLandmarks, new Color(1f, 0.35f, 0.25f, 0.95f), uv);
            }

            if (previewShowFace)
                DrawPreviewFace(imageRect, latestFrame.faceLandmarks, new Color(1f, 0.82f, 0.20f, 0.90f), uv);
        }

        private void DrawPreviewFace(Rect imageRect, Landmark[] face, Color color, Rect uv)
        {
            if (face == null || face.Length != HolisticFrame.FaceLandmarkCount) return;

            // Keep the preview readable: MediaPipe's face contours cover the
            // oval, brows, eyes and lips without drawing the dense tesselation.
            for (int i = 0; i < PreviewFaceConnections.GetLength(0); i++)
                DrawPreviewLine(imageRect, face[PreviewFaceConnections[i, 0]], face[PreviewFaceConnections[i, 1]], color, uv);
            for (int i = 0; i < PreviewFacePoints.Length; i++)
                DrawPreviewJoint(imageRect, face[PreviewFacePoints[i]], color, uv);
        }

        private void DrawPreviewHand(Rect imageRect, Landmark[] hand, Color color, Rect uv)
        {
            if (hand == null || hand.Length != HolisticFrame.HandLandmarkCount) return;
            for (int i = 0; i < PreviewHandConnections.GetLength(0); i++)
                DrawPreviewLine(imageRect, hand[PreviewHandConnections[i, 0]], hand[PreviewHandConnections[i, 1]], color, uv);
            for (int i = 0; i < hand.Length; i++)
                DrawPreviewJoint(imageRect, hand[i], color, uv);
        }

        private void DrawPreviewLine(Rect imageRect, Landmark a, Landmark b, Color color, Rect uv)
        {
            Vector2 start = PreviewPoint(imageRect, a, uv);
            Vector2 end = PreviewPoint(imageRect, b, uv);
            Vector2 delta = end - start;
            float length = delta.magnitude;
            if (length < 1f) return;
            Color old = GUI.color;
            Matrix4x4 oldMatrix = GUI.matrix;
            GUI.color = color;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg, start);
            GUI.DrawTexture(new Rect(start.x, start.y - 1.5f, length, 3f), Texture2D.whiteTexture);
            GUI.matrix = oldMatrix;
            GUI.color = old;
        }

        private void DrawPreviewJoint(Rect imageRect, Landmark landmark, Color color, Rect uv)
        {
            Vector2 point = PreviewPoint(imageRect, landmark, uv);
            Color old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(point.x - 3.5f, point.y - 3.5f, 7f, 7f), Texture2D.whiteTexture);
            GUI.color = old;
        }

        private Vector2 PreviewPoint(Rect imageRect, Landmark landmark, Rect uv)
        {
            // The inference image already has the detector's horizontal flip
            // applied. The preview texture uses the matching mirrored UV, so
            // landmark X needs no second mirroring here.
            float x = landmark.x;
            float y = uv.height < 0f ? 1f - landmark.y : landmark.y;
            return new Vector2(imageRect.x + x * imageRect.width, imageRect.y + y * imageRect.height);
        }

        private static readonly int[,] PreviewPoseConnections =
        {
            { 11, 12 }, { 11, 13 }, { 13, 15 }, { 12, 14 }, { 14, 16 },
            { 11, 23 }, { 12, 24 }, { 23, 24 }, { 23, 25 }, { 25, 27 },
            { 24, 26 }, { 26, 28 }, { 27, 29 }, { 28, 30 }, { 29, 31 },
            { 30, 32 }, { 0, 11 }, { 0, 12 }
        };

        private static readonly int[,] PreviewHandConnections =
        {
            { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 4 },
            { 0, 5 }, { 5, 6 }, { 6, 7 }, { 7, 8 },
            { 0, 9 }, { 9, 10 }, { 10, 11 }, { 11, 12 },
            { 0, 13 }, { 13, 14 }, { 14, 15 }, { 15, 16 },
            { 0, 17 }, { 17, 18 }, { 18, 19 }, { 19, 20 },
            { 5, 9 }, { 9, 13 }, { 13, 17 }
        };

        private static readonly int[] PreviewFacePoints =
        {
            10, 152, 234, 454, 70, 107, 336, 300,
            33, 133, 362, 263, 61, 291, 13, 14,
            1, 168
        };

        private static readonly int[,] PreviewFaceConnections =
        {
            { 0, 267 }, { 7, 163 }, { 10, 338 }, { 13, 312 }, { 14, 317 }, { 17, 314 },
            { 21, 54 }, { 33, 7 }, { 33, 246 }, { 37, 0 }, { 39, 37 }, { 40, 39 },
            { 46, 53 }, { 52, 65 }, { 53, 52 }, { 54, 103 }, { 58, 132 }, { 61, 146 },
            { 61, 185 }, { 63, 105 }, { 65, 55 }, { 66, 107 }, { 67, 109 }, { 70, 63 },
            { 78, 95 }, { 78, 191 }, { 80, 81 }, { 81, 82 }, { 82, 13 }, { 84, 17 },
            { 87, 14 }, { 88, 178 }, { 91, 181 }, { 93, 234 }, { 95, 88 }, { 103, 67 },
            { 105, 66 }, { 109, 10 }, { 127, 162 }, { 132, 93 }, { 136, 172 }, { 144, 145 },
            { 145, 153 }, { 146, 91 }, { 148, 176 }, { 149, 150 }, { 150, 136 }, { 152, 148 },
            { 153, 154 }, { 154, 155 }, { 155, 133 }, { 157, 173 }, { 158, 157 }, { 159, 158 },
            { 160, 159 }, { 161, 160 }, { 162, 21 }, { 163, 144 }, { 172, 58 }, { 173, 133 },
            { 176, 149 }, { 178, 87 }, { 181, 84 }, { 185, 40 }, { 191, 80 }, { 234, 127 },
            { 246, 161 }, { 249, 390 }, { 251, 389 }, { 263, 249 }, { 263, 466 }, { 267, 269 },
            { 269, 270 }, { 270, 409 }, { 276, 283 }, { 282, 295 }, { 283, 282 }, { 284, 251 },
            { 288, 397 }, { 293, 334 }, { 295, 285 }, { 296, 336 }, { 297, 332 }, { 300, 293 },
            { 310, 415 }, { 311, 310 }, { 312, 311 }, { 314, 405 }, { 317, 402 }, { 318, 324 },
            { 321, 375 }, { 323, 361 }, { 324, 308 }, { 332, 284 }, { 334, 296 }, { 338, 297 },
            { 356, 454 }, { 361, 288 }, { 365, 379 }, { 373, 374 }, { 374, 380 }, { 375, 291 },
            { 377, 152 }, { 378, 400 }, { 379, 378 }, { 380, 381 }, { 381, 382 }, { 382, 362 },
            { 384, 398 }, { 385, 384 }, { 386, 385 }, { 387, 386 }, { 388, 387 }, { 389, 356 },
            { 390, 373 }, { 397, 365 }, { 398, 362 }, { 400, 377 }, { 402, 318 }, { 405, 321 },
            { 409, 291 }, { 415, 308 }, { 454, 323 }, { 466, 388 }, { 168, 6 }, { 6, 197 },
            { 197, 195 }, { 195, 5 }, { 5, 4 }, { 4, 1 }
        };

        private float CurrentSettingsPanelHeight()
        {
            return settingsExpanded ? SettingsExpandedHeight : SettingsCollapsedHeight;
        }

        private void SetDebugEnabled(bool enabled)
        {
            debugEnabled = enabled;
            if (skeletonVisualizer != null)
                skeletonVisualizer.gameObject.SetActive(showDetectedSkeleton);
        }

        private void EnsureBoneMappingEnumCache()
        {
            if (boneMappingEnumValues != null)
                return;

            var values = (HumanBodyBones[])Enum.GetValues(typeof(HumanBodyBones));
            boneMappingEnumValues = values;
        }

        private void CycleBoneMapping(int index, int direction)
        {
            if (index < 0 || index >= boneMappings.Count)
                return;

            int current = Array.IndexOf(boneMappingEnumValues, boneMappings[index].target);
            if (current < 0) current = 0;
            current = (current + direction + boneMappingEnumValues.Length) % boneMappingEnumValues.Length;
            SetBoneMappingTarget(index, boneMappingEnumValues[current]);
        }

        private void SetBoneMappingTarget(int index, HumanBodyBones target)
        {
            if (index < 0 || index >= boneMappings.Count)
                return;

            var mapping = boneMappings[index];
            mapping.target = target;
            boneMappings[index] = mapping;
            ApplyBoneMappingsToMapper();
        }

        private void ConfigureSkeletonVisualizer()
        {
            if (skeletonVisualizer == null)
            {
                var go = new GameObject("Detected Holistic Skeleton");
                go.transform.SetParent(transform, false);
                skeletonVisualizer = go.AddComponent<HolisticSkeletonVisualizer>();
            }

            Transform hips = animator != null ? animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            Transform anchor = hips != null ? hips : vrmModel.transform;
            skeletonVisualizer.Configure(anchor, Vector3.zero, mirrorX, invertY, invertZ, GetSkeletonDisplayScale());
        }

        private void UpdateSkeletonVisualizer(HolisticFrame frame)
        {
            if (skeletonVisualizer == null) return;

            skeletonVisualizer.gameObject.SetActive(showDetectedSkeleton);
            if (!showDetectedSkeleton) return;

            skeletonVisualizer.showPose = showDetectedPose;
            skeletonVisualizer.showHands = showDetectedHands;
            skeletonVisualizer.jointRadius = skeletonJointRadius;
            skeletonVisualizer.lineWidth = skeletonLineWidth;
            skeletonVisualizer.alpha = skeletonAlpha;
            skeletonVisualizer.Configure(
                animator != null ? animator.GetBoneTransform(HumanBodyBones.Hips) : vrmModel.transform,
                Vector3.zero,
                mirrorX, invertY, invertZ, GetSkeletonDisplayScale());
            skeletonVisualizer.UpdateFrame(frame);
        }

        private void UpdateTrackingStats(HolisticFrame frame)
        {
            float nowRealtime = Time.realtimeSinceStartup;
            long nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            trackingStats.poseConfidence = SmoothStat(trackingStats.poseConfidence, frame.PoseConfidence, 0.25f);
            trackingStats.faceConfidence = SmoothStat(trackingStats.faceConfidence, frame.FaceConfidence, 0.25f);
            trackingStats.handConfidence = SmoothStat(trackingStats.handConfidence, frame.HandConfidence, 0.25f);

            if (trackingStats.lastFrameRealtime > 0f)
            {
                float delta = nowRealtime - trackingStats.lastFrameRealtime;
                if (delta > 0f)
                {
                    float fps = 1f / delta;
                    trackingStats.actualFps = trackingStats.actualFps > 0f
                        ? Mathf.Lerp(trackingStats.actualFps, fps, 0.18f)
                        : fps;

                    float expectedInterval = 1f / Mathf.Max(1f, expectedTrackingFps);
                    if (delta > expectedInterval * 1.5f)
                        trackingStats.droppedFrames += Mathf.Max(1, Mathf.RoundToInt(delta / expectedInterval) - 1);
                }
            }

            trackingStats.lastFrameRealtime = nowRealtime;
            trackingStats.lastFrameAgeMs = 0f;
            trackingStats.latencyMs = frame.receivedAtUtcMs > 0
                ? Mathf.Max(0f, nowUtcMs - frame.receivedAtUtcMs)
                : -1f;
        }

        private void RefreshTrackingFrameAge()
        {
            trackingStats.lastFrameAgeMs = trackingStats.lastFrameRealtime > 0f
                ? (Time.realtimeSinceStartup - trackingStats.lastFrameRealtime) * 1000f
                : -1f;
        }

        private float SmoothStat(float current, float target, float t)
        {
            return current <= 0f ? target : Mathf.Lerp(current, target, t);
        }

        private void DrawConfidenceRow(string label, float value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, monitorLabelStyle, GUILayout.Width(72f));
            GUILayout.Label((value * 100f).ToString("0") + "%", StyleForConfidence(value), GUILayout.Width(54f));
            Rect bar = GUILayoutUtility.GetRect(110f, 10f, GUILayout.ExpandWidth(true));
            DrawBar(bar, value, ColorForConfidence(value));
            GUILayout.EndHorizontal();
        }

        private void DrawMetricRow(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, monitorLabelStyle, GUILayout.Width(96f));
            GUILayout.Label(value, monitorValueStyle);
            GUILayout.EndHorizontal();
        }

        private void DrawBar(Rect rect, float value, Color color)
        {
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.12f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = color;
            Rect fill = rect;
            fill.width *= Mathf.Clamp01(value);
            GUI.DrawTexture(fill, Texture2D.whiteTexture);
            GUI.color = old;
        }

        private GUIStyle StyleForConfidence(float value)
        {
            if (value < trackingConfidencePoor) return monitorPoorStyle;
            if (value < trackingConfidenceWarning) return monitorWarnStyle;
            return monitorGoodStyle;
        }

        private Color ColorForConfidence(float value)
        {
            if (value < trackingConfidencePoor) return new Color(1f, 0.30f, 0.30f, 1f);
            if (value < trackingConfidenceWarning) return new Color(1f, 0.72f, 0.22f, 1f);
            return new Color(0.30f, 0.88f, 0.52f, 1f);
        }

        private void EnsureMonitorStyles()
        {
            if (monitorStylesReady)
                return;

            monitorBgTexture = RoundedRect(24, 9, new Color(0.09f, 0.10f, 0.13f, 0.96f), UiBorder);
            monitorPanelStyle = new GUIStyle(GUI.skin.box)
            {
                border = new RectOffset(9, 9, 9, 9),
                normal = { background = monitorBgTexture },
                padding = new RectOffset(12, 12, 12, 12),
            };
            Texture2D rowBg = RoundedRect(24, 8, UiRow);
            Texture2D rowHoverBg = RoundedRect(24, 8, UiRowHover);
            Texture2D rowActiveBg = RoundedRect(24, 8, UiAccentDim, UiAccent);
            Texture2D chipBg = RoundedRect(20, 9, new Color(1f, 1f, 1f, 0.08f));
            Texture2D primaryBg = RoundedRect(24, 9, UiAccent);
            Texture2D primaryHoverBg = RoundedRect(24, 9, UiAccentHover);
            Texture2D secondaryBg = RoundedRect(24, 9, new Color(1f, 1f, 1f, 0.07f));
            Texture2D secondaryHoverBg = RoundedRect(24, 9, new Color(1f, 1f, 1f, 0.13f));
            monitorHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                normal = { textColor = UiText },
            };
            monitorLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                normal = { textColor = UiTextDim },
            };
            monitorValueStyle = new GUIStyle(monitorLabelStyle)
            {
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = UiText },
            };
            monitorGoodStyle = new GUIStyle(monitorValueStyle) { normal = { textColor = new Color(0.44f, 0.95f, 0.60f, 1f) } };
            monitorWarnStyle = new GUIStyle(monitorValueStyle) { normal = { textColor = new Color(1f, 0.76f, 0.26f, 1f) } };
            monitorPoorStyle = new GUIStyle(monitorValueStyle) { normal = { textColor = new Color(1f, 0.40f, 0.40f, 1f) } };
            monitorButtonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 11,
                normal = { textColor = UiText },
            };
            monitorButtonStyle.normal.background = secondaryBg;
            monitorButtonStyle.hover.background = secondaryHoverBg;
            monitorButtonStyle.active.background = secondaryBg;
            monitorMiniButtonStyle = new GUIStyle(monitorButtonStyle)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
            };
            monitorPrimaryButtonStyle = new GUIStyle(monitorButtonStyle)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
            };
            monitorPrimaryButtonStyle.normal.background = primaryBg;
            monitorPrimaryButtonStyle.hover.background = primaryHoverBg;
            monitorPrimaryButtonStyle.active.background = primaryBg;
            monitorPrimaryButtonStyle.normal.textColor = new Color(0.04f, 0.08f, 0.15f, 1f);
            monitorPrimaryButtonStyle.hover.textColor = new Color(0.04f, 0.08f, 0.15f, 1f);
            monitorPrimaryButtonStyle.active.textColor = new Color(0.04f, 0.08f, 0.15f, 1f);
            monitorSecondaryButtonStyle = new GUIStyle(monitorButtonStyle)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
            };
            monitorToggleStyle = new GUIStyle(GUI.skin.toggle)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 10,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(4, 4, 2, 2),
                border = new RectOffset(8, 8, 8, 8),
                normal = { textColor = UiTextDim, background = secondaryBg },
                hover = { textColor = Color.white, background = secondaryHoverBg },
                onNormal = { textColor = new Color(0.04f, 0.08f, 0.15f, 1f), background = primaryBg },
                onHover = { textColor = new Color(0.04f, 0.08f, 0.15f, 1f), background = primaryHoverBg },
            };
            monitorSubHeaderStyle = new GUIStyle(monitorHeaderStyle)
            {
                fontSize = 12,
            };
            monitorTinyStyle = new GUIStyle(monitorLabelStyle)
            {
                fontSize = 10,
                normal = { textColor = new Color(0.56f, 0.61f, 0.67f, 1f) },
            };
            monitorRowStyle = new GUIStyle
            {
                border = new RectOffset(8, 8, 8, 8),
                padding = new RectOffset(8, 6, 4, 4),
                margin = new RectOffset(0, 0, 0, 0),
            };
            monitorRowStyle.normal.background = rowBg;
            monitorRowStyle.hover.background = rowHoverBg;
            monitorRowActiveStyle = new GUIStyle(monitorRowStyle);
            monitorRowActiveStyle.normal.background = rowActiveBg;
            monitorRowActiveStyle.hover.background = rowActiveBg;
            monitorChipStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 10,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(8, 8, 2, 2),
                border = new RectOffset(9, 9, 9, 9),
                normal = { textColor = UiText, background = chipBg },
            };
            monitorStylesReady = true;
        }

        private Texture2D RoundedRect(int size, int radius, Color fill)
        {
            return RoundedRect(size, radius, fill, Color.clear);
        }

        private Texture2D RoundedRect(int size, int radius, Color fill, Color border)
        {
            size = Mathf.Max(size, radius * 2 + 1);
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var pixels = new Color[size * size];
            float edgeRadius = radius;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Min(x, size - 1 - x);
                    float dy = Mathf.Min(y, size - 1 - y);
                    float coverage = dx >= edgeRadius || dy >= edgeRadius
                        ? 1f
                        : Mathf.Clamp01(edgeRadius - Mathf.Sqrt(
                            (edgeRadius - dx) * (edgeRadius - dx) +
                            (edgeRadius - dy) * (edgeRadius - dy)) + 0.5f);
                    Color pixel = fill;
                    if (border.a > 0f && Mathf.Min(dx, dy) < 1.5f)
                        pixel = border;
                    pixel.a *= coverage;
                    pixels[y * size + x] = pixel;
                }
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private Texture2D MakeTexture(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }

        private struct TrackingStats
        {
            public float poseConfidence;
            public float faceConfidence;
            public float handConfidence;
            public float actualFps;
            public int droppedFrames;
            public float latencyMs;
            public float lastFrameAgeMs;
            public float lastFrameRealtime;
        }

        private float GetSkeletonDisplayScale()
        {
            float baseScale = landmarkScale;
            if (autoScaleSkeletonToModel)
                baseScale *= skeletonModelScale;
            return baseScale;
        }

        private void UpdateSkeletonModelScale()
        {
            skeletonModelScale = autoScaleSkeletonToModel ? EstimateModelScaleMultiplier() : 1f;
        }

        private float EstimateModelScaleMultiplier()
        {
            if (animator == null)
                return 1f;

            Transform head = GetBone(HumanBodyBones.Head);
            Transform hips = GetBone(HumanBodyBones.Hips);
            Transform leftFoot = GetFootBone(true);
            Transform rightFoot = GetFootBone(false);

            if (head == null || hips == null)
                return 1f;

            float measuredHeight = 0f;
            if (leftFoot != null && rightFoot != null)
            {
                Vector3 footMid = (leftFoot.position + rightFoot.position) * 0.5f;
                measuredHeight = Vector3.Distance(head.position, footMid);
            }
            else if (leftFoot != null)
            {
                measuredHeight = Vector3.Distance(head.position, leftFoot.position);
            }
            else if (rightFoot != null)
            {
                measuredHeight = Vector3.Distance(head.position, rightFoot.position);
            }
            else
            {
                measuredHeight = Vector3.Distance(head.position, hips.position) * 2f;
            }

            if (measuredHeight <= Mathf.Epsilon)
                return 1f;

            return measuredHeight / Mathf.Max(0.01f, skeletonHumanHeightReference);
        }

        private Transform GetBone(HumanBodyBones bone)
        {
            return animator != null ? animator.GetBoneTransform(bone) : null;
        }

        private Transform GetFootBone(bool isLeft)
        {
            if (animator == null)
                return null;

            Transform bone = animator.GetBoneTransform(isLeft ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot);
            if (bone != null) return bone;

            bone = animator.GetBoneTransform(isLeft ? HumanBodyBones.LeftToes : HumanBodyBones.RightToes);
            if (bone != null) return bone;

            return animator.GetBoneTransform(isLeft ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg);
        }

        private void OnDestroy()
        {
            UnwireMapperCallbacks();
            receiver?.Dispose();
            if (monitorBgTexture != null) Destroy(monitorBgTexture);
        }

        private void UnwireMapperCallbacks()
        {
            if (mapper != null)
            {
                UnwireBlinkCallbacks();
                UnwireExpressionCallbacks();
            }

            expressionsWired = false;
            wiredExpressionCount = 0;
            expressionStatus = "Not wired";
        }

        private void OnApplicationQuit()
        {
            receiver?.Dispose();
        }

        // ---- VRM 1.0 expression bridge (UniVRM10) ----

        private void EnsureVRMExpressionsWired()
        {
            if (!enableFaceBlendShapes || expressionsWired || mapper == null)
                return;

            float now = Time.realtimeSinceStartup;
            if (now < nextExpressionWireAttemptRealtime)
                return;

            nextExpressionWireAttemptRealtime = now + 1f;
            TryWireVRMExpressions();
        }

        private bool TryWireVRMExpressions()
        {
            if (vrmModel == null || mapper == null)
            {
                expressionStatus = "No model";
                return false;
            }

            vrm10Instance = vrmModel.GetComponent<Vrm10Instance>();
            if (vrm10Instance == null)
                vrm10Instance = vrmModel.GetComponentInChildren<Vrm10Instance>();

            if (vrm10Instance == null)
            {
                expressionStatus = "No Vrm10Instance";
                return false;
            }

            Vrm10Runtime runtime;
            try
            {
                runtime = vrm10Instance.Runtime;
            }
            catch (Exception exception)
            {
                expressionStatus = "Runtime pending";
                Debug.LogWarning("[VRMHolisticController] VRM runtime is not ready yet: " + exception.Message);
                return false;
            }

            if (runtime == null || runtime.Expression == null || runtime.Expression.ExpressionKeys == null)
            {
                expressionStatus = "Runtime pending";
                return false;
            }

            if (runtime.Expression.ExpressionKeys.Count == 0)
            {
                expressionStatus = "No expression clips";
                return false;
            }

            // Prefer independent per-eye blink expressions so a single-eye wink
            // works. Fall back to the combined Blink channel only when the model
            // does not ship BlinkLeft / BlinkRight expressions.
            hasPerEyeBlink = HasExpression(ExpressionKey.BlinkLeft)
                && HasExpression(ExpressionKey.BlinkRight);
            hasCombinedBlink = HasExpression(ExpressionKey.Blink);

            if (hasPerEyeBlink)
            {
                mapper.OnBlinkLeftUpdated += SetBlinkLeft;
                mapper.OnBlinkRightUpdated += SetBlinkRight;
                wiredExpressionCount += 2;
            }
            else if (hasCombinedBlink)
            {
                mapper.OnBlinkUpdated += SetBlink;
                wiredExpressionCount++;
            }

            wiredExpressionCount += WireExpressionIfPresent(ExpressionKey.Aa, ref mapper.OnMouthOpenUpdated, SetMouthOpen);
            wiredExpressionCount += WireExpressionIfPresent(ExpressionKey.Ih, ref mapper.OnMouthIhUpdated, SetMouthIh);
            wiredExpressionCount += WireExpressionIfPresent(ExpressionKey.Ee, ref mapper.OnMouthEeUpdated, SetMouthEe);
            wiredExpressionCount += WireExpressionIfPresent(ExpressionKey.Ou, ref mapper.OnMouthOuUpdated, SetMouthOu);
            wiredExpressionCount += WireExpressionIfPresent(ExpressionKey.Oh, ref mapper.OnMouthOhUpdated, SetMouthOh);
            wiredExpressionCount += WireExpressionIfPresent(ExpressionKey.Happy, ref mapper.OnHappyUpdated, SetHappy);
            wiredExpressionCount += WireExpressionIfPresent(ExpressionKey.Angry, ref mapper.OnAngryUpdated, SetAngry);
            wiredExpressionCount += WireExpressionIfPresent(ExpressionKey.Surprised, ref mapper.OnSurprisedUpdated, SetSurprised);
            wiredExpressionCount += WireExpressionIfPresent(ExpressionKey.Sad, ref mapper.OnSadUpdated, SetSad);
            wiredExpressionCount += WireExpressionIfPresent(ExpressionKey.Relaxed, ref mapper.OnRelaxedUpdated, SetRelaxed);

            if (wiredExpressionCount == 0)
            {
                expressionStatus = "No supported clips";
                return false;
            }

            expressionsWired = true;
            expressionStatus = "Wired " + wiredExpressionCount;

            Debug.Log(hasPerEyeBlink
                ? "[VRMHolisticController] VRM 1.0 expressions wired (per-eye blink + expanded face)."
                : "[VRMHolisticController] VRM 1.0 expressions wired (single blink + expanded face; model has no BlinkLeft/BlinkRight).");
            return true;
        }

        private bool HasExpression(ExpressionKey key)
        {
            if (vrm10Instance == null) return false;
            var keys = vrm10Instance.Runtime.Expression.ExpressionKeys;
            if (keys == null) return false;
            for (int i = 0; i < keys.Count; i++)
            {
                if (keys[i].Equals(key)) return true;
            }
            return false;
        }

        private int WireExpressionIfPresent(ExpressionKey key, ref System.Action<float> callbackField, System.Action<float> handler)
        {
            if (HasExpression(key))
            {
                callbackField += handler;
                return 1;
            }

            return 0;
        }

        private void UnwireBlinkCallbacks()
        {
            if (mapper == null) return;
            mapper.OnBlinkUpdated -= SetBlink;
            mapper.OnBlinkLeftUpdated -= SetBlinkLeft;
            mapper.OnBlinkRightUpdated -= SetBlinkRight;
        }

        private void UnwireExpressionCallbacks()
        {
            if (mapper == null) return;
            mapper.OnMouthOpenUpdated -= SetMouthOpen;
            mapper.OnMouthIhUpdated -= SetMouthIh;
            mapper.OnMouthEeUpdated -= SetMouthEe;
            mapper.OnMouthOuUpdated -= SetMouthOu;
            mapper.OnMouthOhUpdated -= SetMouthOh;
            mapper.OnHappyUpdated -= SetHappy;
            mapper.OnAngryUpdated -= SetAngry;
            mapper.OnSurprisedUpdated -= SetSurprised;
            mapper.OnSadUpdated -= SetSad;
            mapper.OnRelaxedUpdated -= SetRelaxed;
        }

        private void SetBlink(float value)
        {
            if (vrm10Instance == null) return;
            vrm10Instance.Runtime.Expression.SetWeight(ExpressionKey.Blink, NormalizeBlink(value));
        }

        private void SetBlinkLeft(float value)
        {
            if (vrm10Instance == null) return;
            currentBlinkLeft = NormalizeBlink(value);
            ApplyPerEyeBlink();
        }

        private void SetBlinkRight(float value)
        {
            if (vrm10Instance == null) return;
            currentBlinkRight = NormalizeBlink(value);
            ApplyPerEyeBlink();
        }

        private float NormalizeBlink(float value)
        {
            float amplified = Mathf.Clamp01(value * blinkStrength);
            return amplified >= blinkFullThreshold ? 1f : amplified;
        }

        private void ApplyPerEyeBlink()
        {
            vrm10Instance.Runtime.Expression.SetWeight(ExpressionKey.BlinkLeft, currentBlinkLeft);
            vrm10Instance.Runtime.Expression.SetWeight(ExpressionKey.BlinkRight, currentBlinkRight);

            if (hasCombinedBlink)
            {
                // Add the model's both-eye blink only when both tracked eyes close.
                vrm10Instance.Runtime.Expression.SetWeight(ExpressionKey.Blink, Mathf.Min(currentBlinkLeft, currentBlinkRight) * combinedBlinkLayer);
            }
        }

        private void SetMouthOpen(float value)
        {
            SetExpression(ExpressionKey.Aa, value);
        }

        private void SetMouthIh(float value)
        {
            SetExpression(ExpressionKey.Ih, value);
        }

        private void SetMouthEe(float value)
        {
            SetExpression(ExpressionKey.Ee, value);
        }

        private void SetMouthOu(float value)
        {
            SetExpression(ExpressionKey.Ou, value);
        }

        private void SetMouthOh(float value)
        {
            SetExpression(ExpressionKey.Oh, value);
        }

        private void SetHappy(float value)
        {
            SetExpression(ExpressionKey.Happy, value);
        }

        private void SetAngry(float value)
        {
            SetExpression(ExpressionKey.Angry, value);
        }

        private void SetSurprised(float value)
        {
            SetExpression(ExpressionKey.Surprised, value);
        }

        private void SetSad(float value)
        {
            SetExpression(ExpressionKey.Sad, value);
        }

        private void SetRelaxed(float value)
        {
            SetExpression(ExpressionKey.Relaxed, value);
        }

        private void SetExpression(ExpressionKey key, float value)
        {
            if (vrm10Instance == null) return;
            vrm10Instance.Runtime.Expression.SetWeight(key, Mathf.Clamp01(value));
        }
    }
}
