using UnityEngine;

namespace CCMediaPipe
{
    /// <summary>
    /// Minimal 2D overlay with two foldable screens switched by tabs:
    /// "Models" (switch/load VRM models, see <see cref="VRMModelManager"/>) and
    /// "Scenes" (switch/load background environments, see
    /// <see cref="VRMSceneManager"/>). Uses IMGUI so it needs no Canvas/
    /// EventSystem setup: drop this component on any GameObject alongside a
    /// <see cref="VRMModelManager"/>; a <see cref="VRMSceneManager"/> is added
    /// automatically if missing.
    ///
    /// Drag-and-drop: a <see cref="WindowsFileDropHandler"/> (added automatically
    /// on Windows) feeds dropped files in by type. A .vrm becomes a model; a
    /// .glb/.gltf becomes a 3D scene; a .png/.jpg/.jpeg becomes an image scene.
    /// </summary>
    [RequireComponent(typeof(VRMModelManager))]
    public class VRMModelSwitcherUI : MonoBehaviour
    {
        [Tooltip("Show the panel.")]
        public bool showPanel = true;
        [Tooltip("Screen corner offset for the panel (pixels).")]
        public Vector2 panelMargin = new Vector2(12f, 12f);
        [Tooltip("Panel width in pixels.")]
        public float panelWidth = 264f;

        /// <summary>The two foldable screens this panel switches between.</summary>
        private enum Tab { Models, Scenes }
        private Tab activeTab = Tab.Models;

        private VRMModelManager manager;
        private VRMSceneManager sceneManager;
        private MotionCaptureRecorder motionRecorder;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private WindowsFileDropHandler dropHandler;
#endif
        private Vector2 scroll;
        private Vector2 sceneScroll;

        // --- Theme -----------------------------------------------------------
        private static readonly Color ColBackdrop = new Color(0.09f, 0.10f, 0.13f, 0.96f);
        private static readonly Color ColBorder = new Color(1f, 1f, 1f, 0.07f);
        private static readonly Color ColRow = new Color(1f, 1f, 1f, 0.04f);
        private static readonly Color ColRowHover = new Color(1f, 1f, 1f, 0.10f);
        private static readonly Color ColAccent = new Color(0.31f, 0.64f, 1f, 1f);
        private static readonly Color ColAccentDim = new Color(0.31f, 0.64f, 1f, 0.18f);
        private static readonly Color ColAccentHover = new Color(0.42f, 0.71f, 1f, 1f);
        private static readonly Color ColText = new Color(0.92f, 0.93f, 0.96f, 1f);
        private static readonly Color ColTextDim = new Color(0.60f, 0.63f, 0.70f, 1f);
        private static readonly Color ColDanger = new Color(1f, 0.45f, 0.45f, 1f);

        private bool stylesReady;
        private GUIStyle headerStyle;
        private GUIStyle countStyle;
        private GUIStyle hintStyle;
        private GUIStyle loadingStyle;
        private GUIStyle panelStyle;
        private GUIStyle rowStyle;
        private GUIStyle rowActiveStyle;
        private GUIStyle primaryButtonStyle;
        private GUIStyle secondaryButtonStyle;
        private GUIStyle removeButtonStyle;
        private GUIStyle tabStyle;
        private GUIStyle tabActiveStyle;

        // Generated textures (tracked so they can be released).
        private readonly System.Collections.Generic.List<Texture2D> ownedTextures =
            new System.Collections.Generic.List<Texture2D>();

        private void Awake()
        {
            manager = GetComponent<VRMModelManager>();
            motionRecorder = GetComponent<MotionCaptureRecorder>();
            if (motionRecorder == null)
                motionRecorder = FindObjectOfType<MotionCaptureRecorder>();
            if (motionRecorder == null)
            {
                var controller = FindObjectOfType<VRMHolisticController>();
                if (controller != null)
                {
                    motionRecorder = controller.GetComponent<MotionCaptureRecorder>();
                    if (motionRecorder == null)
                        motionRecorder = controller.gameObject.AddComponent<MotionCaptureRecorder>();
                    motionRecorder.controller = controller;
                }
            }

            sceneManager = GetComponent<VRMSceneManager>();
            if (sceneManager == null)
                sceneManager = gameObject.AddComponent<VRMSceneManager>();

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            dropHandler = GetComponent<WindowsFileDropHandler>();
            if (dropHandler == null)
                dropHandler = gameObject.AddComponent<WindowsFileDropHandler>();
            dropHandler.OnFilesDropped += HandleFilesDropped;
#endif
        }

