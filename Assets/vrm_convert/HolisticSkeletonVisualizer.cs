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

        private readonly List<LineRenderer> lines = new List<LineRenderer>();
        private readonly List<Transform> joints = new List<Transform>();
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
            if (pose != null && pose.Length == HolisticFrame.PoseLandmarkCount)
                hipOrigin = Mid(pose[PoseLandmark.LeftHip], pose[PoseLandmark.RightHip]);

            int lineIndex = 0;
            int jointIndex = 0;
            if (showPose && pose != null && pose.Length == HolisticFrame.PoseLandmarkCount)
            {
                for (int i = 0; i < PoseConnections.GetLength(0); i++)
                    SetLine(lineIndex++, Point(pose[PoseConnections[i, 0]], hipOrigin),
                        Point(pose[PoseConnections[i, 1]], hipOrigin), poseColor);
                for (int i = 0; i < pose.Length; i++)
                    SetJoint(jointIndex++, Point(pose[i], hipOrigin), poseColor);
            }

            if (showHands)
            {
                DrawHand(left, hipOrigin, leftHandColor, ref lineIndex, ref jointIndex);
                DrawHand(right, hipOrigin, rightHandColor, ref lineIndex, ref jointIndex);
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

        private void DrawHand(Landmark[] hand, Vector3 hipOrigin, Color color,
            ref int lineIndex, ref int jointIndex)
        {
            if (hand == null || hand.Length != HolisticFrame.HandLandmarkCount) return;
            for (int i = 0; i < HandConnections.GetLength(0); i++)
                SetLine(lineIndex++, Point(hand[HandConnections[i, 0]], hipOrigin),
                    Point(hand[HandConnections[i, 1]], hipOrigin), color);
            for (int i = 0; i < hand.Length; i++)
                SetJoint(jointIndex++, Point(hand[i], hipOrigin), color);
        }

        private Vector3 Point(Landmark landmark, Vector3 hipOrigin)
        {
            Vector3 p = landmark.AsVector3() - hipOrigin;
            p = new Vector3(mirrorX ? -p.x : p.x, invertY ? -p.y : p.y,
                invertZ ? -p.z : p.z) * scale;
            return anchor.position + anchorOffset + p;
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
