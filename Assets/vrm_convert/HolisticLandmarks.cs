using UnityEngine;

namespace CCMediaPipe
{
    /// <summary>
    /// A single landmark with 3D position and visibility/presence.
    /// MediaPipe provides (x, y, z) — for world landmarks these are in meters;
    /// for normalized landmarks x,y are in [0,1] image space and z is depth.
    /// </summary>
    [System.Serializable]
    public struct Landmark
    {
        public float x;
        public float y;
        public float z;
        /// <summary>Visibility [0..1] for pose landmarks; presence [0..1] for hand landmarks.</summary>
        public float visibility;

        public Vector3 AsVector3() => new Vector3(x, y, z);

        public static Landmark Lerp(Landmark a, Landmark b, float t)
        {
            return new Landmark
            {
                x = Mathf.Lerp(a.x, b.x, t),
                y = Mathf.Lerp(a.y, b.y, t),
                z = Mathf.Lerp(a.z, b.z, t),
                visibility = Mathf.Lerp(a.visibility, b.visibility, t),
            };
        }
    }

    /// <summary>
    /// Complete Holistic frame: body pose (33), face mesh (468),
    /// and both hands (21 each). World landmarks are preferred for 3D mapping;
    /// normalized landmarks serve as fallback / 2D reference.
    /// </summary>
    [System.Serializable]
    public class HolisticFrame
    {
        public const int PoseLandmarkCount = 33;
        public const int FaceLandmarkCount = 468;
        public const int HandLandmarkCount = 21;

        // World landmarks (meters, real-world 3D)
        public Landmark[] poseWorldLandmarks;
        public Landmark[] faceLandmarks;
        public Landmark[] leftHandWorldLandmarks;
        public Landmark[] rightHandWorldLandmarks;

        // Normalized landmarks (0-1 image coordinates + depth)
        public Landmark[] poseLandmarks;
        public Landmark[] leftHandLandmarks;
        public Landmark[] rightHandLandmarks;

        public long timestamp;

        public bool HasPoseWorld => poseWorldLandmarks != null && poseWorldLandmarks.Length == PoseLandmarkCount;
        public bool HasPoseNorm => poseLandmarks != null && poseLandmarks.Length == PoseLandmarkCount;
        public bool HasFace => faceLandmarks != null && faceLandmarks.Length == FaceLandmarkCount;
        public bool HasLeftHandWorld => leftHandWorldLandmarks != null && leftHandWorldLandmarks.Length == HandLandmarkCount;
        public bool HasRightHandWorld => rightHandWorldLandmarks != null && rightHandWorldLandmarks.Length == HandLandmarkCount;
        public bool HasLeftHandNorm => leftHandLandmarks != null && leftHandLandmarks.Length == HandLandmarkCount;
        public bool HasRightHandNorm => rightHandLandmarks != null && rightHandLandmarks.Length == HandLandmarkCount;

        /// <summary>Choose the best available pose data: world landmarks first, then normalized.</summary>
        public Landmark[] BestPoseLandmarks => HasPoseWorld ? poseWorldLandmarks : poseLandmarks;

        /// <summary>Choose the best available left-hand data.</summary>
        public Landmark[] BestLeftHand => HasLeftHandWorld ? leftHandWorldLandmarks : leftHandLandmarks;

        /// <summary>Choose the best available right-hand data.</summary>
        public Landmark[] BestRightHand => HasRightHandWorld ? rightHandWorldLandmarks : rightHandLandmarks;
    }

    /// <summary>MediaPipe Pose landmark indices (BlazePose topology).</summary>
    public static class PoseLandmark
    {
        public const int Nose = 0;
        public const int LeftEyeInner = 1;
        public const int LeftEye = 2;
        public const int LeftEyeOuter = 3;
        public const int RightEyeInner = 4;
        public const int RightEye = 5;
        public const int RightEyeOuter = 6;
        public const int LeftEar = 7;
        public const int RightEar = 8;
        public const int MouthLeft = 9;
        public const int MouthRight = 10;
        public const int LeftShoulder = 11;
        public const int RightShoulder = 12;
        public const int LeftElbow = 13;
        public const int RightElbow = 14;
        public const int LeftWrist = 15;
        public const int RightWrist = 16;
        public const int LeftPinky = 17;
        public const int RightPinky = 18;
        public const int LeftIndex = 19;
        public const int RightIndex = 20;
        public const int LeftThumb = 21;
        public const int RightThumb = 22;
        public const int LeftHip = 23;
        public const int RightHip = 24;
        public const int LeftKnee = 25;
        public const int RightKnee = 26;
        public const int LeftAnkle = 27;
        public const int RightAnkle = 28;
        public const int LeftHeel = 29;
        public const int RightHeel = 30;
        public const int LeftFootIndex = 31;
        public const int RightFootIndex = 32;
    }

    /// <summary>MediaPipe Hand landmark indices.</summary>
    public static class HandLandmark
    {
        public const int Wrist = 0;
        public const int ThumbCMC = 1;
        public const int ThumbMCP = 2;
        public const int ThumbIP = 3;
        public const int ThumbTip = 4;
        public const int IndexMCP = 5;
        public const int IndexPIP = 6;
        public const int IndexDIP = 7;
        public const int IndexTip = 8;
        public const int MiddleMCP = 9;
        public const int MiddlePIP = 10;
        public const int MiddleDIP = 11;
        public const int MiddleTip = 12;
        public const int RingMCP = 13;
        public const int RingPIP = 14;
        public const int RingDIP = 15;
        public const int RingTip = 16;
        public const int PinkyMCP = 17;
        public const int PinkyPIP = 18;
        public const int PinkyDIP = 19;
        public const int PinkyTip = 20;
    }
}
