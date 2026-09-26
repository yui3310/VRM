#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace CCMediaPipe
{
    /// <summary>
    /// Enables OS-level file drag-and-drop onto the Unity window on Windows.
    ///
    /// Unity has no built-in runtime file drop, so this subclasses the game
    /// window's WndProc and listens for WM_DROPFILES. Dropped file paths are
    /// queued on the message thread and dispatched on Unity's main thread via
    /// <see cref="OnFilesDropped"/>.
    ///
    /// Works in standalone Windows players and the Windows editor Game view.
    /// </summary>
    public class WindowsFileDropHandler : MonoBehaviour
    {
        /// <summary>Raised on the main thread with the dropped absolute file paths.</summary>
        public event Action<string[]> OnFilesDropped;

        private const int GWLP_WNDPROC = -4;
        private const uint WM_DROPFILES = 0x0233;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private IntPtr hWnd = IntPtr.Zero;
        private IntPtr oldWndProc = IntPtr.Zero;
        private WndProcDelegate newWndProc;  // kept alive to avoid GC of the thunk

        // Drops arrive on the window thread; hand them to the main thread.
        private readonly object queueLock = new object();
        private readonly List<string[]> pending = new List<string[]>();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetActiveWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, WndProcDelegate newProc);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr newProc);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr prevProc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("shell32.dll")]
        private static extern void DragAcceptFiles(IntPtr hWnd, bool fAccept);

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder lpszFile, uint cch);

        [DllImport("shell32.dll")]
        private static extern void DragFinish(IntPtr hDrop);

        private void OnEnable()
        {
            hWnd = GetActiveWindow();
            if (hWnd == IntPtr.Zero)
            {
                Debug.LogWarning("[WindowsFileDropHandler] No active window handle; drag-drop disabled.");
                return;
            }

            newWndProc = WndProc;
            oldWndProc = SetWindowLongPtr(hWnd, GWLP_WNDPROC, newWndProc);
            DragAcceptFiles(hWnd, true);
        }

        private void OnDisable()
        {
            if (hWnd != IntPtr.Zero && oldWndProc != IntPtr.Zero)
            {
                SetWindowLongPtr(hWnd, GWLP_WNDPROC, oldWndProc);
                DragAcceptFiles(hWnd, false);
            }
            hWnd = IntPtr.Zero;
            oldWndProc = IntPtr.Zero;
            newWndProc = null;
        }

        private IntPtr WndProc(IntPtr wnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_DROPFILES)
            {
                try
                {
                    IntPtr hDrop = wParam;
                    uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                    var files = new List<string>((int)count);
                    var sb = new StringBuilder(1024);
                    for (uint i = 0; i < count; i++)
                    {
                        sb.Length = 0;
                        uint len = DragQueryFile(hDrop, i, sb, (uint)sb.Capacity);
                        if (len > 0)
                            files.Add(sb.ToString());
                    }
                    DragFinish(hDrop);

                    if (files.Count > 0)
                    {
                        lock (queueLock)
                            pending.Add(files.ToArray());
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[WindowsFileDropHandler] WM_DROPFILES error: {e.Message}");
                }
                return IntPtr.Zero;
            }

            return CallWindowProc(oldWndProc, wnd, msg, wParam, lParam);
        }

        private void Update()
        {
            if (OnFilesDropped == null)
                return;

            string[][] batches = null;
            lock (queueLock)
            {
                if (pending.Count > 0)
                {
                    batches = pending.ToArray();
                    pending.Clear();
                }
            }

            if (batches == null)
                return;

            foreach (var batch in batches)
                OnFilesDropped(batch);
        }
    }
}
#endif
