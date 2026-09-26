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
        [Serializable]
        public struct BoneMapping
        {
            public HumanBodyBones role;
            public HumanBodyBones target;

            public BoneMapping(HumanBodyBones role, HumanBodyBones target)
            {
                this.role = role;
                this.target = target;
            }
        }

        private Dictionary<HumanBodyBones, HumanBodyBones> boneMap;

        /// <summary>
        /// Creates the complete default table. The role and target are equal,
        /// which preserves the original Humanoid mapping behavior.
        /// </summary>
        public static List<BoneMapping> CreateDefaultBoneMappings()
        {
            var result = new List<BoneMapping>();
            foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (bone == HumanBodyBones.LastBone)
                    continue;
                result.Add(new BoneMapping(bone, bone));
            }
            return result;
        }

        // ---- Tunable parameters ----
        public float bodySmoothing = 0.3f;          // EMA factor (lower = smoother)
        public float handSmoothing = 0.2f;
        public bool mirrorX = true;                  // flip MediaPipe X ï¿?Unity X
        public bool invertZ = true;                  // flip MediaPipe Z ï¿?Unity Z
        public float scale = 1.0f;                   // world landmark scale multiplier
        public float minVisibility = 0.5f;           // ignore landmarks below this
        public bool useWorldLandmarks = true;        // prefer world over normalized
        public bool invertY = true;                  // flip MediaPipe Y (down) to Unity Y (up)

        // When true the hips, spine chain and legs are left in their rest pose so
        // the avatar stays upright and does not lean/tilt with the tracked torso.
        // Arms, neck/head, face and hands continue to track. Arms stay enabled
        // because hand placement is driven by the arm chain.
        public bool lockLowerBody = true;

        // Extra pitch (degrees) added to the Head bone after tracking. Positive
        // values lift the head up; use this to correct models whose mapped head
        // ends up looking too low. Applied to the Head bone only (not the Neck).
        public float headPitchOffset = 0f;

        // How strongly the tracked head nod (pitch about local X) is applied.
        // The raw shoulder->nose pitch is noisy, so it used to be hard-damped to
        // 0.15 which made nodding almost invisible. Raise this toward 1 (or more)
        // for a pronounced nod.
        public float headPitchScale = 0.8f;

        private Animator animator;
        private Dictionary<HumanBodyBones, Quaternion> smoothedRotations;

        // Rest-pose cache for direction-based aiming (captured lazily on first use,
        // while the model is still in its imported rest/T-pose).
        private Dictionary<HumanBodyBones, Vector3> restChildDirWorld = new Dictionary<HumanBodyBones, Vector3>();
        private Dictionary<HumanBodyBones, Quaternion> restWorldRotation = new Dictionary<HumanBodyBones, Quaternion>();
        private Dictionary<HumanBodyBones, Quaternion> restLocalRotation = new Dictionary<HumanBodyBones, Quaternion>();
        private bool lastLockLowerBody;

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
    /// This is the combined (both-eyes) value, kept for callers that only want a
    /// single blink channel. Prefer the per-eye callbacks below for winks.
    /// </summary>
    public Action<float> OnBlinkUpdated;

    /// <summary>
    /// Invoked each frame with the avatar's left-eye blink value (0..1).
    /// Wire this to the VRM "BlinkLeft" expression so a single eye can close.
    /// </summary>
    public Action<float> OnBlinkLeftUpdated;

    /// <summary>
    /// Invoked each frame with the avatar's right-eye blink value (0..1).
    /// Wire this to the VRM "BlinkRight" expression so a single eye can close.
    /// </summary>
    public Action<float> OnBlinkRightUpdated;

    /// <summary>
    /// Invoked each frame with a 0..1 mouth-open value computed from face
    /// landmarks. Wire this to the VRM "Aa" expression.
    /// </summary>
    public Action<float> OnMouthOpenUpdated;

    /// <summary>Invoked each frame with a 0..1 narrow-mouth vowel value.</summary>
    public Action<float> OnMouthIhUpdated;

    /// <summary>Invoked each frame with a 0..1 wide-mouth vowel value.</summary>
    public Action<float> OnMouthEeUpdated;

    /// <summary>Invoked each frame with a 0..1 rounded-mouth vowel value.</summary>
    public Action<float> OnMouthOuUpdated;

    /// <summary>Invoked each frame with a 0..1 open-rounded vowel value.</summary>
    public Action<float> OnMouthOhUpdated;

    /// <summary>Invoked each frame with a 0..1 smile value.</summary>
    public Action<float> OnHappyUpdated;

    /// <summary>Invoked each frame with a 0..1 brow-down / angry value.</summary>
    public Action<float> OnAngryUpdated;

    /// <summary>Invoked each frame with a 0..1 surprised value.</summary>
    public Action<float> OnSurprisedUpdated;

    /// <summary>Invoked each frame with a 0..1 sad / frown value.</summary>
    public Action<float> OnSadUpdated;

    /// <summary>Invoked each frame with a 0..1 relaxed value.</summary>
    public Action<float> OnRelaxedUpdated;

    public HolisticToVRMBoneMapper(Animator animator, IList<BoneMapping> mappings = null)
    {
        this.animator = animator;
        boneMap = new Dictionary<HumanBodyBones, HumanBodyBones>();
        smoothedRotations = new Dictionary<HumanBodyBones, Quaternion>();
        lastLockLowerBody = lockLowerBody;
        ApplyBoneMappings(mappings, true);
    }

    public void ApplyBoneMappings(IList<BoneMapping> mappings)
    {
        ApplyBoneMappings(mappings, false);
    }

    public void ApplyBoneMappings(IList<BoneMapping> mappings, bool recaptureRestPose)
    {
        boneMap.Clear();
        if (mappings != null)
        {
            foreach (var mapping in mappings)
            {
                if (mapping.role == HumanBodyBones.LastBone)
                    continue;
                boneMap[mapping.role] = mapping.target;
            }
        }

        smoothedRotations.Clear();
        if (recaptureRestPose)
        {
            restChildDirWorld.Clear();
            restWorldRotation.Clear();
            restLocalRotation.Clear();
            CaptureRestPose();
        }
    }

    private HumanBodyBones ResolveBone(HumanBodyBones role)
    {
        HumanBodyBones target;
        return boneMap != null && boneMap.TryGetValue(role, out target)
            ? target
            : role;
    }

    private Transform GetBoneTransform(HumanBodyBones role)
    {
        HumanBodyBones target = ResolveBone(role);
        return target == HumanBodyBones.LastBone ? null : animator.GetBoneTransform(target);
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
            var t = GetBoneTransform(bone);
            var childT = GetBoneTransform(child);
            if (t == null) continue;
            Vector3 restDir = childT != null
                ? (childT.position - t.position)
                : t.rotation * Vector3.down;
            if (restDir.sqrMagnitude < 1e-8f) continue;
            restChildDirWorld[bone] = restDir.normalized;
            restWorldRotation[bone] = t.rotation;
        }

        var lowerBodyBones = new[]
        {
            HumanBodyBones.Hips,
            HumanBodyBones.Spine,
            HumanBodyBones.Chest,
            HumanBodyBones.UpperChest,
            HumanBodyBones.LeftUpperLeg,
            HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.LeftFoot,
            HumanBodyBones.RightUpperLeg,
            HumanBodyBones.RightLowerLeg,
            HumanBodyBones.RightFoot,
        };
        foreach (var bone in lowerBodyBones)
        {
            var t = GetBoneTransform(bone);
            if (t == null) continue;
            restLocalRotation[bone] = t.localRotation;
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
        var hand   = GetBoneTransform(isLeft ? HumanBodyBones.LeftHand          : HumanBodyBones.RightHand);
        var index  = GetBoneTransform(isLeft ? HumanBodyBones.LeftIndexProximal : HumanBodyBones.RightIndexProximal);
        var middle = GetBoneTransform(isLeft ? HumanBodyBones.LeftMiddleProximal: HumanBodyBones.RightMiddleProximal);
        var little = GetBoneTransform(isLeft ? HumanBodyBones.LeftLittleProximal: HumanBodyBones.RightLittleProximal);
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
        var t = GetBoneTransform(bone);
        if (t == null) return;

        Transform childT = childBone >= 0 ? GetBoneTransform(childBone) : null;
        if (childT == null && t.childCount > 0)
            childT = t.GetChild(0); // fingertip leaf, if the rig provides one

        Vector3 restDir;
        if (childT != null)
        {
            // Direction toward the next joint / fingertip leaf.
            restDir = childT.position - t.position;
        }
        else if (parentFallback >= 0 && GetBoneTransform(parentFallback) != null)
        {
            // No child to look at: continue the chain. At rest the finger is
            // straight, so the distal points the same way as parent -> distal.
            var parentT = GetBoneTransform(parentFallback);
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

    public void ResetLowerBodyPose()
    {
        var bones = new[]
        {
            HumanBodyBones.Hips,
            HumanBodyBones.Spine,
            HumanBodyBones.Chest,
            HumanBodyBones.UpperChest,
            HumanBodyBones.LeftUpperLeg,
            HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.LeftFoot,
            HumanBodyBones.RightUpperLeg,
            HumanBodyBones.RightLowerLeg,
            HumanBodyBones.RightFoot,
        };

        foreach (var bone in bones)
        {
            Quaternion rest;
            if (!restLocalRotation.TryGetValue(bone, out rest))
                continue;
            var t = GetBoneTransform(bone);
            if (t == null) continue;

            t.localRotation = rest;
            smoothedRotations[bone] = rest;
        }
    }
        // ============ Body Pose ============

        /// <summary>Apply body pose from landmark data to the VRM animator.</summary>
        public void ApplyBodyPose(HolisticFrame frame)
        {
            var lm = frame.BestPoseLandmarks;
            if (lm == null) return;

            if (lockLowerBody && !lastLockLowerBody)
                ResetLowerBodyPose();
            lastLockLowerBody = lockLowerBody;

            // NOTE: We intentionally do not overwrite Hips.localPosition.
            // Pose world landmarks are centered on the hip midpoint (~origin),
            // so writing that value would collapse the rig's rest offset and
            // sink/displace the model. Root motion is left to the rig.

            // ---- Hips rotation (yaw only from hip line) ----
            if (!lockLowerBody &&
                lm[PoseLandmark.LeftHip].visibility > minVisibility &&
                lm[PoseLandmark.RightHip].visibility > minVisibility)
            {
                var ht = GetBoneTransform(HumanBodyBones.Hips);
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
            if (!lockLowerBody)
                ApplySpineChain(lm);

            // ---- Neck & Head ----
            ApplyNeckHead(lm);

            // ---- Arms ----
            ApplyArm(lm, true);   // left
            ApplyArm(lm, false);  // right

            // ---- Legs ----
            if (!lockLowerBody)
            {
                ApplyLeg(lm, true);   // left
                ApplyLeg(lm, false);  // right
            }
        }

        /// <summary>Apply face landmarks to head blend shapes / eye look.</summary>
        public void ApplyFace(HolisticFrame frame)
        {
            if (frame == null || !frame.HasFace) return;

            var f = frame.faceLandmarks;
            if (f == null) return;

            // Per-eye blink, evaluated independently so a single-eye wink works
            // (closing one eye must not pull the other shut).
            float avatarLeftBlink, avatarRightBlink;

            if (frame.HasFaceBlendshapes)
            {
                // Preferred path: MediaPipe's ARKit-style face blendshapes expose
                // dedicated eyeBlinkLeft / eyeBlinkRight scores. These genuinely
                // separate the two eyes, unlike the near-symmetric face mesh that
                // the geometric EAR below reads from. The scores are anatomical
                // (relative to the tracked subject): eyeBlinkLeft is the subject's
                // own left eye. For a selfie-mirrored view the avatar should copy
                // the user like a mirror, so swap sides when mirrorX is set. If
                // the eyes ever feel reversed, flip the mirrorX toggle.
                float subjectLeftBlink = frame.GetBlendshape("eyeBlinkLeft");
                float subjectRightBlink = frame.GetBlendshape("eyeBlinkRight");

                avatarLeftBlink = mirrorX ? subjectRightBlink : subjectLeftBlink;
                avatarRightBlink = mirrorX ? subjectLeftBlink : subjectRightBlink;
            }
            else
            {
                // Fallback (e.g. the UDP/Python path, which sends no blendshapes):
                // estimate blink from the eye aspect ratio. The 159/145 + 33/133
                // group is the image's left eye; 386/374 + 362/263 is the image's
                // right eye.
                float imageLeftEAR = EyeAspectRatio(f, 159, 145, 33, 133);
                float imageRightEAR = EyeAspectRatio(f, 386, 374, 362, 263);

                // EAR < ~0.2 means an eye is closed; map to a 0-1 blink value.
                float imageLeftBlink = Mathf.Clamp01((0.3f - imageLeftEAR) / 0.2f);
                float imageRightBlink = Mathf.Clamp01((0.3f - imageRightEAR) / 0.2f);

                // Map image-space eyes to the avatar's own left/right. When the X
                // axis is mirrored (selfie-style), the image's left eye drives the
                // avatar's right expression, and vice versa.
                avatarLeftBlink = mirrorX ? imageRightBlink : imageLeftBlink;
                avatarRightBlink = mirrorX ? imageLeftBlink : imageRightBlink;
            }

            // Per-eye callbacks let a single eye close independently.
            OnBlinkLeftUpdated?.Invoke(avatarLeftBlink);
            OnBlinkRightUpdated?.Invoke(avatarRightBlink);

            // Combined channel kept for callers wired only to the single Blink
            // expression: take the stronger eye so a deliberate wink still reads
            // instead of being halved by the open eye.
            OnBlinkUpdated?.Invoke(Mathf.Max(avatarLeftBlink, avatarRightBlink));

            // Mouth open: vertical lip gap (13 upper, 14 lower inner lips)
            // normalized by mouth width (61 left corner, 291 right corner).
            float mouthGap = Vector3.Distance(f[13].AsVector3(), f[14].AsVector3());
            float mouthWidth = Vector3.Distance(f[61].AsVector3(), f[291].AsVector3());
            float mouthRatio = mouthWidth > 0.0001f ? mouthGap / mouthWidth : 0f;
            // Closed mouth sits near ~0.05; fully open near ~0.6.
            float mouthOpen = Mathf.Clamp01((mouthRatio - 0.05f) / 0.55f);

            OnMouthOpenUpdated?.Invoke(mouthOpen);

            float smile = 0f;
            float angry = 0f;
            float surprised = 0f;
            float sad = 0f;
            float relaxed = 0f;
            float mouthIh = 0f;
            float mouthEe = 0f;
            float mouthOu = 0f;
            float mouthOh = 0f;

            if (frame.HasFaceBlendshapes)
            {
                float smileLeft = frame.GetBlendshape("mouthSmileLeft");
                float smileRight = frame.GetBlendshape("mouthSmileRight");
                float smileBlend = Average(smileLeft, smileRight);

                float frownLeft = frame.GetBlendshape("mouthFrownLeft");
                float frownRight = frame.GetBlendshape("mouthFrownRight");
                float browDownLeft = frame.GetBlendshape("browDownLeft");
                float browDownRight = frame.GetBlendshape("browDownRight");
                float browInnerUp = frame.GetBlendshape("browInnerUp");
                float eyeWideLeft = frame.GetBlendshape("eyeWideLeft");
                float eyeWideRight = frame.GetBlendshape("eyeWideRight");
                float jawOpen = frame.GetBlendshape("jawOpen", mouthOpen);
                float mouthFunnel = frame.GetBlendshape("mouthFunnel");
                float mouthPucker = frame.GetBlendshape("mouthPucker");
                float stretchLeft = frame.GetBlendshape("mouthStretchLeft");
                float stretchRight = frame.GetBlendshape("mouthStretchRight");

                float stretchBlend = Average(stretchLeft, stretchRight);
                float frownBlend = Average(frownLeft, frownRight);

                mouthIh = Mathf.Clamp01(stretchBlend * (1f - mouthPucker * 0.5f));
                mouthEe = Mathf.Clamp01(Mathf.Max(smileBlend, stretchBlend) * (1f - jawOpen * 0.35f));
                mouthOu = Mathf.Clamp01(Mathf.Max(mouthPucker, mouthFunnel));
                mouthOh = Mathf.Clamp01(mouthFunnel * 0.65f + jawOpen * 0.35f);

                smile = Mathf.Clamp01(smileBlend * (1f - frownBlend * 0.5f));
                angry = Mathf.Clamp01(Average(browDownLeft, browDownRight));
                surprised = Mathf.Clamp01(browInnerUp * 0.45f + Average(eyeWideLeft, eyeWideRight) * 0.25f + jawOpen * 0.3f);
                sad = Mathf.Clamp01(frownBlend * 0.65f + browInnerUp * 0.35f);
                relaxed = Mathf.Clamp01(frame.GetBlendshape("mouthClose") * 0.35f + (1f - angry) * (1f - surprised) * 0.25f);
            }
            else
            {
                float mouthCornerLift = MouthCornerLift(f);
                float mouthRoundness = MouthRoundness(f, mouthRatio);
                float browDown = BrowDownRatio(f);
                float eyeOpen = Average(
                    Mathf.Clamp01((EyeAspectRatio(f, 159, 145, 33, 133) - 0.18f) / 0.22f),
                    Mathf.Clamp01((EyeAspectRatio(f, 386, 374, 362, 263) - 0.18f) / 0.22f));

                smile = Mathf.Clamp01(mouthCornerLift);
                angry = Mathf.Clamp01(browDown);
                surprised = Mathf.Clamp01(mouthOpen * 0.55f + eyeOpen * 0.25f + (1f - browDown) * 0.2f);
                sad = Mathf.Clamp01((-mouthCornerLift) * 0.7f + browDown * 0.2f);
                relaxed = Mathf.Clamp01((1f - mouthOpen) * (1f - angry) * 0.35f);

                mouthIh = Mathf.Clamp01((mouthWidth / Mathf.Max(mouthGap, 0.0001f) - 2.5f) / 4f) * (1f - mouthOpen * 0.4f);
                mouthEe = Mathf.Clamp01(smile * 0.75f + mouthIh * 0.25f);
                mouthOu = mouthRoundness;
                mouthOh = Mathf.Clamp01(mouthRoundness * 0.65f + mouthOpen * 0.35f);
            }

            InvokeExpandedFaceCallbacks(mouthIh, mouthEe, mouthOu, mouthOh, smile, angry, surprised, sad, relaxed);
        }

        private void InvokeExpandedFaceCallbacks(
            float mouthIh,
            float mouthEe,
            float mouthOu,
            float mouthOh,
            float smile,
            float angry,
            float surprised,
            float sad,
            float relaxed)
        {
            OnMouthIhUpdated?.Invoke(mouthIh);
            OnMouthEeUpdated?.Invoke(mouthEe);
            OnMouthOuUpdated?.Invoke(mouthOu);
            OnMouthOhUpdated?.Invoke(mouthOh);
            OnHappyUpdated?.Invoke(smile);
            OnAngryUpdated?.Invoke(angry);
            OnSurprisedUpdated?.Invoke(surprised);
            OnSadUpdated?.Invoke(sad);
            OnRelaxedUpdated?.Invoke(relaxed);
        }

        private static float EyeAspectRatio(Landmark[] f, int top, int bottom, int left, int right)
        {
            float v = Vector3.Distance(f[top].AsVector3(), f[bottom].AsVector3());
            float h = Vector3.Distance(f[left].AsVector3(), f[right].AsVector3());
            return h > 0.0001f ? v / h : 0f;
        }

        private static float Average(float a, float b)
        {
            return (a + b) * 0.5f;
        }

        private static float MouthCornerLift(Landmark[] f)
        {
            Vector3 leftCorner = f[61].AsVector3();
            Vector3 rightCorner = f[291].AsVector3();
            Vector3 upperLip = f[13].AsVector3();
            Vector3 lowerLip = f[14].AsVector3();
            float mouthWidth = Vector3.Distance(leftCorner, rightCorner);
            if (mouthWidth <= 0.0001f) return 0f;

            float lipCenterY = (upperLip.y + lowerLip.y) * 0.5f;
            float cornerY = (leftCorner.y + rightCorner.y) * 0.5f;
            return Mathf.Clamp((lipCenterY - cornerY) / mouthWidth * 5f, -1f, 1f);
        }

        private static float MouthRoundness(Landmark[] f, float mouthRatio)
        {
            float innerWidth = Vector3.Distance(f[78].AsVector3(), f[308].AsVector3());
            float outerWidth = Vector3.Distance(f[61].AsVector3(), f[291].AsVector3());
            if (outerWidth <= 0.0001f) return 0f;

            float narrow = Mathf.Clamp01(1f - innerWidth / outerWidth);
            float open = Mathf.Clamp01((mouthRatio - 0.08f) / 0.45f);
            return Mathf.Clamp01(narrow * 0.65f + open * 0.35f);
        }

        private static float BrowDownRatio(Landmark[] f)
        {
            float leftEyeBrow = Mathf.Abs(f[70].y - f[159].y);
            float rightEyeBrow = Mathf.Abs(f[300].y - f[386].y);
            float faceHeight = Mathf.Abs(f[10].y - f[152].y);
            if (faceHeight <= 0.0001f) return 0f;

            float normalized = Average(leftEyeBrow, rightEyeBrow) / faceHeight;
            return Mathf.Clamp01((0.09f - normalized) / 0.05f);
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
        /// Otherwise the direction points from aâ†’b (used for limbs).
        /// </summary>
        private void ApplyRotationFromTwoPoints(Landmark[] lm, HumanBodyBones bone,
            int idxA, int idxB, bool horizontal, float smoothing)
        {
            var t = GetBoneTransform(bone);
            if (t == null) return;
            if (lm[idxA].visibility < minVisibility || lm[idxB].visibility < minVisibility) return;

            Vector3 dir;
            if (horizontal)
            {
                // Direction from leftâ†’right landmark in world space
                dir = new Vector3(
                    lm[idxB].x - lm[idxA].x,
                    lm[idxB].y - lm[idxA].y,
                    lm[idxB].z - lm[idxA].z
                );
            }
            else
            {
                // Direction from Aâ†’B (e.g., shoulderâ†’elbow, hipâ†’knee)
                dir = new Vector3(
                    lm[idxB].x - lm[idxA].x,
                    lm[idxB].y - lm[idxA].y,
                    lm[idxB].z - lm[idxA].z
                );
            }

            if (dir.sqrMagnitude < 0.0001f) return;
            dir = ConvertDirection(dir).normalized;

            // Build target rotation: the bone's local reference direction ï¿?landark direction
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
            // Spine chain: Hips ï¿?Spine ï¿?Chest ï¿?UpperChest
            // Landmarks: hip midpoint (23+24) ï¿?shoulder midpoint (11+12)
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
               var t = GetBoneTransform(bone);
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
                Quaternion finalLocal = smoothedRotations[bone];
                // Lift/lower the head to correct a model whose mapped head sits
                // too low. Applied after smoothing so it is a stable bias, not
                // something the EMA chases. Negative pitch about local X raises
                // the gaze for typical VRM head bones.
                if (bone == HumanBodyBones.Head && Mathf.Abs(headPitchOffset) > 0.0001f)
                    finalLocal = finalLocal * Quaternion.Euler(-headPitchOffset, 0f, 0f);
                t.localRotation = finalLocal;
            }
        }

        private void ApplyNeckHead(Landmark[] lm)
        {
            // Neck: shoulder midpoint ï¿?head (nose)
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
               var t = GetBoneTransform(bone);
               if (t == null) continue;

               Vector3 localTargetDir = t.parent != null
                   ? Quaternion.Inverse(t.parent.rotation) * neckDir
                    : neckDir;
                Quaternion targetLocal = Quaternion.FromToRotation(Vector3.up, localTargetDir);
                // Remove twist to stop spinning (Y rotation around neck/head axis)
                // Keep pitch (X = nod) and drop yaw twist (Y). headPitchScale
                // controls how much of the tracked nod is applied.
                Vector3 euler = targetLocal.eulerAngles;
                if (euler.x > 180f) euler.x -= 360f;
                euler.x *= headPitchScale;
                euler.y = 0;
                targetLocal = Quaternion.Euler(euler);
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
            var t = GetBoneTransform(bone);
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
            var t = GetBoneTransform(bone);
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

            var handBone = GetBoneTransform(isLeft ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            if (handBone == null) return false;

            Vector3 mpForward = ConvertDirection(hand[HandLandmark.MiddleMCP].AsVector3() - hand[HandLandmark.Wrist].AsVector3());
            Vector3 mpSide    = ConvertDirection(hand[HandLandmark.PinkyMCP].AsVector3()  - hand[HandLandmark.IndexMCP].AsVector3());
            if (!TryBuildPalmRotation(mpForward, mpSide, out var mpPalm)) return false;

            // Correct the wrist first. The arm chain only aims the forearm
            // (elbow -> wrist), leaving the hand bone at its rest roll, which
            // reads as the palm angled outward. Since SetBoneWorldRotation uses
            // smoothing, handBone.rotation below is the rotation that actually
            // took effect this frame, rather than the unsmoothed target.
            Quaternion targetHandWorld = mpPalm * Quaternion.Inverse(palmRest.handToPalm);
            SetBoneWorldRotation(isLeft ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand,
                targetHandWorld, handSmoothing);

            // Build the finger mapping from the palm rotation that actually took
            // effect. Using the target before smoothing leaves the hand bone and
            // proximal finger directions in different frames, which opens a
            // visible gap at the palm/knuckle junction on rigs with non-trivial
            // hand-bone axes.
            Quaternion livePalm = handBone.rotation * palmRest.handToPalm;
            _currentHandMpToModel = livePalm * Quaternion.Inverse(mpPalm);
            _currentHandMappingValid = true;

            return true;
        }

        // Drive a bone toward a desired world rotation with the shared EMA
        // smoothing, then write it back through local space.
        private void SetBoneWorldRotation(HumanBodyBones bone, Quaternion targetWorld, float smoothing)
        {
            var t = GetBoneTransform(bone);
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
