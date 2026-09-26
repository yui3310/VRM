using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace CCMediaPipe
{
    /// <summary>
    /// Manages selectable background "scenes" (environments) shown behind the
    /// avatar and lets the UI switch between them at runtime.
    ///
    /// A scene drives what sits behind/around the avatar. Solid-color scenes set
    /// the camera clear color directly (handy for green/blue screen capture).
    /// Image and gradient scenes are drawn on a fullscreen quad parented to the
    /// camera so they always fill the view behind the model. 3D scenes load an
    /// actual glTF/glb environment (e.g. an MMD stage exported to glb) into the
    /// world around the avatar.
    ///
    /// Built-in presets are created on startup so the feature works with no setup.
    /// Custom image and 3D scenes can be added from disk (UI button or drag-drop).
    /// </summary>
    public class VRMSceneManager : MonoBehaviour
    {
        [Tooltip("Camera whose background these scenes drive. Auto-found if empty.")]
        public Camera targetCamera;

        [Tooltip("Local distance in front of the camera where the background quad " +
            "sits. Must be larger than the camera's max zoom so it stays behind " +
            "the model.")]
        public float backgroundDistance = 50f;

        [Tooltip("Where loaded 3D scene models are parented / positioned.")]
        public Transform sceneSpawnPoint;

        /// <summary>How a scene paints the background.</summary>
        public enum BackgroundKind
        {
            /// <summary>Restore whatever the camera was authored with.</summary>
            Default,
            /// <summary>Flat color via the camera clear color.</summary>
            SolidColor,
            /// <summary>A texture (image or generated gradient) on a fullscreen quad.</summary>
            Texture,
            /// <summary>A 3D glTF/glb environment loaded into the world.</summary>
            Model3D,
        }

        /// <summary>A selectable background environment.</summary>
        public class Scene
        {
            public string displayName;
            public BackgroundKind kind = BackgroundKind.SolidColor;
            public Color color = Color.gray;
            public Texture2D texture;     // for Texture kind
            public bool ownsTexture;      // destroy the texture when removed
            public bool builtIn;          // built-ins cannot be removed
            public string sourcePath;     // for custom image scenes
            public GameObject instance;   // for Model3D kind (loaded hierarchy)
        }

        private readonly List<Scene> scenes = new List<Scene>();
        private int activeIndex = -1;
        private bool isLoading;

        /// <summary>Raised whenever the scene list or active selection changes.</summary>
        public event Action OnScenesChanged;

        public IReadOnlyList<Scene> Scenes => scenes;
        public int ActiveIndex => activeIndex;
        public bool IsLoading => isLoading;

        // Fullscreen background quad (child of the camera) for textured scenes.
        private GameObject bgQuad;
        private Transform bgQuadTransform;
        private MeshRenderer bgRenderer;
        private Material bgMaterial;
        private bool bgUnsupported;

        // Authored camera state, restored by the "Default" scene.
        private CameraClearFlags originalClearFlags;
        private Color originalBgColor;
        private bool capturedOriginal;

        private void Awake()
        {
            ResolveCamera();
            CaptureOriginal();
            BuildBuiltInScenes();
        }

        private void Start()
        {
            // Default scene is index 0; leave the authored look untouched on boot.
            if (activeIndex < 0 && scenes.Count > 0)
                SwitchTo(0);
        }

        private void LateUpdate()
        {
            // Keep the background quad filling the view as fov/aspect change.
            if (bgQuad != null && bgQuad.activeSelf)
                FitBackgroundQuad();
        }

        private void OnDestroy()
        {
            for (int i = 0; i < scenes.Count; i++)
            {
                if (scenes[i].ownsTexture && scenes[i].texture != null)
                    Destroy(scenes[i].texture);
                if (scenes[i].instance != null)
                    Destroy(scenes[i].instance);
            }
            scenes.Clear();

            if (bgMaterial != null)
                Destroy(bgMaterial);
            if (bgQuad != null)
                Destroy(bgQuad);
        }

        // --- Public API ------------------------------------------------------

        /// <summary>Switch the active background to the scene at <paramref name="index"/>.</summary>
        public void SwitchTo(int index)
        {
            if (index < 0 || index >= scenes.Count)
                return;

            activeIndex = index;
            Apply(scenes[index]);
            OnScenesChanged?.Invoke();
        }

        /// <summary>
        /// Add a background image from disk (.png/.jpg/.jpeg) and switch to it.
        /// Already-loaded paths are reused instead of reloaded.
        /// </summary>
        public void AddImageScene(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            if (!File.Exists(path))
            {
                Debug.LogWarning($"[VRMSceneManager] File not found: {path}");
                return;
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg")
            {
                Debug.LogWarning($"[VRMSceneManager] Ignoring non-image file: {path}");
                return;
            }

            int existing = scenes.FindIndex(s =>
                !string.IsNullOrEmpty(s.sourcePath) &&
                string.Equals(s.sourcePath, path, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                SwitchTo(existing);
                return;
            }

            Texture2D tex;
            try
            {
                byte[] data = File.ReadAllBytes(path);
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                };
                if (!tex.LoadImage(data))
                {
                    Destroy(tex);
                    Debug.LogError($"[VRMSceneManager] Could not decode image: {path}");
                    return;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[VRMSceneManager] Error loading '{path}': {e.Message}");
                return;
            }

            scenes.Add(new Scene
            {
                displayName = Path.GetFileNameWithoutExtension(path),
                kind = BackgroundKind.Texture,
                color = Color.black,
                texture = tex,
                ownsTexture = true,
                builtIn = false,
                sourcePath = path,
            });
            SwitchTo(scenes.Count - 1);
        }

        /// <summary>
        /// Load a 3D environment (.glb/.gltf) from disk and switch to it. The
        /// model is loaded once and cached; switching back is instant. An MMD
        /// stage works once exported to glb. Already-loaded paths are reused.
        /// </summary>
        public async void Add3DScene(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            if (!File.Exists(path))
            {
                Debug.LogWarning($"[VRMSceneManager] File not found: {path}");
                return;
            }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".glb" && ext != ".gltf")
            {
                Debug.LogWarning($"[VRMSceneManager] Ignoring non-glTF file: {path}");
                return;
            }

            int existing = scenes.FindIndex(s =>
                s.kind == BackgroundKind.Model3D &&
                !string.IsNullOrEmpty(s.sourcePath) &&
                string.Equals(s.sourcePath, path, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                SwitchTo(existing);
                return;
            }

            if (isLoading)
            {
                Debug.Log("[VRMSceneManager] Already loading a scene; please wait.");
                return;
            }

            isLoading = true;
            OnScenesChanged?.Invoke();
            try
            {
                GameObject root = await GltfSceneLoader.LoadPathAsync(path);
                if (root == null)
                {
                    Debug.LogError($"[VRMSceneManager] Failed to load 3D scene: {path}");
                    return;
                }

                if (sceneSpawnPoint != null)
                    root.transform.SetPositionAndRotation(sceneSpawnPoint.position, sceneSpawnPoint.rotation);

                // Start hidden; SwitchTo activates only the selected scene model.
                root.SetActive(false);

                scenes.Add(new Scene
                {
                    displayName = Path.GetFileNameWithoutExtension(path),
                    kind = BackgroundKind.Model3D,
                    color = originalBgColor,
                    builtIn = false,
                    sourcePath = path,
                    instance = root,
                });
                SwitchTo(scenes.Count - 1);
            }
            catch (Exception e)
            {
                Debug.LogError($"[VRMSceneManager] Error loading '{path}': {e.Message}");
            }
            finally
            {
                isLoading = false;
                OnScenesChanged?.Invoke();
            }
        }

        /// <summary>Remove a custom scene (built-in presets cannot be removed).</summary>
        public void Remove(int index)
        {
            if (index < 0 || index >= scenes.Count)
                return;

            var scene = scenes[index];
            if (scene.builtIn)
            {
                Debug.Log("[VRMSceneManager] Built-in scenes cannot be removed.");
                return;
            }

            scenes.RemoveAt(index);
            if (scene.ownsTexture && scene.texture != null)
                Destroy(scene.texture);
            if (scene.instance != null)
                Destroy(scene.instance);

            // Re-resolve the active selection.
            if (index == activeIndex)
            {
                activeIndex = -1;
                SwitchTo(Mathf.Clamp(index, 0, scenes.Count - 1));
            }
            else
            {
                if (index < activeIndex)
                    activeIndex--;
                OnScenesChanged?.Invoke();
            }
        }

        // --- Setup -----------------------------------------------------------

        private void ResolveCamera()
        {
            if (targetCamera != null)
                return;

            var viewer = FindObjectOfType<ModelViewerCamera>();
            if (viewer != null)
                targetCamera = viewer.GetComponent<Camera>();
            if (targetCamera == null)
                targetCamera = Camera.main;
            if (targetCamera == null)
                targetCamera = FindObjectOfType<Camera>();

            if (targetCamera == null)
                Debug.LogWarning("[VRMSceneManager] No camera found; scenes will have no effect.");
        }

        private void CaptureOriginal()
        {
            if (capturedOriginal || targetCamera == null)
                return;
            originalClearFlags = targetCamera.clearFlags;
            originalBgColor = targetCamera.backgroundColor;
            capturedOriginal = true;
        }

        private void BuildBuiltInScenes()
        {
            scenes.Add(new Scene { displayName = "Default", kind = BackgroundKind.Default, builtIn = true });
            scenes.Add(new Scene { displayName = "Studio", kind = BackgroundKind.SolidColor, color = new Color(0.18f, 0.19f, 0.22f), builtIn = true });
            scenes.Add(new Scene { displayName = "Charcoal", kind = BackgroundKind.SolidColor, color = new Color(0.07f, 0.07f, 0.08f), builtIn = true });
            scenes.Add(new Scene { displayName = "White", kind = BackgroundKind.SolidColor, color = new Color(0.96f, 0.96f, 0.97f), builtIn = true });
            scenes.Add(new Scene { displayName = "Green Screen", kind = BackgroundKind.SolidColor, color = new Color(0f, 0.71f, 0.30f), builtIn = true });
            scenes.Add(new Scene { displayName = "Blue Screen", kind = BackgroundKind.SolidColor, color = new Color(0f, 0.28f, 0.80f), builtIn = true });

            scenes.Add(new Scene
            {
                displayName = "Sky",
                kind = BackgroundKind.Texture,
                color = new Color(0.55f, 0.74f, 0.95f),
                texture = MakeVerticalGradient(new Color(0.33f, 0.60f, 0.92f), new Color(0.86f, 0.93f, 0.99f)),
                ownsTexture = true,
                builtIn = true,
            });
            scenes.Add(new Scene
            {
                displayName = "Sunset",
                kind = BackgroundKind.Texture,
                color = new Color(0.30f, 0.20f, 0.38f),
                texture = MakeVerticalGradient(new Color(0.16f, 0.16f, 0.42f), new Color(0.99f, 0.56f, 0.29f)),
                ownsTexture = true,
                builtIn = true,
            });
        }

        // --- Background application -----------------------------------------

        private void Apply(Scene scene)
        {
            if (targetCamera == null)
            {
                ResolveCamera();
                if (targetCamera == null)
                    return;
            }
            CaptureOriginal();

            // Only the active scene's 3D model (if any) stays visible.
            for (int i = 0; i < scenes.Count; i++)
            {
                if (scenes[i].instance != null)
                    scenes[i].instance.SetActive(scenes[i] == scene && scene.kind == BackgroundKind.Model3D);
            }

            switch (scene.kind)
            {
                case BackgroundKind.Default:
                    targetCamera.clearFlags = originalClearFlags;
                    targetCamera.backgroundColor = originalBgColor;
                    SetQuadActive(false);
                    break;

                case BackgroundKind.SolidColor:
                    targetCamera.clearFlags = CameraClearFlags.SolidColor;
                    targetCamera.backgroundColor = scene.color;
                    SetQuadActive(false);
                    break;

                case BackgroundKind.Texture:
                    targetCamera.clearFlags = CameraClearFlags.SolidColor;
                    targetCamera.backgroundColor = scene.color; // shows if the quad is unavailable
                    if (EnsureQuad() && scene.texture != null)
                    {
                        bgMaterial.mainTexture = scene.texture;
                        SetQuadActive(true);
                        FitBackgroundQuad();
                    }
                    else
                    {
                        SetQuadActive(false);
                    }
                    break;

                case BackgroundKind.Model3D:
                    // The 3D environment provides the surroundings; restore the
                    // authored camera clear so skybox/solid shows beyond it, and
                    // hide the 2D background quad.
                    targetCamera.clearFlags = originalClearFlags;
                    targetCamera.backgroundColor = originalBgColor;
                    SetQuadActive(false);
                    break;
            }
        }

        private void SetQuadActive(bool active)
        {
            if (bgQuad != null)
                bgQuad.SetActive(active);
        }

        /// <summary>Create the background quad lazily. Returns false if unsupported.</summary>
        private bool EnsureQuad()
        {
            if (bgQuad != null)
                return true;
            if (bgUnsupported || targetCamera == null)
                return false;

            Shader shader = Shader.Find("Unlit/Texture");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                Debug.LogWarning("[VRMSceneManager] No unlit shader available; " +
                    "image/gradient scenes fall back to a solid color. Add " +
                    "'Unlit/Texture' to Always Included Shaders for standalone builds.");
                bgUnsupported = true;
                return false;
            }

            bgMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

            bgQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            bgQuad.name = "SceneBackgroundQuad";
            bgQuad.hideFlags = HideFlags.HideAndDontSave;

            var collider = bgQuad.GetComponent<Collider>();
            if (collider != null)
                Destroy(collider);

            bgRenderer = bgQuad.GetComponent<MeshRenderer>();
            bgRenderer.sharedMaterial = bgMaterial;
            bgRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            bgRenderer.receiveShadows = false;
            bgRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            bgRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            bgQuadTransform = bgQuad.transform;
            bgQuadTransform.SetParent(targetCamera.transform, false);
            bgQuadTransform.localRotation = Quaternion.identity;

            // Make sure the camera can actually see out to the quad.
            if (targetCamera.farClipPlane < backgroundDistance + 10f)
                targetCamera.farClipPlane = backgroundDistance + 10f;

            return true;
        }

        /// <summary>Size/position the quad so it fills the camera view at its distance.</summary>
        private void FitBackgroundQuad()
        {
            if (bgQuadTransform == null || targetCamera == null)
                return;

            float d = backgroundDistance;
            float height;
            if (targetCamera.orthographic)
                height = targetCamera.orthographicSize * 2f;
            else
                height = 2f * d * Mathf.Tan(targetCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float width = height * targetCamera.aspect;

            // A touch of margin avoids hairline gaps at the screen edges.
            bgQuadTransform.localPosition = new Vector3(0f, 0f, d);
            bgQuadTransform.localScale = new Vector3(width * 1.02f, height * 1.02f, 1f);
        }

        private Texture2D MakeVerticalGradient(Color top, Color bottom)
        {
            const int h = 256;
            var tex = new Texture2D(1, h, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var pixels = new Color[h];
            for (int y = 0; y < h; y++)
            {
                // Row 0 is the bottom of the quad in Unity UV space.
                float t = y / (float)(h - 1);
                pixels[y] = Color.Lerp(bottom, top, t);
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}
