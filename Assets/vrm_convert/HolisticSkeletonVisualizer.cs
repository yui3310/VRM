using System.Collections.Generic;
using UnityEngine;

namespace CCMediaPipe
{
    /// <summary>
    /// Draws the latest MediaPipe pose and hand landmarks over the avatar.
    /// The generated renderers use an x-ray material so the tracking result
    /// remains visible through the VRM mesh.
    /// </summary>
    public class HolisticSkeletonVisualizer : MonoBehaviour
    {
        [Header("Display")]
        public bool showPose = true;
        public bool showHands = true;
        [Range(0.001f, 0.05f)]
        public float jointRadius = 0.012f;
        [Range(0.001f, 0.03f)]
        public float lineWidth = 0.006f;
        [Range(0f, 1f)]
        public float alpha = 0.9f;
        public Color poseColor = new Color(0.1f, 0.9f, 1f, 1f);
        public Color leftHandColor = new Color(0.3f, 1f, 0.35f, 1f);
        public Color rightHandColor = new Color(1f, 0.35f, 0.25f, 1f);

        [Header("Model Depth Fit")]
        [Tooltip("Correct MediaPipe depth by aligning landmarks to the VRM humanoid bones along the camera depth axis.")]
        public bool fitDepthToModel = true;
        [Range(0f, 1f)]
        [Tooltip("0 keeps the raw AI depth, 1 fully matches the VRM bone depth.")]
        public float depthFitStrength = 1f;

        private readonly List<LineRenderer> lines = new List<LineRenderer>();
        private readonly List<Transform> joints = new List<Transform>();
        private readonly Vector3[] posePoints = new Vector3[HolisticFrame.PoseLandmarkCount];
        private readonly Vector3[] handPoints = new Vector3[HolisticFrame.HandLandmarkCount];
        private readonly float[] depthDeltas = new float[HolisticFrame.PoseLandmarkCount];
        private readonly Transform[] poseBones = new Transform[HolisticFrame.PoseLandmarkCount];
        private Animator modelAnimator;
        private Transform headBone;
        private Transform leftHandBone;
        private Transform rightHandBone;
        private Transform leftMiddleProximalBone;
        private Transform rightMiddleProximalBone;
        private Material xrayMaterial;
        private Transform anchor;
        private Vector3 anchorOffset;
        private bool initialized;
        private bool shaderWarningLogged;

        private static readonly int[,] PoseConnections =
        {
            { 11, 12 }, { 11, 13 }, { 13, 15 }, { 12, 14 }, { 14, 16 },
            { 11, 23 }, { 12, 24 }, { 23, 24 }, { 23, 25 }, { 25, 27 },
            { 24, 26 }, { 26, 28 }, { 27, 29 }, { 28, 30 }, { 29, 31 },
            { 30, 32 }, { 0, 11 }, { 0, 12 }
        };

        private static readonly int[,] HandConnections =
        {
            { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 4 },
            { 0, 5 }, { 5, 6 }, { 6, 7 }, { 7, 8 },
            { 0, 9 }, { 9, 10 }, { 10, 11 }, { 11, 12 },
            { 0, 13 }, { 13, 14 }, { 14, 15 }, { 15, 16 },
            { 0, 17 }, { 17, 18 }, { 18, 19 }, { 19, 20 },
            { 5, 9 }, { 9, 13 }, { 13, 17 }
        };

        private static readonly int[] DepthSource =
        {
            -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,
            PoseLandmark.LeftShoulder, PoseLandmark.RightShoulder,
            PoseLandmark.LeftElbow, PoseLandmark.RightElbow,
            PoseLandmark.LeftWrist, PoseLandmark.RightWrist,
            PoseLandmark.LeftWrist, PoseLandmark.RightWrist,
            PoseLandmark.LeftWrist, PoseLandmark.RightWrist,
            PoseLandmark.LeftWrist, PoseLandmark.RightWrist,
            PoseLandmark.LeftHip, PoseLandmark.RightHip,
            PoseLandmark.LeftKnee, PoseLandmark.RightKnee,
            PoseLandmark.LeftAnkle, PoseLandmark.RightAnkle,
            PoseLandmark.LeftAnkle, PoseLandmark.RightAnkle,
            PoseLandmark.LeftAnkle, PoseLandmark.RightAnkle
        };

