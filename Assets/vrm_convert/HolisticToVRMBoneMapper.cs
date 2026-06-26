using System;
using System.Collections.Generic;
using UnityEngine;

namespace CCMediaPipe
{
    /// <summary>
    /// Maps MediaPipe Holistic landmarks to Unity Humanoid bone rotations
    /// for VRM models. Uses landmark-pair directions to compute bone rotations,
    /// with configurable smoothing and coordinate-space conversion.
    ///
    /// Usage: call ApplyBodyPose() then ApplyHands() every frame.
    /// </summary>
    public class HolisticToVRMBoneMapper
    {
        // ---- Tunable parameters ----
        public float bodySmoothing = 0.3f;          // EMA factor (lower = smoother)
        public float handSmoothing = 0.2f;
        public bool mirrorX = true;                  // flip MediaPipe X 锟?Unity X
        public bool invertZ = true;                  // flip MediaPipe Z 锟?Unity Z
        public float scale = 1.0f;                   // world landmark scale multiplier
        public float minVisibility = 0.5f;           // ignore landmarks below this
        public bool useWorldLandmarks = true;        // prefer world over normalized
        public bool invertY = true;                  // flip MediaPipe Y (down) to Unity Y (up)

        private Animator animator;
        private Dictionary<HumanBodyBones, Quaternion> smoothedRotations;

        // Rest-pose cache for direction-based aiming (captured lazily on first use,
        // while the model is still in its imported rest/T-pose).
        private Dictionary<HumanBodyBones, Vector3> restChildDirWorld = new Dictionary<HumanBodyBones, Vector3>();
        private Dictionary<HumanBodyBones, Quaternion> restWorldRotation = new Dictionary<HumanBodyBones, Quaternion>();

        // Rest-pose palm frame for each hand, captured while the model is still
        // in its imported pose. We store the rotation from the hand bone's local
        // space to the anatomical palm frame (forward = toward middle-finger
        // knuckle, up = palm normal). At runtime the live palm frame is
        // handBone.rotation * handToPalm, which lets finger directions tracked by
        // MediaPipe be re-expressed onto the model no matter how the wrist is
        // oriented in space.
        private struct PalmRest
        {
            public Quaternion handToPalm; // hand-bone local space -> palm frame
            public bool valid;
        }
        private PalmRest _leftPalmRest;
        private PalmRest _rightPalmRest;

        // Maps MediaPipe (coordinate-converted) finger directions into the
        // model's world space for the hand currently being processed. Built each
        // frame in BeginHand from the LIVE, driven palm frame so finger curl
        // follows the wrist: direction_model = livePalm * (inverse(mpPalm) * dir).
        private Quaternion _currentHandMpToModel = Quaternion.identity;
        private bool _currentHandMappingValid;

        // Reference directions in T-pose (model local space at import)
        private static readonly Vector3 RefUp = Vector3.up;
        private static readonly Vector3 RefForward = Vector3.forward;
        private static readonly Vector3 RefRight = Vector3.right;

        // Bone direction references in local space (relative to parent bone)
        // These define what direction the bone points in T-pose.
        private static readonly Dictionary<HumanBodyBones, Vector3> BoneLocalRef = new Dictionary<HumanBodyBones, Vector3>
        {
            { HumanBodyBones.Hips,          Vector3.up },
            { HumanBodyBones.Spine,         Vector3.up },
            { HumanBodyBones.Chest,         Vector3.up },
            { HumanBodyBones.UpperChest,    Vector3.up },
            { HumanBodyBones.Neck,          Vector3.up },
            { HumanBodyBones.Head,          Vector3.up },
            { HumanBodyBones.LeftUpperArm,  Vector3.down },
            { HumanBodyBones.LeftLowerArm,  Vector3.down },
            { HumanBodyBones.RightUpperArm, Vector3.down },
            { HumanBodyBones.RightLowerArm, Vector3.down },
            { HumanBodyBones.LeftUpperLeg,  Vector3.down },
            { HumanBodyBones.LeftLowerLeg,  Vector3.down },
            { HumanBodyBones.RightUpperLeg, Vector3.down },
            { HumanBodyBones.RightLowerLeg, Vector3.down },
        };
    // ---- Optional feature callbacks (no hard dependency on VRM / UniVRM) ----

    /// <summary>
    /// Invoked each frame with a 0..1 blink value computed from face landmarks.
    /// Wire this to your VRM BlendShapeProxy or any other blend-shape system.
    /// </summary>
    public Action<float> OnBlinkUpdated;

    /// <summary>
    /// Invoked each frame with a 0..1 mouth-open value computed from face
    /// landmarks. Wire this to the VRM "Aa" expression.
    /// </summary>
    public Action<float> OnMouthOpenUpdated;

    public HolisticToVRMBoneMapper(Animator animator)
    {
        this.animator = animator;
        smoothedRotations = new Dictionary<HumanBodyBones, Quaternion>();
        CaptureRestPose();
    }