        private void OnDestroy()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (dropHandler != null)
                dropHandler.OnFilesDropped -= HandleFilesDropped;
#endif
            for (int i = 0; i < ownedTextures.Count; i++)
            {
                if (ownedTextures[i] != null)
                    Destroy(ownedTextures[i]);
            }
            ownedTextures.Clear();
        }

        private void HandleFilesDropped(string[] paths)
        {
            if (paths == null)
                return;

            // Route by extension: .vrm builds a model, images become scenes.
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p))
                    continue;

                string ext = System.IO.Path.GetExtension(p).ToLowerInvariant();
                if (ext == ".vrm")
                {
                    manager?.LoadAndSwitch(p);
                }
                else if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
                {
                    if (sceneManager != null)
                    {
                        sceneManager.AddImageScene(p);
                        activeTab = Tab.Scenes; // jump to where the result shows
                    }
                }
                else if (ext == ".glb" || ext == ".gltf")
                {
                    if (sceneManager != null)
                    {
                        sceneManager.Add3DScene(p);
                        activeTab = Tab.Scenes;
                    }
                }
            }
        }

        private void Toggle()
        {
            showPanel = !showPanel;
        }

        private void Update()
        {
            // F1 toggles the panel so it can be hidden during capture.
            if (Input.GetKeyDown(KeyCode.F1))
                Toggle();
        }

        private void OnGUI()
        {
            if (!showPanel || manager == null)
                return;

            EnsureStyles();

            float x = panelMargin.x;
            float y = panelMargin.y;
            float h = Mathf.Min(Screen.height - panelMargin.y * 2f, 560f);

            GUILayout.BeginArea(new Rect(x, y, panelWidth, h), GUIContent.none, panelStyle);

            DrawHeader();
            DrawTabs();
            GUILayout.Space(8f);

            // Only one screen is drawn at a time; the tabs fold between them.
            if (activeTab == Tab.Models)
                DrawModelsScreen();
            else
                DrawScenesScreen();

            GUILayout.EndArea();
        }

        private void DrawHeader()
        {
            // Title reflects the active screen; count chip shows that list's size.
            int count = activeTab == Tab.Models
                ? manager.Models.Count
                : (sceneManager != null ? sceneManager.Scenes.Count : 0);

            GUILayout.BeginHorizontal();
            GUILayout.Label(activeTab == Tab.Models ? "Models" : "Scenes", headerStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label(count.ToString(), countStyle);
            GUILayout.EndHorizontal();

            GUILayout.Label("F1 to hide", hintStyle);
        }

        private void DrawTabs()
        {
            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();

            bool modelsSel = activeTab == Tab.Models;
            if (GUILayout.Button("Models", modelsSel ? tabActiveStyle : tabStyle, GUILayout.Height(26f)))
                activeTab = Tab.Models;

            GUILayout.Space(4f);

            bool scenesSel = activeTab == Tab.Scenes;
            if (GUILayout.Button("Scenes", scenesSel ? tabActiveStyle : tabStyle, GUILayout.Height(26f)))
                activeTab = Tab.Scenes;

            GUILayout.EndHorizontal();
        }

        private void DrawModelsScreen()
        {
            if (manager.IsLoading)
                GUILayout.Label("\u25CF  Loading model\u2026", loadingStyle);

            GUILayout.Space(8f);

            scroll = GUILayout.BeginScrollView(scroll, GUIStyle.none, GUIStyle.none);
            var models = manager.Models;
            for (int i = 0; i < models.Count; i++)
            {
                bool isActive = i == manager.ActiveIndex;

                GUILayout.BeginHorizontal(isActive ? rowActiveStyle : rowStyle);

                // Active marker keeps the label aligned whether selected or not.
                GUILayout.Label(isActive ? "\u25B6" : "", markerStyle, GUILayout.Width(14f));

                if (GUILayout.Button(models[i].displayName, rowLabelStyle))
                {
                    if (!isActive)
                        manager.SwitchTo(i);
                }

                // Allow removing non-active extras (keep at least one).
                if (models.Count > 1 && GUILayout.Button("\u00D7", removeButtonStyle, GUILayout.Width(22f)))
                {
                    manager.Remove(i);
                    GUILayout.EndHorizontal();
                    break;
                }

                GUILayout.EndHorizontal();
                GUILayout.Space(4f);
            }
            GUILayout.EndScrollView();

            GUILayout.FlexibleSpace();
            GUILayout.Space(8f);

            if (GUILayout.Button("Load VRM file\u2026", primaryButtonStyle, GUILayout.Height(34f)))
                OpenModelDialog();

            if (motionRecorder != null)
            {
                GUILayout.Space(8f);
                GUILayout.Label("Motion capture", hintStyle);
                GUILayout.BeginHorizontal();
                if (motionRecorder.IsRecording)
                {
                    if (GUILayout.Button("Stop & save", primaryButtonStyle, GUILayout.Height(28f)))
                        motionRecorder.StopRecordingAndSave();
                }
                else if (GUILayout.Button("Record", primaryButtonStyle, GUILayout.Height(28f)))
                {
                    motionRecorder.StartRecording();
                }

                if (motionRecorder.IsPlaying)
                {
                    if (GUILayout.Button("Stop", secondaryButtonStyle, GUILayout.Height(28f)))
                        motionRecorder.StopPlayback();
                }
                else if (GUILayout.Button("Load & play", secondaryButtonStyle, GUILayout.Height(28f)))
                {
                    OpenMotionDialog();
                }
                GUILayout.EndHorizontal();

                if (motionRecorder.IsPlaying)
                {
                    GUILayout.Label("Playing " + motionRecorder.PlaybackTime.ToString("0.0") + " / " +
                        motionRecorder.Duration.ToString("0.0") + " s", hintStyle);
                    motionRecorder.playbackSpeed = GUILayout.HorizontalSlider(
                        motionRecorder.playbackSpeed, 0.05f, 4f);
                    GUILayout.Label("Speed " + motionRecorder.playbackSpeed.ToString("0.00") + "x", hintStyle);
                }
                else if (!string.IsNullOrEmpty(motionRecorder.CurrentPath))
                {
                    GUILayout.Label("Saved: " + System.IO.Path.GetFileName(motionRecorder.CurrentPath), hintStyle);
                }
            }

            GUILayout.Space(4f);
            GUILayout.Label("Drag a .vrm onto the window to add it.", hintStyle);
        }

        private void DrawScenesScreen()
        {
            if (sceneManager == null)
            {
                GUILayout.Label("Scene manager unavailable.", hintStyle);
                return;
            }

            if (sceneManager.IsLoading)
                GUILayout.Label("\u25CF  Loading 3D scene\u2026", loadingStyle);

            GUILayout.Space(8f);

            sceneScroll = GUILayout.BeginScrollView(sceneScroll, GUIStyle.none, GUIStyle.none);
            var list = sceneManager.Scenes;
            for (int i = 0; i < list.Count; i++)
            {
                bool isActive = i == sceneManager.ActiveIndex;

                GUILayout.BeginHorizontal(isActive ? rowActiveStyle : rowStyle);

                GUILayout.Label(isActive ? "\u25B6" : "", markerStyle, GUILayout.Width(14f));

                // Tag 3D environment scenes so they're easy to tell from 2D ones.
                bool is3D = list[i].kind == VRMSceneManager.BackgroundKind.Model3D;
                string label = is3D ? "\u25C8 " + list[i].displayName : list[i].displayName;
                if (GUILayout.Button(label, rowLabelStyle))
                {
                    if (!isActive)
                        sceneManager.SwitchTo(i);
                }

                // Only custom scenes can be removed; built-in presets stay.
                if (!list[i].builtIn && GUILayout.Button("\u00D7", removeButtonStyle, GUILayout.Width(22f)))
                {
                    sceneManager.Remove(i);
                    GUILayout.EndHorizontal();
                    break;
                }

                GUILayout.EndHorizontal();
                GUILayout.Space(4f);
            }
            GUILayout.EndScrollView();

            GUILayout.FlexibleSpace();
            GUILayout.Space(8f);

            if (GUILayout.Button("Load 3D scene\u2026", primaryButtonStyle, GUILayout.Height(34f)))
                OpenScene3DDialog();

            GUILayout.Space(4f);

            if (GUILayout.Button("Load background image\u2026", secondaryButtonStyle, GUILayout.Height(28f)))
                OpenSceneDialog();

            GUILayout.Space(4f);
            GUILayout.Label("Drag a .glb/.gltf (3D) or .png/.jpg (image) onto the window.", hintStyle);
        }

        private void OpenModelDialog()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            string path = WindowsFileDialog.OpenVrm();
            if (!string.IsNullOrEmpty(path))
                manager.LoadAndSwitch(path);
#else
            Debug.Log("[VRMModelSwitcherUI] File dialog only implemented on Windows. " +
                "Use drag-and-drop or call manager.LoadAndSwitch(path).");
#endif
        }

        private void OpenSceneDialog()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            string path = WindowsFileDialog.OpenImage();
            if (!string.IsNullOrEmpty(path) && sceneManager != null)
                sceneManager.AddImageScene(path);