        public void SetModelAnimator(Animator animator)
        {
            if (animator == modelAnimator && (animator == null || headBone != null))
                return;

            modelAnimator = animator;
            for (int i = 0; i < poseBones.Length; i++) poseBones[i] = null;
            headBone = Bone(HumanBodyBones.Head);
            leftHandBone = Bone(HumanBodyBones.LeftHand);
            rightHandBone = Bone(HumanBodyBones.RightHand);
            leftMiddleProximalBone = Bone(HumanBodyBones.LeftMiddleProximal);
            rightMiddleProximalBone = Bone(HumanBodyBones.RightMiddleProximal);

            poseBones[PoseLandmark.LeftShoulder] = Bone(HumanBodyBones.LeftUpperArm);
            poseBones[PoseLandmark.RightShoulder] = Bone(HumanBodyBones.RightUpperArm);
            poseBones[PoseLandmark.LeftElbow] = Bone(HumanBodyBones.LeftLowerArm);
            poseBones[PoseLandmark.RightElbow] = Bone(HumanBodyBones.RightLowerArm);
            poseBones[PoseLandmark.LeftWrist] = leftHandBone;
            poseBones[PoseLandmark.RightWrist] = rightHandBone;
            poseBones[PoseLandmark.LeftHip] = Bone(HumanBodyBones.LeftUpperLeg);
            poseBones[PoseLandmark.RightHip] = Bone(HumanBodyBones.RightUpperLeg);
            poseBones[PoseLandmark.LeftKnee] = Bone(HumanBodyBones.LeftLowerLeg);
            poseBones[PoseLandmark.RightKnee] = Bone(HumanBodyBones.RightLowerLeg);
            poseBones[PoseLandmark.LeftAnkle] = Bone(HumanBodyBones.LeftFoot);
            poseBones[PoseLandmark.RightAnkle] = Bone(HumanBodyBones.RightFoot);
            poseBones[PoseLandmark.LeftFootIndex] = Bone(HumanBodyBones.LeftToes);
            poseBones[PoseLandmark.RightFootIndex] = Bone(HumanBodyBones.RightToes);
        }

        private Transform Bone(HumanBodyBones bone)
        {
            return modelAnimator != null && modelAnimator.isHuman
                ? modelAnimator.GetBoneTransform(bone) : null;
        }

        public void Configure(Transform modelAnchor, Vector3 offset,
            bool mirrorX, bool invertY, bool invertZ, float scale)
        {
            anchor = modelAnchor;
            anchorOffset = offset;
            this.mirrorX = mirrorX;
            this.invertY = invertY;
            this.invertZ = invertZ;
            this.scale = scale;
            EnsureInitialized();
        }

        private bool mirrorX;
        private bool invertY;
        private bool invertZ;
        private float scale = 1f;

        public void UpdateFrame(HolisticFrame frame)
        {
            if (frame == null || anchor == null) return;
            EnsureInitialized();
            if (xrayMaterial == null)
            {
                // Resources and shader lookup can finish after the first
                // frame in a player. Keep trying instead of permanently
                // disabling the visualizer after one failed lookup.
                initialized = false;
                if (!shaderWarningLogged)
                {
                    Debug.LogWarning("[HolisticSkeletonVisualizer] Waiting for a runtime shader.");
                    shaderWarningLogged = true;
                }
                return;
            }

            var pose = SelectRenderablePose(frame);
            var left = SelectRenderableHand(frame.BestLeftHand);
            var right = SelectRenderableHand(frame.BestRightHand);
            Vector3 hipOrigin = Vector3.zero;
            bool hasPose = pose != null && pose.Length == HolisticFrame.PoseLandmarkCount;
            if (hasPose)
            {
                hipOrigin = Mid(pose[PoseLandmark.LeftHip], pose[PoseLandmark.RightHip]);
                BuildPosePoints(pose, hipOrigin);
            }

            int lineIndex = 0;
            int jointIndex = 0;
            if (showPose && hasPose)
            {
                for (int i = 0; i < PoseConnections.GetLength(0); i++)
                    SetLine(lineIndex++, posePoints[PoseConnections[i, 0]],
                        posePoints[PoseConnections[i, 1]], poseColor);
                for (int i = 0; i < pose.Length; i++)
                    SetJoint(jointIndex++, posePoints[i], poseColor);
            }

            if (showHands)
            {
                DrawHand(left, true, hasPose, hipOrigin, leftHandColor, ref lineIndex, ref jointIndex);
                DrawHand(right, false, hasPose, hipOrigin, rightHandColor, ref lineIndex, ref jointIndex);
            }

            for (; lineIndex < lines.Count; lineIndex++) lines[lineIndex].enabled = false;
            for (; jointIndex < joints.Count; jointIndex++) joints[jointIndex].gameObject.SetActive(false);
        }