    // Snapshot each limb bone's world rotation and rest direction toward its
    // child while the model is still in its imported T/A-pose. Doing this once
    // up front avoids reading a pose that earlier bones (spine/chest) have
    // already rotated within the same frame.
    private void CaptureRestPose()
    {
        var chain = new (HumanBodyBones bone, HumanBodyBones child)[]
        {
            (HumanBodyBones.LeftUpperArm,  HumanBodyBones.LeftLowerArm),
            (HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftHand),
            (HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm),
            (HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand),
            (HumanBodyBones.LeftUpperLeg,  HumanBodyBones.LeftLowerLeg),
            (HumanBodyBones.LeftLowerLeg,  HumanBodyBones.LeftFoot),
            (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg),
            (HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot),
        };

        foreach (var (bone, child) in chain)
        {
            var t = animator.GetBoneTransform(bone);
            var childT = animator.GetBoneTransform(child);
            if (t == null) continue;
            Vector3 restDir = childT != null
                ? (childT.position - t.position)
                : t.rotation * Vector3.down;
            if (restDir.sqrMagnitude < 1e-8f) continue;
            restChildDirWorld[bone] = restDir.normalized;
            restWorldRotation[bone] = t.rotation;
        }

        CaptureFingerRestPose();
        CapturePalmRest();
    }

    // Capture each hand's rest palm frame relative to its hand bone. The palm
    // frame is defined anatomically (forward = toward the middle-finger knuckle,
    // side = index knuckle -> little knuckle, up = palm normal) so the very same
    // construction can be rebuilt from MediaPipe landmarks at runtime. Storing
    // the rotation from the hand bone to the palm frame lets us derive the
    // model's live palm orientation as handBone.rotation * handToPalm.
    private void CapturePalmRest()
    {
        _leftPalmRest  = BuildModelPalmRest(true);
        _rightPalmRest = BuildModelPalmRest(false);
    }

