#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CCMediaPipe
{
    /// <summary>
    /// 使用 Windows API 創建獨立的外部視窗來顯示 Unity Camera 畫面
    /// </summary>
    public class ExternalCameraWindow : MonoBehaviour
    {
        [Header("Camera Settings")]
        [Tooltip("要顯示的相機，留空則使用主相機")]
        public Camera targetCamera;
        
        [Header("Window Settings")]
        [Tooltip("外部視窗的寬度")]
        public int windowWidth = 1280;
        
        [Tooltip("外部視窗的高度")]
        public int windowHeight = 720;
        
        [Tooltip("視窗標題")]
        public string windowTitle = "VRM Camera View";
        
        [Tooltip("是否在啟動時自動開啟視窗")]
        public bool openOnStart = false;

        private IntPtr hwnd = IntPtr.Zero;
        private IntPtr hdc = IntPtr.Zero;
        private IntPtr memDC = IntPtr.Zero;
        private IntPtr hBitmap = IntPtr.Zero;
        private IntPtr oldBitmap = IntPtr.Zero;
        
        private RenderTexture renderTexture;
        private Texture2D readbackTexture;
        private bool isWindowOpen = false;
        private const string WINDOW_CLASS_NAME = "UnityExternalCameraWindow";

        #region Windows API Declarations

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private WndProcDelegate wndProcDelegate;

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

        private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint CS_HREDRAW = 0x0002;
        private const uint CS_VREDRAW = 0x0001;
        private const uint WM_DESTROY = 0x0002;
        private const uint WM_CLOSE = 0x0010;
        private const uint WM_PAINT = 0x000F;
        private const int CW_USEDEFAULT = unchecked((int)0x80000000);
        private const uint DIB_RGB_COLORS = 0;
        private const int SRCCOPY = 0x00CC0020;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool UpdateWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

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

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

        #endregion

        private void Start()
        {
            if (targetCamera == null)
                targetCamera = Camera.main;

            if (targetCamera == null)
            {
                Debug.LogError("[ExternalCameraWindow] No camera assigned or found!");
                return;
            }

            if (openOnStart)
                OpenWindow();
        }

        public void OpenWindow()
        {
            if (isWindowOpen)
            {
                Debug.LogWarning("[ExternalCameraWindow] Window already open!");
                return;
            }

            CreateRenderTexture();
            CreateExternalWindow();
            isWindowOpen = true;
        }

        public void CloseWindow()
        {
            if (!isWindowOpen) return;

            CleanupWindow();
            CleanupRenderTexture();
            isWindowOpen = false;
        }

        private void CreateRenderTexture()
        {
            renderTexture = new RenderTexture(windowWidth, windowHeight, 24);
            renderTexture.Create();
            targetCamera.targetTexture = renderTexture;

            readbackTexture = new Texture2D(windowWidth, windowHeight, TextureFormat.RGB24, false);
        }

        private void CreateExternalWindow()
        {
            IntPtr hInstance = GetModuleHandle(null);

            // 註冊視窗類別
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
                lpszClassName = WINDOW_CLASS_NAME,
                hIconSm = IntPtr.Zero
            };

            RegisterClassEx(ref wc);

            // 創建視窗
            hwnd = CreateWindowEx(
                0, WINDOW_CLASS_NAME, windowTitle,
                WS_OVERLAPPEDWINDOW | WS_VISIBLE,
                CW_USEDEFAULT, CW_USEDEFAULT,
                windowWidth, windowHeight,
                IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
            {
                Debug.LogError("[ExternalCameraWindow] Failed to create window!");
                return;
            }

            // 創建 GDI 設備上下文
            hdc = GetDC(hwnd);
            memDC = CreateCompatibleDC(hdc);

            // 創建 DIB Section
            BITMAPINFO bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                    biWidth = windowWidth,
                    biHeight = -windowHeight, // 負值表示 top-down bitmap
                    biPlanes = 1,
                    biBitCount = 24,
                    biCompression = 0,
                    biSizeImage = 0
                }
            };

            IntPtr pBits;
            hBitmap = CreateDIBSection(memDC, ref bmi, DIB_RGB_COLORS, out pBits, IntPtr.Zero, 0);
            oldBitmap = SelectObject(memDC, hBitmap);

            ShowWindow(hwnd, 5); // SW_SHOW
            UpdateWindow(hwnd);

            Debug.Log($"[ExternalCameraWindow] Window created: {hwnd}");
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_CLOSE:
                case WM_DESTROY:
                    CloseWindow();
                    return IntPtr.Zero;
                    
                case WM_PAINT:
                    // 在 WM_PAINT 時更新畫面
                    return DefWindowProc(hWnd, msg, wParam, lParam);
            }

            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void LateUpdate()
        {
            if (!isWindowOpen || hwnd == IntPtr.Zero) return;

            UpdateWindowContent();
        }

        private void UpdateWindowContent()
        {
            // 從 RenderTexture 讀取畫面
            RenderTexture.active = renderTexture;
            readbackTexture.ReadPixels(new Rect(0, 0, windowWidth, windowHeight), 0, 0);
            readbackTexture.Apply();
            RenderTexture.active = null;

            // 獲取像素數據
            Color32[] pixels = readbackTexture.GetPixels32();

            // 將像素數據複製到 DIB Section
            BITMAPINFO bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER)),
                    biWidth = windowWidth,
                    biHeight = -windowHeight,
                    biPlanes = 1,
                    biBitCount = 24,
                    biCompression = 0
                }
            };

            IntPtr pBits;
            IntPtr newBitmap = CreateDIBSection(memDC, ref bmi, DIB_RGB_COLORS, out pBits, IntPtr.Zero, 0);

            // 轉換並複製像素數據 (Unity 的 Color32 到 BGR 格式)
            byte[] bgrData = new byte[windowWidth * windowHeight * 3];
            for (int i = 0; i < pixels.Length; i++)
            {
                int idx = i * 3;
                bgrData[idx] = pixels[i].b;     // B
                bgrData[idx + 1] = pixels[i].g; // G
                bgrData[idx + 2] = pixels[i].r; // R
            }

            Marshal.Copy(bgrData, 0, pBits, bgrData.Length);

            // 更新視窗
            SelectObject(memDC, oldBitmap);
            DeleteObject(hBitmap);
            hBitmap = newBitmap;
            oldBitmap = SelectObject(memDC, hBitmap);

            BitBlt(hdc, 0, 0, windowWidth, windowHeight, memDC, 0, 0, SRCCOPY);
            InvalidateRect(hwnd, IntPtr.Zero, false);
        }

        private void CleanupWindow()
        {
            if (oldBitmap != IntPtr.Zero)
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

            if (hdc != IntPtr.Zero && hwnd != IntPtr.Zero)
            {
                ReleaseDC(hwnd, hdc);
                hdc = IntPtr.Zero;
            }

            if (hwnd != IntPtr.Zero)
            {
                DestroyWindow(hwnd);
                hwnd = IntPtr.Zero;
            }
        }

        private void CleanupRenderTexture()
        {
            if (targetCamera != null)
                targetCamera.targetTexture = null;

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
        }

        private void OnDestroy()
        {
            CloseWindow();
        }

        private void OnApplicationQuit()
        {
            CloseWindow();
        }
    }
}
#endif