        private static Landmark[] SelectRenderablePose(HolisticFrame frame)
        {
            // World landmarks are the correct 3D source. Some player builds
            // can receive a normalized-only frame, so retain that fallback
            // rather than silently producing no model skeleton.
            if (frame.HasPoseWorld) return frame.poseWorldLandmarks;
            return frame.HasPoseNorm ? frame.poseLandmarks : null;
        }

        private static Landmark[] SelectRenderableHand(Landmark[] hand)
        {
            return hand != null && hand.Length == HolisticFrame.HandLandmarkCount
                ? hand : null;
        }

        private void BuildPosePoints(Landmark[] pose, Vector3 hipOrigin)
        {
            for (int i = 0; i < pose.Length; i++)
                posePoints[i] = Point(pose[i], hipOrigin);

            if (!fitDepthToModel || depthFitStrength <= 0f || modelAnimator == null)
                return;

            for (int i = 0; i < depthDeltas.Length; i++)
            {
                Transform bone = poseBones[i];
                depthDeltas[i] = bone != null ? bone.position.z - posePoints[i].z : float.NaN;
            }

            float headDelta = float.NaN;
            if (headBone != null)
                headDelta = headBone.position.z -
                    (posePoints[PoseLandmark.LeftEar].z + posePoints[PoseLandmark.RightEar].z) * 0.5f;

            for (int i = 0; i < posePoints.Length; i++)
            {
                float delta = depthDeltas[i];
                if (float.IsNaN(delta))
                {
                    int source = DepthSource[i];
                    delta = source < 0 ? headDelta : depthDeltas[source];
                }

                if (!float.IsNaN(delta))
                    posePoints[i].z += delta * depthFitStrength;
            }
        }

        private void DrawHand(Landmark[] hand, bool isLeft, bool hasPose, Vector3 hipOrigin,
            Color color, ref int lineIndex, ref int jointIndex)
        {
            if (hand == null || hand.Length != HolisticFrame.HandLandmarkCount) return;
            BuildHandPoints(hand, isLeft, hasPose, hipOrigin);
            for (int i = 0; i < HandConnections.GetLength(0); i++)
                SetLine(lineIndex++, handPoints[HandConnections[i, 0]],
                    handPoints[HandConnections[i, 1]], color);
            for (int i = 0; i < hand.Length; i++)
                SetJoint(jointIndex++, handPoints[i], color);
        }

        private void BuildHandPoints(Landmark[] hand, bool isLeft, bool hasPose, Vector3 hipOrigin)
        {
            Transform handBone = isLeft ? leftHandBone : rightHandBone;
            if (!hasPose && handBone == null)
            {
                for (int i = 0; i < hand.Length; i++)
                    handPoints[i] = Point(hand[i], hipOrigin);
                return;
            }

            Vector3 wrist = hasPose
                ? posePoints[isLeft ? PoseLandmark.LeftWrist : PoseLandmark.RightWrist]
                : handBone.position;
            Vector3 handOrigin = hand[HandLandmark.Wrist].AsVector3();
            float handScale = EstimateHandScale(hand, isLeft);
            for (int i = 0; i < hand.Length; i++)
                handPoints[i] = wrist + ConvertAxes(hand[i].AsVector3() - handOrigin) * handScale;
        }

