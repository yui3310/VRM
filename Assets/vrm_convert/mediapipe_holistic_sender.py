"""
MediaPipe Holistic → UDP sender for Unity VRM pipeline.

Captures webcam frames, runs MediaPipe Holistic, and sends landmark
data as JSON over UDP to the Unity VRMHolisticController.

Dependencies:
    pip install mediapipe==0.10.14 opencv-python numpy

Usage:
    python mediapipe_holistic_sender.py [--port 9999] [--host 127.0.0.1] [--camera 0]

JSON packet schema (one per frame):
{
    "ts": 123456789,
    "pw": [[x,y,z,v], ...33],
    "p":  [[x,y,z,v], ...33],
    "f":  [[x,y,z,v], ...468],
    "lh": [[x,y,z,v], ...21],
    "rh": [[x,y,z,v], ...21]
}

Note: mediapipe 0.10.x removed *_world_landmarks for hands.
Hand tracking uses normalized (0-1) image-space coordinates.
"""

import argparse
import json
import socket
import time
from collections import deque

import cv2
import mediapipe as mp
import numpy as np


def landmarks_to_list(landmarks, count):
    if landmarks is None:
        return None
    result = []
    for lm in landmarks.landmark:
        result.append([lm.x, lm.y, lm.z, lm.visibility if hasattr(lm, 'visibility') else 1.0])
    return result[:count]


def world_landmarks_to_list(landmarks, count):
    if landmarks is None:
        return None
    result = []
    for lm in landmarks.landmark:
        result.append([lm.x, lm.y, lm.z, lm.visibility if hasattr(lm, 'visibility') else 1.0])
    return result[:count]


def open_camera(camera_id):
    """Open camera with CAP_DSHOW (Windows), fallback to default."""
    cap = cv2.VideoCapture(camera_id, cv2.CAP_DSHOW)
    if not cap.isOpened():
        cap = cv2.VideoCapture(camera_id)
    return cap


def main():
    parser = argparse.ArgumentParser(description="MediaPipe Holistic UDP sender")
    parser.add_argument("--port", type=int, default=9999)
    parser.add_argument("--host", type=str, default="127.0.0.1")
    parser.add_argument("--camera", type=int, default=0)
    parser.add_argument("--no-display", action="store_true", help="Hide preview window")
    parser.add_argument("--max-fps", type=int, default=30, help="Max send rate")
    parser.add_argument("--max-retries", type=int, default=10, help="Max camera frame retries before exit")
    args = parser.parse_args()

    # ---- Setup MediaPipe Holistic ----
    mp_holistic = mp.solutions.holistic
    holistic = mp_holistic.Holistic(
        static_image_mode=False,
        model_complexity=1,
        smooth_landmarks=True,
        refine_face_landmarks=True,
        min_detection_confidence=0.5,
        min_tracking_confidence=0.5,
    )

    # ---- Setup UDP socket ----
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    target = (args.host, args.port)

    # ---- Setup webcam ----
    cap = open_camera(args.camera)
    if not cap.isOpened():
        print(f"ERROR: Cannot open camera {args.camera}")
        return

    cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
    cap.set(cv2.CAP_PROP_FPS, args.max_fps)

    print(f"MediaPipe Holistic sender started.")
    print(f"  Camera: {args.camera}  |  "
          f"{int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))}x{int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))}")
    print(f"  UDP   : {args.host}:{args.port}")
    print(f"  FPS   : max {args.max_fps}")
    print("Press Ctrl+C to stop.")

    # ---- Warmup: discard first few frames ----
    for _ in range(10):
        cap.read()

    retry_count = 0
    max_retries = args.max_retries
    frame_interval = 1.0 / args.max_fps
    last_send = 0.0
    fps_smoother = deque(maxlen=30)

    try:
        while True:
            ret, frame = cap.read()
            if not ret:
                retry_count += 1
                print(f"Camera frame read failed, retrying ({retry_count}/{max_retries})...")
                cap.release()
                time.sleep(0.2)
                if retry_count > max_retries:
                    print(f"ERROR: Cannot read camera after {max_retries} retries. Exiting.")
                    break
                cap = open_camera(args.camera)
                if not cap.isOpened():
                    print("Cannot reopen camera, waiting...")
                    time.sleep(1.0)
                    continue
                cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
                cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
                cap.set(cv2.CAP_PROP_FPS, args.max_fps)
                retry_count = 0  # reset on successful reopen
                for _ in range(5):
                    cap.read()
                continue

            # BGR to RGB for MediaPipe
            rgb = cv2.cvtColor(frame, cv2.COLOR_BGR2RGB)
            rgb.flags.writeable = False
            results = holistic.process(rgb)
            rgb.flags.writeable = True

            now = time.time()

            # ---- Build JSON frame ----
            payload = {"ts": int(now * 1000)}

            if results.pose_world_landmarks:
                payload["pw"] = world_landmarks_to_list(results.pose_world_landmarks, 33)

            if results.pose_landmarks:
                payload["p"] = landmarks_to_list(results.pose_landmarks, 33)

            if results.face_landmarks:
                payload["f"] = landmarks_to_list(results.face_landmarks, 468)

            if results.left_hand_landmarks:
                payload["lh"] = landmarks_to_list(results.left_hand_landmarks, 21)

            if results.right_hand_landmarks:
                payload["rh"] = landmarks_to_list(results.right_hand_landmarks, 21)

            # ---- Send (throttled) ----
            if now - last_send >= frame_interval:
                data = json.dumps(payload, separators=(",", ":"))
                sock.sendto(data.encode("utf-8"), target)
                fps_smoother.append(1.0 / max(now - last_send, 0.001))
                last_send = now

            # ---- Preview (optional) ----
            if not args.no_display:
                annotated = frame.copy()
                if results.pose_landmarks:
                    mp.solutions.drawing_utils.draw_landmarks(
                        annotated, results.pose_landmarks,
                        mp_holistic.POSE_CONNECTIONS,
                        mp.solutions.drawing_styles.get_default_pose_landmarks_style(),
                    )
                if results.face_landmarks:
                    mp.solutions.drawing_utils.draw_landmarks(
                        annotated, results.face_landmarks,
                        mp_holistic.FACEMESH_TESSELATION,
                        landmark_drawing_spec=None,
                        connection_drawing_spec=mp.solutions.drawing_styles
                            .get_default_face_mesh_tesselation_style(),
                    )
                if results.left_hand_landmarks:
                    mp.solutions.drawing_utils.draw_landmarks(
                        annotated, results.left_hand_landmarks,
                        mp_holistic.HAND_CONNECTIONS,
                    )
                if results.right_hand_landmarks:
                    mp.solutions.drawing_utils.draw_landmarks(
                        annotated, results.right_hand_landmarks,
                        mp_holistic.HAND_CONNECTIONS,
                    )

                fps_str = f"FPS: {np.mean(fps_smoother):.1f}" if fps_smoother else ""
                cv2.putText(annotated, fps_str, (10, 30),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 0), 2)
                cv2.imshow("MediaPipe Holistic Sender", annotated)
                if cv2.waitKey(1) & 0xFF == 27:
                    break

    except KeyboardInterrupt:
        print("\nStopped by user.")
    finally:
        cap.release()
        cv2.destroyAllWindows()
        holistic.close()
        sock.close()
        print("Cleanup complete.")


if __name__ == "__main__":
    main()