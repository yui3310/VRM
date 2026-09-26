#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
using System;
using System.Runtime.InteropServices;

namespace CCMediaPipe
{
    /// <summary>
    /// Native Windows "Open File" dialog (comdlg32 GetOpenFileName) so the UI can
    /// browse for a .vrm without any extra package. Returns the selected path or
    /// an empty string if the user cancels.
    /// </summary>
    public static class WindowsFileDialog
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct OpenFileName
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public string lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int flagsEx;
        }

        private const int OFN_FILEMUSTEXIST = 0x00001000;
        private const int OFN_PATHMUSTEXIST = 0x00000800;
        private const int OFN_NOCHANGEDIR = 0x00000008;
        private const int OFN_EXPLORER = 0x00080000;

        [DllImport("comdlg32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GetOpenFileName(ref OpenFileName ofn);

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        /// <summary>Open a dialog filtered to .vrm files. Empty string if cancelled.</summary>
        public static string OpenVrm()
        {
            return Open(
                "Select a VRM model",
                "VRM files\0*.vrm\0All files\0*.*\0\0",
                "vrm");
        }

        /// <summary>Open a dialog filtered to image files. Empty string if cancelled.</summary>
        public static string OpenImage()
        {
            return Open(
                "Select a background image",
                "Image files\0*.png;*.jpg;*.jpeg\0All files\0*.*\0\0",
                "png");
        }

        /// <summary>Open a dialog filtered to glTF/glb 3D files. Empty string if cancelled.</summary>
        public static string OpenGltf()
        {
            return Open(
                "Select a 3D scene (glTF/glb)",
                "glTF files\0*.glb;*.gltf\0All files\0*.*\0\0",
                "glb");
        }

        public static string OpenMotion()
        {
            return Open(
                "Select a motion capture",
                "Motion capture files\0*.json\0All files\0*.*\0\0",
                "json");
        }

        /// <summary>Show a native open-file dialog with the given filter/title.</summary>
        private static string Open(string title, string filter, string defExt)
        {
            var ofn = new OpenFileName();
            ofn.lStructSize = Marshal.SizeOf(ofn);
            ofn.hwndOwner = GetActiveWindow();
            // Filter pairs are null-separated, terminated by a double null.
            ofn.lpstrFilter = filter;
            ofn.nFilterIndex = 1;
            ofn.lpstrFile = new string(new char[2048]);
            ofn.nMaxFile = 2048;
            ofn.lpstrFileTitle = new string(new char[260]);
            ofn.nMaxFileTitle = 260;
            ofn.lpstrTitle = title;
            ofn.lpstrDefExt = defExt;
            ofn.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR | OFN_EXPLORER;

            try
            {
                if (GetOpenFileName(ref ofn))
                    return ofn.lpstrFile.TrimEnd('\0');
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError($"[WindowsFileDialog] {e.Message}");
            }
            return string.Empty;
        }
    }
}
#endif