#else
            Debug.Log("[VRMModelSwitcherUI] File dialog only implemented on Windows. " +
                "Use drag-and-drop or call sceneManager.AddImageScene(path).");
#endif
        }

        private void OpenScene3DDialog()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            string path = WindowsFileDialog.OpenGltf();
            if (!string.IsNullOrEmpty(path) && sceneManager != null)
                sceneManager.Add3DScene(path);
#else
            Debug.Log("[VRMModelSwitcherUI] File dialog only implemented on Windows. " +
                "Use drag-and-drop or call sceneManager.Add3DScene(path).");
#endif
        }

        private void OpenMotionDialog()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            string path = WindowsFileDialog.OpenMotion();
            if (!string.IsNullOrEmpty(path) && motionRecorder != null)
                motionRecorder.LoadAndPlay(path);
#else
            Debug.Log("[VRMModelSwitcherUI] Motion file dialog only implemented on Windows.");
#endif
        }

        private GUIStyle markerStyle;
        private GUIStyle rowLabelStyle;

        private void EnsureStyles()
        {
            if (stylesReady)
                return;

            // --- Backgrounds (rounded 9-slice) -------------------------------
            Texture2D panelTex = RoundedRect(40, 14, ColBackdrop, ColBorder, 1);
            Texture2D rowTex = RoundedRect(24, 8, ColRow, Color.clear, 0);
            Texture2D rowHoverTex = RoundedRect(24, 8, ColRowHover, Color.clear, 0);
            Texture2D rowActiveTex = RoundedRect(24, 8, ColAccentDim, ColAccent, 1);
            Texture2D btnTex = RoundedRect(24, 9, ColAccent, Color.clear, 0);
            Texture2D btnHoverTex = RoundedRect(24, 9, ColAccentHover, Color.clear, 0);
            Texture2D removeTex = RoundedRect(20, 7, new Color(1f, 1f, 1f, 0.06f), Color.clear, 0);
            Texture2D removeHoverTex = RoundedRect(20, 7, new Color(1f, 0.45f, 0.45f, 0.22f), Color.clear, 0);
            Texture2D chipTex = RoundedRect(20, 9, new Color(1f, 1f, 1f, 0.08f), Color.clear, 0);

            panelStyle = new GUIStyle
            {
                border = new RectOffset(14, 14, 14, 14),
                padding = new RectOffset(16, 16, 14, 14),
            };
            panelStyle.normal.background = panelTex;

            headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                fontSize = 15,
                alignment = TextAnchor.MiddleLeft,
            };
            headerStyle.normal.textColor = ColText;

            countStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(8, 8, 2, 2),
                border = new RectOffset(9, 9, 9, 9),
            };
            countStyle.normal.textColor = ColText;
            countStyle.normal.background = chipTex;

            hintStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10,
                wordWrap = true,
            };
            hintStyle.normal.textColor = ColTextDim;

            loadingStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
            };
            loadingStyle.normal.textColor = ColAccent;

            rowStyle = new GUIStyle
            {
                border = new RectOffset(8, 8, 8, 8),
                padding = new RectOffset(8, 6, 4, 4),
                margin = new RectOffset(0, 0, 0, 0),
            };
            rowStyle.normal.background = rowTex;
            rowStyle.hover.background = rowHoverTex;

            rowActiveStyle = new GUIStyle(rowStyle);
            rowActiveStyle.normal.background = rowActiveTex;
            rowActiveStyle.hover.background = rowActiveTex;

            markerStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 10,
            };
            markerStyle.normal.textColor = ColAccent;

            rowLabelStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleLeft,
                fontSize = 12,
                clipping = TextClipping.Clip,
                stretchWidth = true,
                padding = new RectOffset(2, 2, 6, 6),
            };
            rowLabelStyle.normal.textColor = ColText;
            rowLabelStyle.hover.textColor = ColText;

            primaryButtonStyle = new GUIStyle
            {
                border = new RectOffset(9, 9, 9, 9),
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                fontStyle = FontStyle.Bold,
            };
            primaryButtonStyle.normal.background = btnTex;
            primaryButtonStyle.hover.background = btnHoverTex;
            primaryButtonStyle.active.background = btnTex;
            primaryButtonStyle.normal.textColor = new Color(0.04f, 0.08f, 0.15f, 1f);
            primaryButtonStyle.hover.textColor = new Color(0.04f, 0.08f, 0.15f, 1f);
            primaryButtonStyle.active.textColor = new Color(0.04f, 0.08f, 0.15f, 1f);

            // Secondary button: quieter fill for the less-prominent action.
            Texture2D btn2Tex = RoundedRect(24, 9, new Color(1f, 1f, 1f, 0.07f), Color.clear, 0);
            Texture2D btn2HoverTex = RoundedRect(24, 9, new Color(1f, 1f, 1f, 0.13f), Color.clear, 0);
            secondaryButtonStyle = new GUIStyle
            {
                border = new RectOffset(9, 9, 9, 9),
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11,
                fontStyle = FontStyle.Bold,
            };
            secondaryButtonStyle.normal.background = btn2Tex;
            secondaryButtonStyle.hover.background = btn2HoverTex;
            secondaryButtonStyle.active.background = btn2Tex;
            secondaryButtonStyle.normal.textColor = ColText;
            secondaryButtonStyle.hover.textColor = ColText;
            secondaryButtonStyle.active.textColor = ColText;

            removeButtonStyle = new GUIStyle
            {
                border = new RectOffset(7, 7, 7, 7),
                alignment = TextAnchor.MiddleCenter,
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                margin = new RectOffset(4, 0, 0, 0),
            };
            removeButtonStyle.normal.background = removeTex;
            removeButtonStyle.hover.background = removeHoverTex;
            removeButtonStyle.active.background = removeHoverTex;
            removeButtonStyle.normal.textColor = ColTextDim;
            removeButtonStyle.hover.textColor = ColDanger;
            removeButtonStyle.active.textColor = ColDanger;

            // --- Tabs (fold between the Models / Scenes screens) -------------
            Texture2D tabTex = RoundedRect(22, 8, new Color(1f, 1f, 1f, 0.05f), Color.clear, 0);
            Texture2D tabHoverTex = RoundedRect(22, 8, new Color(1f, 1f, 1f, 0.10f), Color.clear, 0);
            Texture2D tabActiveTex = RoundedRect(22, 8, ColAccentDim, ColAccent, 1);

            tabStyle = new GUIStyle
            {
                border = new RectOffset(8, 8, 8, 8),
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                stretchWidth = true,
            };
            tabStyle.normal.background = tabTex;
            tabStyle.hover.background = tabHoverTex;
            tabStyle.normal.textColor = ColTextDim;
            tabStyle.hover.textColor = ColText;

            tabActiveStyle = new GUIStyle(tabStyle);
            tabActiveStyle.normal.background = tabActiveTex;
            tabActiveStyle.hover.background = tabActiveTex;
            tabActiveStyle.normal.textColor = ColText;
            tabActiveStyle.hover.textColor = ColText;

            stylesReady = true;
        }

        /// <summary>
        /// Builds a rounded-rectangle texture suitable for a 9-slice GUIStyle
        /// background. <paramref name="radius"/> is the corner radius in pixels
        /// and should match the style's border so corners stay crisp at any size.
        /// </summary>
        private Texture2D RoundedRect(int size, int radius, Color fill, Color border, int borderWidth)
        {
            size = Mathf.Max(size, radius * 2 + 1);
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };

            var pixels = new Color[size * size];
            bool hasBorder = borderWidth > 0 && border.a > 0f;
            float r = radius;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Distance from the rounded edge: positive = inside.
                    float dx = Mathf.Min(x, size - 1 - x);
                    float dy = Mathf.Min(y, size - 1 - y);
                    float coverage;

                    if (dx >= r || dy >= r)
                    {
                        coverage = 1f; // Straight edge / interior.
                    }
                    else
                    {
                        // Inside a corner quadrant: anti-alias against the arc.
                        float cd = Mathf.Sqrt((r - dx) * (r - dx) + (r - dy) * (r - dy));
                        coverage = Mathf.Clamp01(r - cd + 0.5f);
                    }

                    Color c = fill;
                    if (hasBorder)
                    {
                        float edge = Mathf.Min(dx, dy);
                        float bt = Mathf.Clamp01((borderWidth + 0.5f) - edge);
                        c = Color.Lerp(fill, border, bt * border.a);
                        c.a = Mathf.Max(fill.a, border.a) * coverage;
                    }
                    else
                    {
                        c.a = fill.a * coverage;
                    }

                    pixels[y * size + x] = c;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            ownedTextures.Add(tex);
            return tex;
        }
    }
}