    private PalmRest BuildModelPalmRest(bool isLeft)
    {
        var hand   = animator.GetBoneTransform(isLeft ? HumanBodyBones.LeftHand          : HumanBodyBones.RightHand);
        var index  = animator.GetBoneTransform(isLeft ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
        var middle = animator.GetBoneTransform(isLeft ? HumanBodyBones.LeftMiddleProximal: HumanBodyBones.RightMiddleProximal);
        var little = animator.GetBoneTransform(isLeft ? HumanBodyBones.LeftLittleProximal: HumanBodyBones.RightLittleProximal);
        if (hand == null || index == null || middle == null || little == null)
            return default;

        Vector3 forward = middle.position - hand.position;
        Vector3 side    = little.position - index.position; // index -> little
        if (!TryBuildPalmRotation(forward, side, out var palmWorld))
            return default;

        return new PalmRest
        {
            handToPalm = Quaternion.Inverse(hand.rotation) * palmWorld,
            valid = true,
        };
    }

    // Build an orthonormal rotation from a palm's forward (along the fingers)
    // and side (across the knuckles) vectors. Uses the identical formula for the
    // model and for MediaPipe so the two frames correspond and their relative
    // rotation is a pure rotation (no reflection). Returns false when the input
    // geometry is degenerate (zero-length or collinear vectors).
    private static bool TryBuildPalmRotation(Vector3 forward, Vector3 side, out Quaternion rotation)
    {
        rotation = Quaternion.identity;
        if (forward.sqrMagnitude < 1e-10f || side.sqrMagnitude < 1e-10f)
            return false;
        Vector3 normal = Vector3.Cross(forward, side);
        if (normal.sqrMagnitude < 1e-10f)
            return false;
        rotation = Quaternion.LookRotation(forward.normalized, normal.normalized);
        return true;
    }

    // Snapshot rest direction + world rotation for every finger bone so that
    // RotateBoneToChildDirection can aim them the same rig-agnostic way the
    // limbs use. The distal segments have no humanoid child bone, so we fall
    // back to the bone transform's first child (the fingertip leaf).
    private void CaptureFingerRestPose()
    {
        var fingers = new (HumanBodyBones proximal, HumanBodyBones intermediate, HumanBodyBones distal)[]
        {
            (HumanBodyBones.LeftThumbProximal,  HumanBodyBones.LeftThumbIntermediate,  HumanBodyBones.LeftThumbDistal),
            (HumanBodyBones.LeftIndexProximal,  HumanBodyBones.LeftIndexIntermediate,  HumanBodyBones.LeftIndexDistal),
            (HumanBodyBones.LeftMiddleProximal, HumanBodyBones.LeftMiddleIntermediate, HumanBodyBones.LeftMiddleDistal),
            (HumanBodyBones.LeftRingProximal,   HumanBodyBones.LeftRingIntermediate,   HumanBodyBones.LeftRingDistal),
            (HumanBodyBones.LeftLittleProximal, HumanBodyBones.LeftLittleIntermediate, HumanBodyBones.LeftLittleDistal),
            (HumanBodyBones.RightThumbProximal,  HumanBodyBones.RightThumbIntermediate,  HumanBodyBones.RightThumbDistal),
            (HumanBodyBones.RightIndexProximal,  HumanBodyBones.RightIndexIntermediate,  HumanBodyBones.RightIndexDistal),
            (HumanBodyBones.RightMiddleProximal, HumanBodyBones.RightMiddleIntermediate, HumanBodyBones.RightMiddleDistal),
            (HumanBodyBones.RightRingProximal,   HumanBodyBones.RightRingIntermediate,   HumanBodyBones.RightRingDistal),
            (HumanBodyBones.RightLittleProximal, HumanBodyBones.RightLittleIntermediate, HumanBodyBones.RightLittleDistal),
        };

        foreach (var (proximal, intermediate, distal) in fingers)
        {
            // Proximal and intermediate have a real next bone, so their rest
            // pointing direction is bone -> childBone.
            CaptureFingerSegment(proximal, intermediate, (HumanBodyBones)(-1));
            CaptureFingerSegment(intermediate, distal, (HumanBodyBones)(-1));
            // The distal bone ends the humanoid chain (no fingertip bone on most
            // VRM/VRoid rigs). Pass the intermediate as a parent fallback so its
            // rest direction is the chain continuation (parent -> distal), which
            // for a straight rest finger equals the way the distal points. This
            // avoids the old guess (t.rotation * forward) that bent the top
            // knuckle even when the real finger was straight.
            CaptureFingerSegment(distal, (HumanBodyBones)(-1), intermediate);
        }
    }

    private void CaptureFingerSegment(HumanBodyBones bone, HumanBodyBones childBone, HumanBodyBones parentFallback)
    {
        var t = animator.GetBoneTransform(bone);
        if (t == null) return;

        Transform childT = childBone >= 0 ? animator.GetBoneTransform(childBone) : null;
        if (childT == null && t.childCount > 0)
            childT = t.GetChild(0); // fingertip leaf, if the rig provides one

        Vector3 restDir;
        if (childT != null)
        {
            // Direction toward the next joint / fingertip leaf.
            restDir = childT.position - t.position;
        }
        else if (parentFallback >= 0 && animator.GetBoneTransform(parentFallback) != null)
        {
            // No child to look at: continue the chain. At rest the finger is
            // straight, so the distal points the same way as parent -> distal.
            var parentT = animator.GetBoneTransform(parentFallback);
            restDir = t.position - parentT.position;
        }
        else
        {
            restDir = t.rotation * Vector3.forward;
        }

        if (restDir.sqrMagnitude < 1e-8f) return;

        restChildDirWorld[bone] = restDir.normalized;
        restWorldRotation[bone] = t.rotation;
    }

        // ============ Body Pose ============

        /// <summary>Apply body pose from landmark data to the VRM animator.</summary>
        public void ApplyBodyPose(HolisticFrame frame)
        {
            var lm = frame.BestPoseLandmarks;
            if (lm == null) return;

            // NOTE: We intentionally do not overwrite Hips.localPosition.
            // Pose world landmarks are centered on the hip midpoint (~origin),
            // so writing that value would collapse the rig's rest offset and
            // sink/displace the model. Root motion is left to the rig.

            // ---- Hips rotation (yaw only from hip line) ----
            if (lm[PoseLandmark.LeftHip].visibility > minVisibility &&
                lm[PoseLandmark.RightHip].visibility > minVisibility)
            {
                var ht = animator.GetBoneTransform(HumanBodyBones.Hips);
                if (ht != null)
                {
                    Vector3 hipDir = ConvertDirection(lm[PoseLandmark.RightHip].AsVector3() - lm[PoseLandmark.LeftHip].AsVector3()).normalized;
                    Vector3 localHipDir = ht.parent != null ? Quaternion.Inverse(ht.parent.rotation) * hipDir : hipDir;
                    float yaw = Mathf.Atan2(localHipDir.x, localHipDir.z) * Mathf.Rad2Deg - 90f;
                    Quaternion targetLocal = Quaternion.Euler(0, yaw, 0);
                    if (!smoothedRotations.ContainsKey(HumanBodyBones.Hips))
                        smoothedRotations[HumanBodyBones.Hips] = targetLocal;
                    if (Quaternion.Angle(smoothedRotations[HumanBodyBones.Hips], targetLocal) >= 0.5f)
                        smoothedRotations[HumanBodyBones.Hips] = Quaternion.Slerp(smoothedRotations[HumanBodyBones.Hips], targetLocal, 1f - bodySmoothing);
                    ht.localRotation = smoothedRotations[HumanBodyBones.Hips];
                }
            }

            // ---- Spine chain ----
            ApplySpineChain(lm);

            // ---- Neck & Head ----
            ApplyNeckHead(lm);

            // ---- Arms ----
            ApplyArm(lm, true);   // left
            ApplyArm(lm, false);  // right

            // ---- Legs ----
            ApplyLeg(lm, true);   // left
            ApplyLeg(lm, false);  // right
        }

        /// <summary>Apply face landmarks to head blend shapes / eye look.</summary>
        public void ApplyFace(HolisticFrame frame)
        {
            if (frame == null || !frame.HasFace) return;

            // Face mapping is VRM-specific (blend shapes).
            // Subclasses or custom implementations fill this in.
            // Reserved for VRM blend-shape clip keys: Blink, Joy, Angry, etc.
            // For now, compute a basic eye-blink from eye landmark distances.

            var f = frame.faceLandmarks;
            if (f == null) return;

            // Blink detection via eye aspect ratio (left eye: 159-145 vertical, 33-133 horizontal)
            float leftEAR = EyeAspectRatio(f, 159, 145, 33, 133);
            float rightEAR = EyeAspectRatio(f, 386, 374, 362, 263);
            float avgEAR = (leftEAR + rightEAR) * 0.5f;

            // EAR < ~0.2 means eyes closed; map to a 0-1 blink value
            float blink = Mathf.Clamp01((0.3f - avgEAR) / 0.2f);

            // Delegate to whatever blend-shape system the user has wired up
            if (OnBlinkUpdated != null)
                OnBlinkUpdated(blink);

            // Mouth open: vertical lip gap (13 upper, 14 lower inner lips)
            // normalized by mouth width (61 left corner, 291 right corner).
            float mouthGap = Vector3.Distance(f[13].AsVector3(), f[14].AsVector3());
            float mouthWidth = Vector3.Distance(f[61].AsVector3(), f[291].AsVector3());
            float mouthRatio = mouthWidth > 0.0001f ? mouthGap / mouthWidth : 0f;
            // Closed mouth sits near ~0.05; fully open near ~0.6.
            float mouthOpen = Mathf.Clamp01((mouthRatio - 0.05f) / 0.55f);

            if (OnMouthOpenUpdated != null)
                OnMouthOpenUpdated(mouthOpen);
        }

        private static float EyeAspectRatio(Landmark[] f, int top, int bottom, int left, int right)
        {
            float v = Vector3.Distance(f[top].AsVector3(), f[bottom].AsVector3());
            float h = Vector3.Distance(f[left].AsVector3(), f[right].AsVector3());
            return h > 0.0001f ? v / h : 0f;
        }

        // ============ Hands ============

        /// <summary>Apply hand landmarks to finger bones.</summary>
        public void ApplyHands(HolisticFrame frame)
        {
            if (frame == null) return;
            ApplySingleHand(frame.BestLeftHand, true);
            ApplySingleHand(frame.BestRightHand, false);
        }

        private void ApplySingleHand(Landmark[] hand, bool isLeft)
        {
            if (hand == null) return;
            if (hand.Length < HolisticFrame.HandLandmarkCount) return;

            // Establish the MediaPipe -> model mapping for this hand before any
            // finger is aimed. Without it, raw landmark directions are applied as
            // if the hand sat in the camera's canonical orientation, so an open
            // hand won't lie flat and a fist bends off-axis.
            if (!BeginHand(hand, isLeft)) return;

            if (isLeft)
            {
                // Thumb: CMC(1)->MCP(2), MCP(2)->IP(3), IP(3)->Tip(4)
                ApplyFingerBone(HumanBodyBones.LeftThumbProximal,     hand, HandLandmark.ThumbCMC, HandLandmark.ThumbMCP);
                ApplyFingerBone(HumanBodyBones.LeftThumbIntermediate, hand, HandLandmark.ThumbMCP, HandLandmark.ThumbIP);
                ApplyFingerBone(HumanBodyBones.LeftThumbDistal,       hand, HandLandmark.ThumbIP,  HandLandmark.ThumbTip);

                ApplyFingerBone(HumanBodyBones.LeftIndexProximal,     hand, HandLandmark.IndexMCP, HandLandmark.IndexPIP);
                ApplyFingerBone(HumanBodyBones.LeftIndexIntermediate, hand, HandLandmark.IndexPIP, HandLandmark.IndexDIP);
                ApplyFingerBone(HumanBodyBones.LeftIndexDistal,       hand, HandLandmark.IndexDIP, HandLandmark.IndexTip);

                ApplyFingerBone(HumanBodyBones.LeftMiddleProximal,     hand, HandLandmark.MiddleMCP, HandLandmark.MiddlePIP);
                ApplyFingerBone(HumanBodyBones.LeftMiddleIntermediate, hand, HandLandmark.MiddlePIP, HandLandmark.MiddleDIP);
                ApplyFingerBone(HumanBodyBones.LeftMiddleDistal,       hand, HandLandmark.MiddleDIP, HandLandmark.MiddleTip);

                ApplyFingerBone(HumanBodyBones.LeftRingProximal,     hand, HandLandmark.RingMCP, HandLandmark.RingPIP);
                ApplyFingerBone(HumanBodyBones.LeftRingIntermediate, hand, HandLandmark.RingPIP, HandLandmark.RingDIP);
                ApplyFingerBone(HumanBodyBones.LeftRingDistal,       hand, HandLandmark.RingDIP, HandLandmark.RingTip);

                ApplyFingerBone(HumanBodyBones.LeftLittleProximal,     hand, HandLandmark.PinkyMCP, HandLandmark.PinkyPIP);
                ApplyFingerBone(HumanBodyBones.LeftLittleIntermediate, hand, HandLandmark.PinkyPIP, HandLandmark.PinkyDIP);
                ApplyFingerBone(HumanBodyBones.LeftLittleDistal,       hand, HandLandmark.PinkyDIP, HandLandmark.PinkyTip);
            }
            else
            {
                ApplyFingerBone(HumanBodyBones.RightThumbProximal,     hand, HandLandmark.ThumbCMC, HandLandmark.ThumbMCP);
                ApplyFingerBone(HumanBodyBones.RightThumbIntermediate, hand, HandLandmark.ThumbMCP, HandLandmark.ThumbIP);
                ApplyFingerBone(HumanBodyBones.RightThumbDistal,       hand, HandLandmark.ThumbIP,  HandLandmark.ThumbTip);

                ApplyFingerBone(HumanBodyBones.RightIndexProximal,     hand, HandLandmark.IndexMCP, HandLandmark.IndexPIP);
                ApplyFingerBone(HumanBodyBones.RightIndexIntermediate, hand, HandLandmark.IndexPIP, HandLandmark.IndexDIP);
                ApplyFingerBone(HumanBodyBones.RightIndexDistal,       hand, HandLandmark.IndexDIP, HandLandmark.IndexTip);

                ApplyFingerBone(HumanBodyBones.RightMiddleProximal,     hand, HandLandmark.MiddleMCP, HandLandmark.MiddlePIP);
                ApplyFingerBone(HumanBodyBones.RightMiddleIntermediate, hand, HandLandmark.MiddlePIP, HandLandmark.MiddleDIP);
                ApplyFingerBone(HumanBodyBones.RightMiddleDistal,       hand, HandLandmark.MiddleDIP, HandLandmark.MiddleTip);

                ApplyFingerBone(HumanBodyBones.RightRingProximal,     hand, HandLandmark.RingMCP, HandLandmark.RingPIP);
                ApplyFingerBone(HumanBodyBones.RightRingIntermediate, hand, HandLandmark.RingPIP, HandLandmark.RingDIP);
                ApplyFingerBone(HumanBodyBones.RightRingDistal,       hand, HandLandmark.RingDIP, HandLandmark.RingTip);

                ApplyFingerBone(HumanBodyBones.RightLittleProximal,     hand, HandLandmark.PinkyMCP, HandLandmark.PinkyPIP);
                ApplyFingerBone(HumanBodyBones.RightLittleIntermediate, hand, HandLandmark.PinkyPIP, HandLandmark.PinkyDIP);
                ApplyFingerBone(HumanBodyBones.RightLittleDistal,       hand, HandLandmark.PinkyDIP, HandLandmark.PinkyTip);
            }
        }

        // ============ Internal helpers ============

        private Vector3 ConvertPoint(Landmark lm)
        {
            return new Vector3(
                (mirrorX ? -lm.x : lm.x) * scale,
                (invertY ? -lm.y : lm.y) * scale,
                (invertZ ? -lm.z : lm.z) * scale
            );
        }

        private Vector3 ConvertPoint(Vector3 v)
        {
            return new Vector3(
                (mirrorX ? -v.x : v.x) * scale,
                (invertY ? -v.y : v.y) * scale,
                (invertZ ? -v.z : v.z) * scale
            );
        }


        /// <summary>Convert a direction vector (mirrors X, inverts Z when enabled; Y passes through).</summary>
        private Vector3 ConvertDirection(Vector3 v)
        {
            return new Vector3(
                (mirrorX ? -v.x : v.x) * scale,
                (invertY ? -v.y : v.y) * scale,
                (invertZ ? -v.z : v.z) * scale
            );
        }
        private static Vector3 Mid(Landmark a, Landmark b)
        {
            return new Vector3(
                (a.x + b.x) * 0.5f,
                (a.y + b.y) * 0.5f,
                (a.z + b.z) * 0.5f
            );
        }

        /// <summary>
        /// Compute rotation from two landmark points and apply to a humanoid bone.
        /// When horizontal=true, the direction vector is horizontal (used for hip/shoulder alignment).
        /// Otherwise the direction points from a鈫抌 (used for limbs).
        /// </summary>
        private void ApplyRotationFromTwoPoints(Landmark[] lm, HumanBodyBones bone,
            int idxA, int idxB, bool horizontal, float smoothing)
        {
            var t = animator.GetBoneTransform(bone);
            if (t == null) return;
            if (lm[idxA].visibility < minVisibility || lm[idxB].visibility < minVisibility) return;

            Vector3 dir;
            if (horizontal)
            {
                // Direction from left鈫抮ight landmark in world space
                dir = new Vector3(
                    lm[idxB].x - lm[idxA].x,
                    lm[idxB].y - lm[idxA].y,
                    lm[idxB].z - lm[idxA].z
                );
            }
            else
            {
                // Direction from A鈫払 (e.g., shoulder鈫抏lbow, hip鈫択nee)
                dir = new Vector3(
                    lm[idxB].x - lm[idxA].x,
                    lm[idxB].y - lm[idxA].y,
                    lm[idxB].z - lm[idxA].z
                );
            }

            if (dir.sqrMagnitude < 0.0001f) return;
            dir = ConvertDirection(dir).normalized;

            // Build target rotation: the bone's local reference direction 锟?landark direction
            Vector3 refDir = BoneLocalRef.ContainsKey(bone) ? BoneLocalRef[bone] : Vector3.up;
            // Convert world dir to local space
            Vector3 localDir2 = t.parent != null
                ? Quaternion.Inverse(t.parent.rotation) * dir
                : dir;
            Quaternion targetLocal = Quaternion.FromToRotation(refDir, localDir2);

            // Smooth
            if (!smoothedRotations.ContainsKey(bone))
                smoothedRotations[bone] = targetLocal;
            else if (Quaternion.Angle(smoothedRotations[bone], targetLocal) >= 0.5f)
                smoothedRotations[bone] = Quaternion.Slerp(smoothedRotations[bone], targetLocal, 1f - smoothing);

            t.localRotation = smoothedRotations[bone];
        }

        private void ApplySpineChain(Landmark[] lm)
        {
            // Spine chain: Hips 锟?Spine 锟?Chest 锟?UpperChest
            // Landmarks: hip midpoint (23+24) 锟?shoulder midpoint (11+12)
            bool hasHips = lm[PoseLandmark.LeftHip].visibility > minVisibility &&
                           lm[PoseLandmark.RightHip].visibility > minVisibility;
            bool hasShoulders = lm[PoseLandmark.LeftShoulder].visibility > minVisibility &&
                                lm[PoseLandmark.RightShoulder].visibility > minVisibility;
            if (!hasHips || !hasShoulders) return;

            Vector3 hipMid = Mid(lm[PoseLandmark.LeftHip], lm[PoseLandmark.RightHip]);
            Vector3 shMid = Mid(lm[PoseLandmark.LeftShoulder], lm[PoseLandmark.RightShoulder]);
            Vector3 spineDir = ConvertDirection(shMid - hipMid).normalized;

            if (spineDir.sqrMagnitude < 0.0001f) return;

            // Apply same spine direction to Spine, Chest, UpperChest
            foreach (var bone in new[] { HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest })
           {
               var t = animator.GetBoneTransform(bone);
               if (t == null) continue;

               Vector3 localTargetDir = t.parent != null
                    ? Quaternion.Inverse(t.parent.rotation) * spineDir
                   : spineDir;
                Quaternion targetLocal = Quaternion.FromToRotation(Vector3.up, localTargetDir);
                // Remove twist to stop spinning (Y rotation around spine axis)
                Vector3 euler = targetLocal.eulerAngles; euler.y = 0; targetLocal = Quaternion.Euler(euler);

                if (!smoothedRotations.ContainsKey(bone))
                    smoothedRotations[bone] = targetLocal;
                else if (Quaternion.Angle(smoothedRotations[bone], targetLocal) >= 0.3f)
                    smoothedRotations[bone] = Quaternion.Slerp(smoothedRotations[bone], targetLocal, 1f - bodySmoothing);
                t.localRotation = smoothedRotations[bone];
            }
        }

        private void ApplyNeckHead(Landmark[] lm)
        {
            // Neck: shoulder midpoint 锟?head (nose)
            // Head: follow neck + face orientation
            bool hasShoulders = lm[PoseLandmark.LeftShoulder].visibility > minVisibility &&
                                lm[PoseLandmark.RightShoulder].visibility > minVisibility;
            bool hasNose = lm[PoseLandmark.Nose].visibility > minVisibility;
            if (!hasShoulders || !hasNose) return;

            Vector3 shMid = Mid(lm[PoseLandmark.LeftShoulder], lm[PoseLandmark.RightShoulder]);
            Vector3 nose = lm[PoseLandmark.Nose].AsVector3();
            Vector3 neckDir = ConvertDirection(nose - shMid).normalized;

            if (neckDir.sqrMagnitude < 0.0001f) return;

            foreach (var bone in new[] { HumanBodyBones.Neck, HumanBodyBones.Head })
           {
               var t = animator.GetBoneTransform(bone);
               if (t == null) continue;

               Vector3 localTargetDir = t.parent != null
                   ? Quaternion.Inverse(t.parent.rotation) * neckDir
                    : neckDir;
                Quaternion targetLocal = Quaternion.FromToRotation(Vector3.up, localTargetDir);
                // Remove twist to stop spinning (Y rotation around neck/head axis)
                Vector3 euler = targetLocal.eulerAngles; if (euler.x > 180f) euler.x -= 360f; euler.x *= 0.15f; euler.y = 0; targetLocal = Quaternion.Euler(euler);
                if (!smoothedRotations.ContainsKey(bone))
                    smoothedRotations[bone] = targetLocal;
                else if (Quaternion.Angle(smoothedRotations[bone], targetLocal) >= 0.3f)
                    smoothedRotations[bone] = Quaternion.Slerp(smoothedRotations[bone], targetLocal, 1f - bodySmoothing);
                t.localRotation = smoothedRotations[bone];
            }
        }

        private void ApplyArm(Landmark[] lm, bool isLeft)
        {
            int shoulder = isLeft ? PoseLandmark.LeftShoulder : PoseLandmark.RightShoulder;
            int elbow    = isLeft ? PoseLandmark.LeftElbow    : PoseLandmark.RightElbow;
            int wrist    = isLeft ? PoseLandmark.LeftWrist    : PoseLandmark.RightWrist;

            if (lm[shoulder].visibility < minVisibility || lm[elbow].visibility < minVisibility) return;

            var upperBone = isLeft ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm;
            var lowerBone = isLeft ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm;
            var handBone  = isLeft ? HumanBodyBones.LeftHand     : HumanBodyBones.RightHand;

            // Aim the upper arm (shoulder->elbow) first, then the lower arm
            // (elbow->wrist). Parent before child so the child reads an
            // up-to-date parent orientation.
            Vector3 shElb = lm[elbow].AsVector3() - lm[shoulder].AsVector3();
            RotateBoneToChildDirection(upperBone, lowerBone, shElb, bodySmoothing);

            if (lm[wrist].visibility > minVisibility)
            {
                Vector3 elbWr = lm[wrist].AsVector3() - lm[elbow].AsVector3();
                RotateBoneToChildDirection(lowerBone, handBone, elbWr, bodySmoothing);
            }
        }

        private void ApplyLeg(Landmark[] lm, bool isLeft)
        {
            int hip   = isLeft ? PoseLandmark.LeftHip   : PoseLandmark.RightHip;
            int knee  = isLeft ? PoseLandmark.LeftKnee  : PoseLandmark.RightKnee;
            int ankle = isLeft ? PoseLandmark.LeftAnkle : PoseLandmark.RightAnkle;

            if (lm[hip].visibility < minVisibility || lm[knee].visibility < minVisibility) return;

            var upperBone = isLeft ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg;
            var lowerBone = isLeft ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg;
            var footBone  = isLeft ? HumanBodyBones.LeftFoot     : HumanBodyBones.RightFoot;

            Vector3 hipKnee = lm[knee].AsVector3() - lm[hip].AsVector3();
            RotateBoneToChildDirection(upperBone, lowerBone, hipKnee, bodySmoothing);

            if (lm[ankle].visibility > minVisibility)
            {
                Vector3 kneeAnk = lm[ankle].AsVector3() - lm[knee].AsVector3();
                RotateBoneToChildDirection(lowerBone, footBone, kneeAnk, bodySmoothing);
            }
        }

        /// <summary>
        /// Aim <paramref name="bone"/> so that the rest-pose direction toward its
        /// child lands on the tracked landmark direction. This makes no assumption
        /// about which local axis the bone points along, so it works for arms
        /// (which rest sideways) and legs (which rest downward) alike, and avoids
        /// the cross-body flipping caused by a hardcoded reference axis.
        /// </summary>
        private void RotateBoneToChildDirection(HumanBodyBones bone, HumanBodyBones childBone,
            Vector3 worldDir, float smoothing)
        {
            // Apply the MediaPipe -> Unity coordinate conversion, then aim using
            // the model-space direction.
            AimBoneToModelDirection(bone, ConvertDirection(worldDir), smoothing);
        }

        // Aim <paramref name="bone"/> so its captured rest direction toward the
        // child lands on <paramref name="modelDir"/>, a direction already
        // expressed in the model's world space (no further coordinate
        // conversion is applied here).
        private void AimBoneToModelDirection(HumanBodyBones bone, Vector3 modelDir, float smoothing)
        {
            var t = animator.GetBoneTransform(bone);
            if (t == null) return;

            // Rest pose is captured once up front in CaptureRestPose().
            if (!restChildDirWorld.ContainsKey(bone)) return;

            Vector3 targetDir = modelDir.normalized;
            if (targetDir.sqrMagnitude < 1e-6f) return;

            // World-space delta that turns the rest direction onto the target.
            Quaternion delta = Quaternion.FromToRotation(restChildDirWorld[bone], targetDir);
            Quaternion targetWorld = delta * restWorldRotation[bone];

            // Convert desired world rotation into the bone's local space.
            Quaternion targetLocal = t.parent != null
                ? Quaternion.Inverse(t.parent.rotation) * targetWorld
                : targetWorld;

            if (!smoothedRotations.ContainsKey(bone))
                smoothedRotations[bone] = targetLocal;
            else if (Quaternion.Angle(smoothedRotations[bone], targetLocal) >= 0.5f)
                smoothedRotations[bone] = Quaternion.Slerp(smoothedRotations[bone], targetLocal, 1f - smoothing);

            t.localRotation = smoothedRotations[bone];
        }

        private void ApplyBoneDirection(HumanBodyBones bone, Vector3 worldDir,
            Vector3 refLocalDir, float smoothing, Vector3? worldUp = null, bool armTwist180 = false)
        {
            // Retained for legacy/manual use; arm and leg chains now use
            // RotateBoneToChildDirection (rig-agnostic, no fixed rest axis).
            var t = animator.GetBoneTransform(bone);
            if (t == null) return;

            Vector3 dir = ConvertDirection(worldDir).normalized;
            if (dir.sqrMagnitude < 0.0001f) return;

            // Convert world dir to local space, then compute rotation
            Vector3 localTargetDir = t.parent != null
                ? Quaternion.Inverse(t.parent.rotation) * dir
                : dir;
            Quaternion targetLocal = Quaternion.FromToRotation(refLocalDir, localTargetDir);
            if (armTwist180)
            {
                targetLocal *= Quaternion.AngleAxis(180f, Vector3.up);
            }
            else // Twist correction: align bone X/Z to arm-plane normal (cross of upper/lower arm directions)
            if (worldUp.HasValue && worldUp.Value.sqrMagnitude > 0.0001f)
            {
                Vector3 localNormal = t.parent != null
                    ? Quaternion.Inverse(t.parent.rotation) * ConvertDirection(worldUp.Value).normalized
                    : ConvertDirection(worldUp.Value).normalized;
                // Desired forward: lies in arm plane, perpendicular to bone direction
                Vector3 desiredFwd = Vector3.Cross(localTargetDir, localNormal).normalized;
                if (desiredFwd.sqrMagnitude > 0.0001f)
                {
                    Vector3 currentFwd = Vector3.ProjectOnPlane(targetLocal * Vector3.forward, localTargetDir).normalized;
                    if (currentFwd.sqrMagnitude > 0.0001f)
                    {
                        float twistAngle = Vector3.SignedAngle(currentFwd, desiredFwd, localTargetDir);
                        targetLocal *= Quaternion.AngleAxis(twistAngle, Vector3.up);
                    }
                }
            }

            if (!smoothedRotations.ContainsKey(bone))
                smoothedRotations[bone] = targetLocal;
            else if (Quaternion.Angle(smoothedRotations[bone], targetLocal) >= 0.5f)
                smoothedRotations[bone] = Quaternion.Slerp(smoothedRotations[bone], targetLocal, 1f - smoothing);

            t.localRotation = smoothedRotations[bone];
        }

        // Establish the per-hand mapping that takes MediaPipe finger directions
        // into the model's world space. Builds a palm coordinate frame from the
        // tracked hand landmarks (forward = wrist -> middle knuckle, side = index
        // knuckle -> little knuckle, up = palm normal) and aligns it with the
        // model's live palm frame. Because both frames are built from the same
        // anatomical features, the relative rotation transfers finger curl
        // correctly no matter how the hand is oriented in space, and it is
        // independent of how the wrist sits in space. The MediaPipe vectors are
        // run through ConvertDirection first so the
        // palm frame lives in the same converted space the body path uses; with
        // the default flags that space matches Unity's handedness, so the palm
        // normals correspond and finger curl transfers without flipping.
        private bool BeginHand(Landmark[] hand, bool isLeft)
        {
            _currentHandMappingValid = false;

            var palmRest = isLeft ? _leftPalmRest : _rightPalmRest;
            if (!palmRest.valid) return false;

            var handBone = animator.GetBoneTransform(isLeft ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            if (handBone == null) return false;

            Vector3 mpForward = ConvertDirection(hand[HandLandmark.MiddleMCP].AsVector3() - hand[HandLandmark.Wrist].AsVector3());
            Vector3 mpSide    = ConvertDirection(hand[HandLandmark.PinkyMCP].AsVector3()  - hand[HandLandmark.IndexMCP].AsVector3());
            if (!TryBuildPalmRotation(mpForward, mpSide, out var mpPalm)) return false;

            // Build the finger mapping from the palm frame as it is RIGHT NOW,
            // before driving the wrist. At this point handBone.rotation only
            // reflects the forearm aim, which is a stable, body-frame palm. The
            // mapping converts tracked finger directions into that palm:
            //   direction_model = preDrivePalm * (inverse(mpPalm) * convertedDir).
            // The inverse(mpPalm) step is essential: it strips the MediaPipe
            // hand's own frame and keeps only the palm-relative curl, so the
            // finger directions don't depend on the absolute (and less reliable)
            // orientation of the hand landmarks. Fingers are then aimed in world
            // space and converted to local through their CURRENT (driven) parent,
            // so correcting the wrist below does not disturb the finger curl.
            Quaternion preDrivePalm = handBone.rotation * palmRest.handToPalm;
            _currentHandMpToModel = preDrivePalm * Quaternion.Inverse(mpPalm);
            _currentHandMappingValid = true;

            // Now correct the wrist itself. The arm chain only aims the forearm
            // (elbow -> wrist), leaving the hand bone at its rest roll, which
            // reads as the palm angled outward. mpPalm is already in the model's
            // world space (ConvertDirection maps MediaPipe coords to Unity coords
            // like the body path), so the desired hand-bone world rotation is
            // mpPalm * inverse(handToPalm). Doing this AFTER the finger mapping
            // keeps the two independent.
            Quaternion targetHandWorld = mpPalm * Quaternion.Inverse(palmRest.handToPalm);
            SetBoneWorldRotation(isLeft ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand,
                targetHandWorld, handSmoothing);

            return true;
        }

        // Drive a bone toward a desired world rotation with the shared EMA
        // smoothing, then write it back through local space.
        private void SetBoneWorldRotation(HumanBodyBones bone, Quaternion targetWorld, float smoothing)
        {
            var t = animator.GetBoneTransform(bone);
            if (t == null) return;

            Quaternion targetLocal = t.parent != null
                ? Quaternion.Inverse(t.parent.rotation) * targetWorld
                : targetWorld;

            if (!smoothedRotations.ContainsKey(bone))
                smoothedRotations[bone] = targetLocal;
            else if (Quaternion.Angle(smoothedRotations[bone], targetLocal) >= 0.5f)
                smoothedRotations[bone] = Quaternion.Slerp(smoothedRotations[bone], targetLocal, 1f - smoothing);

            t.localRotation = smoothedRotations[bone];
        }

        // Aim a single finger bone toward the MediaPipe landmark direction.
        // Resolves the bone through the humanoid rig (GetBoneTransform) so it
        // works regardless of how the VRM names its finger GameObjects. The
        // MediaPipe segment direction is coordinate-converted and mapped through
        // the palm frame alignment established in BeginHand, then aimed using the
        // captured rest pose.
        private void ApplyFingerBone(HumanBodyBones bone, Landmark[] hand, int idxA, int idxB)
        {
            if (!_currentHandMappingValid) return;
            if (idxA >= hand.Length || idxB >= hand.Length) return;

            Vector3 mpDir = ConvertDirection(new Vector3(
                hand[idxB].x - hand[idxA].x,
                hand[idxB].y - hand[idxA].y,
                hand[idxB].z - hand[idxA].z
            ));
            if (mpDir.sqrMagnitude < 1e-10f) return;

            Vector3 modelDir = _currentHandMpToModel * mpDir;
            AimBoneToModelDirection(bone, modelDir, handSmoothing);
        }
    }
}
