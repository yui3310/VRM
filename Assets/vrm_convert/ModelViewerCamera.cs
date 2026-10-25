using UnityEngine;

namespace CCMediaPipe
{
    /// <summary>
    /// Simple viewer camera that stays locked facing a target model.
    ///
    /// Controls:
    ///   - Mouse scroll wheel: zoom in/out along the view (Z) axis.
    ///   - Right mouse button drag: pan the camera (and its focus point).
    ///
    /// The camera always looks at its focus point, so it keeps facing the model
    /// no matter how you zoom or pan. Attach this to the Camera GameObject and
    /// assign Target (the VRM model root). If Target is left empty it tries to
    /// follow the VRMHolisticController's model.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class ModelViewerCamera : MonoBehaviour
    {
        [Header("Target")]
        [Tooltip("Model to look at. If empty, the VRMHolisticController's model is used.")]
        public Transform target;
        [Tooltip("Height above the target pivot to aim at (meters). ~1.0-1.4 frames the upper body/face.")]
        public float targetHeight = 1.2f;

        [Header("Zoom (scroll wheel)")]
        [Tooltip("Current distance from the focus point along the view axis.")]
        public float distance = 2.5f;
        [Tooltip("Meters of zoom per scroll notch.")]
        public float zoomSpeed = 0.15f;
        public float minDistance = 0.3f;
        public float maxDistance = 10f;
        [Range(0f, 0.95f)]
        [Tooltip("Zoom smoothing (0 = instant).")]
        public float zoomSmoothing = 0.6f;

        [Header("Pan (right mouse drag)")]
        [Tooltip("Pan sensitivity. Scaled by distance so panning feels even at any zoom.")]
        public float panSpeed = 1.0f;
        [Tooltip("Invert horizontal pan direction.")]
        public bool invertPanX = false;
        [Tooltip("Invert vertical pan direction.")]
        public bool invertPanY = false;

        // Focus point the camera looks at. Starts at target + height and moves
        // with panning. Stored as an offset from the target so the framing
        // follows the model if it moves.
        private Vector3 panOffset = Vector3.zero;
        private float smoothedDistance;
        private Vector3 lastMousePos;
        private VRMHolisticController controller;
        // Locked view direction (from focus point toward the camera). Captured
        // once so zoom/pan keep the same facing onto the model.
        private Vector3 viewDir = Vector3.back;

        private void Start()
        {
            controller = FindObjectOfType<VRMHolisticController>();
            if (target == null)
            {
                if (controller != null && controller.vrmModel != null)
                    target = controller.vrmModel.transform;
            }

            smoothedDistance = distance;

            // Establish the locked facing from the camera's authored placement.
            // If the camera already sits away from the focus point, keep that
            // direction; otherwise default to viewing from -Z (in front of the
            // model looking toward +Z world).
            Vector3 focus = GetFocusPoint();
            Vector3 toCam = transform.position - focus;
            if (toCam.sqrMagnitude > 1e-4f)
            {
                viewDir = toCam.normalized;
                distance = Mathf.Clamp(toCam.magnitude, minDistance, maxDistance);
                smoothedDistance = distance;
            }
        }

        private void LateUpdate()
        {
            HandleZoom();
            HandlePan();
            UpdateCameraTransform();
        }

        private void HandleZoom()
        {
            if (IsPointerOverBlockingUI())
                return;

            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.0001f)
            {
                // Scrolling up zooms in (smaller distance).
                distance = Mathf.Clamp(distance - scroll * zoomSpeed * 10f, minDistance, maxDistance);
            }

            smoothedDistance = Mathf.Lerp(smoothedDistance, distance, 1f - zoomSmoothing);
        }

        private void HandlePan()
        {
            if (IsPointerOverBlockingUI())
                return;

            if (Input.GetMouseButtonDown(1))
            {
                lastMousePos = Input.mousePosition;
            }
            else if (Input.GetMouseButton(1))
            {
                Vector3 delta = Input.mousePosition - lastMousePos;
                lastMousePos = Input.mousePosition;

                // Scale by distance so panning covers a consistent fraction of
                // the screen regardless of zoom. /500 keeps a comfortable feel.
                float scale = panSpeed * smoothedDistance / 500f;
                float dx = (invertPanX ? delta.x : -delta.x) * scale;
                float dy = (invertPanY ? delta.y : -delta.y) * scale;

                // Move along the camera's own right/up axes so panning matches
                // what the user sees on screen.
                panOffset += transform.right * dx + transform.up * dy;
            }
        }

        private bool IsPointerOverBlockingUI()
        {
            if (controller == null)
                controller = FindObjectOfType<VRMHolisticController>();

            return controller != null && controller.IsPointerOverBoneMappingUI();
        }

        private void UpdateCameraTransform()
        {
            Vector3 focus = GetFocusPoint();

            // Locked facing: camera sits along viewDir from the focus point and
            // always looks back at it, so it keeps facing the model through zoom
            // and pan. Only the distance changes on scroll.
            transform.position = focus + viewDir * smoothedDistance;
            transform.rotation = Quaternion.LookRotation(-viewDir, Vector3.up);
        }

        private Vector3 GetFocusPoint()
        {
            Vector3 basePoint = target != null
                ? target.position + Vector3.up * targetHeight
                : Vector3.up * targetHeight;
            return basePoint + panOffset;
        }
    }
}
