#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CCMediaPipe
{
    /// <summary>
    /// Displays the in-Unity webcam preview in a separate native Windows window.
    /// The image is read from the detector's current camera texture and the
    /// MediaPipe pose/hand/face landmarks are drawn over it with GDI.
    /// </summary>
    public class ExternalCameraPreviewWindow : MonoBehaviour
    {
        [Header("Source")]
        [Tooltip("Detector that owns the live webcam texture. Leave empty and " +
            "the component finds one on this GameObject or in the scene.")]
        public MediaPipeHolisticDetector detector;

        [Header("Window")]
        public int windowWidth = 640;
        public int windowHeight = 480;
        public string windowTitle = "Camera Preview";

        [Header("Overlay")]
        public bool showPose = true;
        public bool showHands = true;
        public bool showFace = true;

        public bool IsOpen { get; private set; }

        public event Action ClosedByUser;

        private IntPtr hwnd;
        private IntPtr hdc;
        private IntPtr memDC;
        private IntPtr hBitmap;
        private IntPtr oldBitmap;
        private IntPtr dibBits;

        private int bufferWidth;
        private int bufferHeight;
        private RenderTexture renderTexture;
        private Texture2D readbackTexture;
        private byte[] bgrData;
        private HolisticFrame overlayFrame;

        private static bool windowClassRegistered;
        private static IntPtr windowClassInstance;
        private static int openWindowCount;
        private const string WindowClassName = "CCMediaPipeExternalCameraPreviewWindow";

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private static WndProcDelegate wndProcDelegate;
        private static readonly Dictionary<IntPtr, ExternalCameraPreviewWindow> windowsByHwnd =
            new Dictionary<IntPtr, ExternalCameraPreviewWindow>();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public uint[] bmiColors;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private const uint WS_OVERLAPPED = 0x00000000;
        private const uint WS_CAPTION = 0x00C00000;
        private const uint WS_SYSMENU = 0x00080000;
        private const uint WS_MINIMIZEBOX = 0x00020000;
        private const uint WS_THICKFRAME = 0x00040000;
        private const uint WS_RESIZABLE_PREVIEW =
            WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_THICKFRAME;
        private const uint CS_HREDRAW = 0x0002;
        private const uint CS_VREDRAW = 0x0001;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_DESTROY = 0x0002;
        private const uint WM_ERASEBKGND = 0x0014;
        private const int CW_USEDEFAULT = unchecked((int)0x80000000);
        private const uint DIB_RGB_COLORS = 0;
        private const int SRCCOPY = 0x00CC0020;
        private const int PS_SOLID = 0;
        private const uint WS_EX_TOPMOST = 0x00000008;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool UpdateWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool AdjustWindowRect(ref RECT lpRect, uint dwStyle, bool bMenu);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(
            IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
            out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr ho);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(
            IntPtr hdcDest, int xDest, int yDest, int width, int height,
            IntPtr hdcSrc, int xSrc, int ySrc, int rop);

        [DllImport("gdi32.dll")]
        private static extern bool StretchBlt(
            IntPtr hdcDest, int xDest, int yDest, int widthDest, int heightDest,
            IntPtr hdcSrc, int xSrc, int ySrc, int widthSrc, int heightSrc, int rop);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreatePen(int fnPenStyle, int nWidth, int crColor);

        [DllImport("gdi32.dll")]
        private static extern bool MoveToEx(IntPtr hdc, int x, int y, IntPtr lpPoint);

        [DllImport("gdi32.dll")]
        private static extern bool LineTo(IntPtr hdc, int x, int y);

        [DllImport("gdi32.dll")]
        private static extern bool Ellipse(IntPtr hdc, int left, int top, int right, int bottom);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private void Start()
        {
            if (detector == null)
                detector = GetComponent<MediaPipeHolisticDetector>();
            if (detector == null)
                detector = FindObjectOfType<MediaPipeHolisticDetector>();
        }

        public void SetDetector(MediaPipeHolisticDetector value)
        {
            detector = value;
        }

        public void SetOverlay(HolisticFrame frame)
        {
            overlayFrame = frame;
        }

        public void Open()
        {
            if (IsOpen)
                return;

            CreateRenderTexture();
            if (!CreateExternalWindow())
            {
                CleanupRenderTexture();
                return;
            }

            IsOpen = true;
            openWindowCount++;
            windowsByHwnd[hwnd] = this;
            Debug.Log($"[ExternalCameraPreviewWindow] Window created: {hwnd}");
        }

        public void Close()
        {
            if (!IsOpen)
                return;

            IsOpen = false;
            IntPtr closingHwnd = hwnd;
            hwnd = IntPtr.Zero;

            FinishClose(closingHwnd, false);
        }

        private void CreateRenderTexture()
        {
            windowWidth = Mathf.Max(1, windowWidth);
            windowHeight = Mathf.Max(1, windowHeight);

            bufferWidth = windowWidth;
            bufferHeight = windowHeight;

            renderTexture = new RenderTexture(bufferWidth, bufferHeight, 24);
            renderTexture.Create();
            readbackTexture = new Texture2D(bufferWidth, bufferHeight, TextureFormat.RGB24, false);
            bgrData = new byte[bufferWidth * bufferHeight * 3];
        }

        private bool EnsureWindowClassRegistered(IntPtr hInstance)
        {
            if (windowClassRegistered)
                return true;

            wndProcDelegate = new WndProcDelegate(WndProc);

            WNDCLASSEX wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX)),
                style = CS_HREDRAW | CS_VREDRAW,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProcDelegate),
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = hInstance,
                hIcon = IntPtr.Zero,
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = WindowClassName,
                hIconSm = IntPtr.Zero,
            };

            ushort atom = RegisterClassEx(ref wc);

            // Unity can reload the managed domain while the native class from a
            // previous Play session is still registered. Re-registering in that
            // case would leave a stale WndProc thunk installed, so replace it.
            if (atom == 0 && Marshal.GetLastWin32Error() == 1410)
            {
                UnregisterClass(WindowClassName, hInstance);
                atom = RegisterClassEx(ref wc);
            }

            if (atom == 0)
                return false;

            windowClassRegistered = true;
            windowClassInstance = hInstance;
            return true;
        }

        private bool CreateExternalWindow()
        {
            IntPtr hInstance = GetModuleHandle(null);

            if (!EnsureWindowClassRegistered(hInstance))
            {
                Debug.LogError("[ExternalCameraPreviewWindow] Failed to register window class.");
                return false;
            }

            RECT clientRect = new RECT
            {
                Left = 0,
                Top = 0,
                Right = bufferWidth,
                Bottom = bufferHeight,
            };
            AdjustWindowRect(ref clientRect, WS_RESIZABLE_PREVIEW, false);
            int outerWidth = clientRect.Right - clientRect.Left;
            int outerHeight = clientRect.Bottom - clientRect.Top;

            hwnd = CreateWindowEx(
                WS_EX_TOPMOST, WindowClassName, windowTitle,
                WS_RESIZABLE_PREVIEW,
                CW_USEDEFAULT, CW_USEDEFAULT,
                outerWidth, outerHeight,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
            {
                Debug.LogError("[ExternalCameraPreviewWindow] Failed to create window.");
                return false;
            }

            hdc = GetDC(hwnd);
            memDC = CreateCompatibleDC(hdc);

            BITMAPINFO bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                    biWidth = bufferWidth,
                    biHeight = -bufferHeight,
                    biPlanes = 1,
                    biBitCount = 24,
                    biCompression = 0,
                    biSizeImage = 0,
                }
            };

            hBitmap = CreateDIBSection(memDC, ref bmi, DIB_RGB_COLORS, out dibBits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero)
            {
                Debug.LogError("[ExternalCameraPreviewWindow] Failed to create preview bitmap.");
                CleanupGdi(hwnd);
                DestroyWindow(hwnd);
                hwnd = IntPtr.Zero;
                return false;
            }

            oldBitmap = SelectObject(memDC, hBitmap);
            ShowWindow(hwnd, 5);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            UpdateWindow(hwnd);
            return true;
        }

        private void LateUpdate()
        {
            if (!IsOpen || hwnd == IntPtr.Zero || detector == null || renderTexture == null)
                return;

            UpdateWindowContent();
        }

        private void UpdateWindowContent()
        {
            Texture source = detector.CameraTexture;
            if (source == null)
                return;

            RenderTexture.active = renderTexture;
            Graphics.Blit(source, renderTexture);
            readbackTexture.ReadPixels(new Rect(0, 0, bufferWidth, bufferHeight), 0, 0);
            readbackTexture.Apply();
            RenderTexture.active = null;

            Color32[] pixels = readbackTexture.GetPixels32();
            CopyPixelsToDib(pixels, detector.CameraFlipHorizontally);
            Marshal.Copy(bgrData, 0, dibBits, bgrData.Length);

            if (overlayFrame != null)
                DrawOverlay();

            if (GetClientRect(hwnd, out RECT clientRect))
            {
                int clientWidth = clientRect.Right - clientRect.Left;
                int clientHeight = clientRect.Bottom - clientRect.Top;
                StretchBlt(hdc, 0, 0, clientWidth, clientHeight,
                    memDC, 0, 0, bufferWidth, bufferHeight, SRCCOPY);
            }
            else
            {
                BitBlt(hdc, 0, 0, bufferWidth, bufferHeight, memDC, 0, 0, SRCCOPY);
            }
            InvalidateRect(hwnd, IntPtr.Zero, false);
        }

        private void CopyPixelsToDib(Color32[] pixels, bool flipHorizontally)
        {
            for (int y = 0; y < bufferHeight; y++)
            {
                int sourceRowOffset = y * bufferWidth;
                int destinationRowOffset = (bufferHeight - 1 - y) * bufferWidth;
                for (int x = 0; x < bufferWidth; x++)
                {
                    Color32 pixel = pixels[sourceRowOffset + x];
                    int destX = flipHorizontally ? bufferWidth - 1 - x : x;
                    int destIndex = (destinationRowOffset + destX) * 3;
                    bgrData[destIndex] = pixel.b;
                    bgrData[destIndex + 1] = pixel.g;
                    bgrData[destIndex + 2] = pixel.r;
                }
            }
        }

        private void DrawOverlay()
        {
            if (showPose && overlayFrame.poseLandmarks != null &&
                overlayFrame.poseLandmarks.Length == HolisticFrame.PoseLandmarkCount)
            {
                DrawConnections(overlayFrame.poseLandmarks, PoseConnections, new Color(0.15f, 0.9f, 1f, 1f));
                DrawPoints(overlayFrame.poseLandmarks, new Color(0.15f, 0.9f, 1f, 1f));
            }

            if (showHands)
            {
                DrawHand(overlayFrame.leftHandLandmarks, new Color(0.35f, 1f, 0.35f, 1f));
                DrawHand(overlayFrame.rightHandLandmarks, new Color(1f, 0.35f, 0.25f, 1f));
            }

            if (showFace && overlayFrame.faceLandmarks != null &&
                overlayFrame.faceLandmarks.Length == HolisticFrame.FaceLandmarkCount)
            {
                DrawConnections(overlayFrame.faceLandmarks, FaceConnections, new Color(1f, 0.82f, 0.20f, 1f));
                DrawSelectedPoints(overlayFrame.faceLandmarks, FacePoints, new Color(1f, 0.82f, 0.20f, 1f));
            }
        }

        private void DrawHand(Landmark[] hand, Color color)
        {
            if (hand == null || hand.Length != HolisticFrame.HandLandmarkCount)
                return;

            DrawConnections(hand, HandConnections, color);
            DrawPoints(hand, color);
        }

        private void DrawConnections(Landmark[] landmarks, int[,] connections, Color color)
        {
            IntPtr pen = CreatePen(PS_SOLID, 2, ColorToWin32(color));
            IntPtr oldPen = SelectObject(memDC, pen);

            for (int i = 0; i < connections.GetLength(0); i++)
            {
                int a = connections[i, 0];
                int b = connections[i, 1];
                if (a < 0 || a >= landmarks.Length || b < 0 || b >= landmarks.Length)
                    continue;

                MoveToEx(memDC, PixelX(landmarks[a].x), PixelY(landmarks[a].y), IntPtr.Zero);
                LineTo(memDC, PixelX(landmarks[b].x), PixelY(landmarks[b].y));
            }

            SelectObject(memDC, oldPen);
            DeleteObject(pen);
        }

        private void DrawPoints(Landmark[] landmarks, Color color)
        {
            IntPtr pen = CreatePen(PS_SOLID, 1, ColorToWin32(color));
            IntPtr oldPen = SelectObject(memDC, pen);

            for (int i = 0; i < landmarks.Length; i++)
                DrawPoint(landmarks[i], 3);

            SelectObject(memDC, oldPen);
            DeleteObject(pen);
        }

        private void DrawSelectedPoints(Landmark[] landmarks, int[] indices, Color color)
        {
            IntPtr pen = CreatePen(PS_SOLID, 1, ColorToWin32(color));
            IntPtr oldPen = SelectObject(memDC, pen);

            for (int i = 0; i < indices.Length; i++)
            {
                int index = indices[i];
                if (index < 0 || index >= landmarks.Length)
                    continue;
                DrawPoint(landmarks[index], 3);
            }

            SelectObject(memDC, oldPen);
            DeleteObject(pen);
        }

        private void DrawPoint(Landmark landmark, int radius)
        {
            int x = PixelX(landmark.x);
            int y = PixelY(landmark.y);
            Ellipse(memDC, x - radius, y - radius, x + radius, y + radius);
        }

        private int PixelX(float normalizedX)
        {
            return Mathf.RoundToInt(Mathf.Clamp01(normalizedX) * (bufferWidth - 1));
        }

        private int PixelY(float normalizedY)
        {
            return Mathf.RoundToInt(Mathf.Clamp01(normalizedY) * (bufferHeight - 1));
        }

        private static int ColorToWin32(Color color)
        {
            byte r = (byte)Mathf.RoundToInt(Mathf.Clamp01(color.r) * 255f);
            byte g = (byte)Mathf.RoundToInt(Mathf.Clamp01(color.g) * 255f);
            byte b = (byte)Mathf.RoundToInt(Mathf.Clamp01(color.b) * 255f);
            return r | (g << 8) | (b << 16);
        }

        private void FinishClose(IntPtr closingHwnd, bool notifyClosedByUser)
        {
            overlayFrame = null;

            if (openWindowCount > 0)
                openWindowCount--;

            if (closingHwnd != IntPtr.Zero)
            {
                CleanupGdi(closingHwnd);
                CleanupRenderTexture();
                DestroyWindow(closingHwnd);
                windowsByHwnd.Remove(closingHwnd);
            }
            else
            {
                CleanupRenderTexture();
            }

            if (notifyClosedByUser)
                ClosedByUser?.Invoke();

            UnregisterWindowClassIfUnused();
        }

        private static void UnregisterWindowClassIfUnused()
        {
            if (!windowClassRegistered || openWindowCount > 0)
                return;

            IntPtr hInstance = windowClassInstance != IntPtr.Zero
                ? windowClassInstance
                : GetModuleHandle(null);
            UnregisterClass(WindowClassName, hInstance);
            windowClassRegistered = false;
            windowClassInstance = IntPtr.Zero;
        }

        private void CleanupGdi(IntPtr windowHandle)
        {
            if (oldBitmap != IntPtr.Zero && memDC != IntPtr.Zero)
            {
                SelectObject(memDC, oldBitmap);
                oldBitmap = IntPtr.Zero;
            }

            if (hBitmap != IntPtr.Zero)
            {
                DeleteObject(hBitmap);
                hBitmap = IntPtr.Zero;
            }

            if (memDC != IntPtr.Zero)
            {
                DeleteDC(memDC);
                memDC = IntPtr.Zero;
            }

            if (hdc != IntPtr.Zero && windowHandle != IntPtr.Zero)
            {
                ReleaseDC(windowHandle, hdc);
                hdc = IntPtr.Zero;
            }

            dibBits = IntPtr.Zero;
        }

        private void CleanupRenderTexture()
        {
            if (renderTexture != null)
            {
                renderTexture.Release();
                Destroy(renderTexture);
                renderTexture = null;
            }

            if (readbackTexture != null)
            {
                Destroy(readbackTexture);
                readbackTexture = null;
            }

            bgrData = null;
        }

        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_CLOSE:
                    if (windowsByHwnd.TryGetValue(hWnd, out ExternalCameraPreviewWindow window))
                    {
                        window.CloseFromWindowMessage(hWnd);
                    }
                    return IntPtr.Zero;
                case WM_DESTROY:
                    return IntPtr.Zero;
                case WM_ERASEBKGND:
                    return (IntPtr)1;
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void CloseFromWindowMessage(IntPtr closingHwnd)
        {
            if (!IsOpen)
                return;

            IsOpen = false;
            hwnd = IntPtr.Zero;
            FinishClose(closingHwnd, true);
        }

        private void OnDestroy()
        {
            Close();
            UnregisterWindowClassIfUnused();
        }

        private void OnApplicationQuit()
        {
            Close();
            UnregisterWindowClassIfUnused();
        }

        private static readonly int[,] PoseConnections =
        {
            { 11, 12 }, { 11, 13 }, { 13, 15 }, { 12, 14 }, { 14, 16 },
            { 11, 23 }, { 12, 24 }, { 23, 24 }, { 23, 25 }, { 25, 27 },
            { 24, 26 }, { 26, 28 }, { 27, 29 }, { 28, 30 }, { 29, 31 },
            { 30, 32 }, { 0, 11 }, { 0, 12 },
        };

        private static readonly int[,] HandConnections =
        {
            { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 4 },
            { 0, 5 }, { 5, 6 }, { 6, 7 }, { 7, 8 },
            { 0, 9 }, { 9, 10 }, { 10, 11 }, { 11, 12 },
            { 0, 13 }, { 13, 14 }, { 14, 15 }, { 15, 16 },
            { 0, 17 }, { 17, 18 }, { 18, 19 }, { 19, 20 },
            { 5, 9 }, { 9, 13 }, { 13, 17 },
        };

        private static readonly int[] FacePoints =
        {
            10, 152, 234, 454, 70, 107, 336, 300,
            33, 133, 362, 263, 61, 291, 13, 14,
            1, 168,
        };

        private static readonly int[,] FaceConnections =
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
            { 197, 195 }, { 195, 5 }, { 5, 4 }, { 4, 1 },
        };
    }
}
#endif