        private float EstimateHandScale(Landmark[] hand, bool isLeft)
        {
            Transform handBone = isLeft ? leftHandBone : rightHandBone;
            Transform middleBone = isLeft ? leftMiddleProximalBone : rightMiddleProximalBone;
            float landmarkLength = Vector3.Distance(hand[HandLandmark.Wrist].AsVector3(),
                hand[HandLandmark.MiddleMCP].AsVector3());
            if (handBone == null || middleBone == null || landmarkLength <= 1e-5f)
                return scale;

            float modelLength = Vector3.Distance(handBone.position, middleBone.position);
            return modelLength > 1e-5f ? modelLength / landmarkLength : scale;
        }

        private Vector3 Point(Landmark landmark, Vector3 hipOrigin)
        {
            return anchor.position + anchorOffset + ConvertAxes(landmark.AsVector3() - hipOrigin) * scale;
        }

        private Vector3 ConvertAxes(Vector3 p)
        {
            return new Vector3(mirrorX ? -p.x : p.x, invertY ? -p.y : p.y,
                invertZ ? -p.z : p.z);
        }

        private static Vector3 Mid(Landmark a, Landmark b)
        {
            return (a.AsVector3() + b.AsVector3()) * 0.5f;
        }

        private void EnsureInitialized()
        {
            if (initialized) return;
            Shader shader = Resources.Load<Shader>("HolisticSkeletonXRay");
            if (shader == null) shader = Shader.Find("CCMediaPipe/HolisticSkeletonXRay");
            // Shader.Find can return null in a player when an otherwise
            // unreferenced custom shader was stripped from the build. Keep a
            // built-in fallback so the runtime skeleton still renders.
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("UI/Default");
            if (shader == null)
            {
                Debug.LogError("[HolisticSkeletonVisualizer] No runtime shader available for the 3D skeleton.");
                initialized = true;
                return;
            }
            xrayMaterial = new Material(shader);
            if (xrayMaterial.HasProperty("_Color"))
                xrayMaterial.SetColor("_Color", Color.white);
            initialized = true;
        }

        private void SetLine(int index, Vector3 a, Vector3 b, Color color)
        {
            while (lines.Count <= index)
            {
                var go = new GameObject("Skeleton Line");
                go.transform.SetParent(transform, false);
                var line = go.AddComponent<LineRenderer>();
                line.positionCount = 2;
                line.useWorldSpace = true;
                line.alignment = LineAlignment.View;
                line.textureMode = LineTextureMode.Stretch;
                if (xrayMaterial != null)
                    line.material = xrayMaterial;
                lines.Add(line);
            }
            var renderer = lines[index];
            renderer.enabled = true;
            renderer.startWidth = lineWidth;
            renderer.endWidth = lineWidth;
            renderer.startColor = WithAlpha(color);
            renderer.endColor = WithAlpha(color);
            renderer.SetPosition(0, a);
            renderer.SetPosition(1, b);
        }

        private void SetJoint(int index, Vector3 position, Color color)
        {
            while (joints.Count <= index)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "Skeleton Joint";
                go.transform.SetParent(transform, false);
                var collider = go.GetComponent<Collider>();
                if (collider != null) Destroy(collider);
                if (xrayMaterial != null)
                    go.GetComponent<Renderer>().material = xrayMaterial;
                joints.Add(go.transform);
            }
            var joint = joints[index];
            joint.gameObject.SetActive(true);
            joint.position = position;
            joint.localScale = Vector3.one * (jointRadius * 2f);
            var renderer = joint.GetComponent<Renderer>();
            if (xrayMaterial == null) return;
            renderer.material = xrayMaterial;
            renderer.material.color = WithAlpha(color);
        }

        private Color WithAlpha(Color color)
        {
            color.a = alpha;
            return color;
        }

        private void OnDestroy()
        {
            if (xrayMaterial != null) Destroy(xrayMaterial);
        }
    }
}
