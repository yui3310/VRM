using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UniVRM10;

namespace CCMediaPipe
{
    /// <summary>
    /// Loads VRM files at runtime and switches the active model that the
    /// <see cref="VRMHolisticController"/> drives.
    ///
    /// Models are loaded lazily from disk (drag-and-drop or the UI) and cached so
    /// switching back to a previously loaded model is instant. Only the active
    /// model is shown; the others are hidden but kept alive.
    /// </summary>
    public class VRMModelManager : MonoBehaviour
    {
        [Tooltip("Controller that drives the active model. Auto-found if empty.")]
        public VRMHolisticController controller;

        [Tooltip("Where loaded models are parented / positioned.")]
        public Transform spawnPoint;

        /// <summary>A model that has been loaded into the scene.</summary>
        public class Entry
        {
            public string displayName;
            public string sourcePath;   // empty for the pre-placed scene model
            public GameObject root;
        }

        private readonly List<Entry> entries = new List<Entry>();
        private int activeIndex = -1;
        private bool isLoading;

        /// <summary>Raised whenever the model list or active selection changes.</summary>
        public event Action OnModelsChanged;

        public IReadOnlyList<Entry> Models => entries;
        public int ActiveIndex => activeIndex;
        public bool IsLoading => isLoading;

        private void Awake()
        {
            if (controller == null)
                controller = FindObjectOfType<VRMHolisticController>();
        }

        private void Start()
        {
            // Adopt a model already placed in the scene (assigned on the
            // controller) so it shows up in the switch list as the first entry.
            if (controller != null && controller.vrmModel != null)
            {
                var existing = new Entry
                {
                    displayName = controller.vrmModel.name,
                    sourcePath = string.Empty,
                    root = controller.vrmModel,
                };
                entries.Add(existing);
                activeIndex = 0;
                OnModelsChanged?.Invoke();
            }
        }

        /// <summary>
        /// Load a VRM (or VRM 0.x) file from disk and switch to it. If the same
        /// path was already loaded, just switch to the cached instance.
        /// </summary>
        public async void LoadAndSwitch(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".vrm")
            {
                Debug.LogWarning($"[VRMModelManager] Ignoring non-VRM file: {path}");
                return;
            }

            if (!File.Exists(path))
            {
                Debug.LogWarning($"[VRMModelManager] File not found: {path}");
                return;
            }

            // Already loaded? Just switch.
            int existingIndex = entries.FindIndex(e =>
                !string.IsNullOrEmpty(e.sourcePath) &&
                string.Equals(e.sourcePath, path, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                SwitchTo(existingIndex);
                return;
            }

            if (isLoading)
            {
                Debug.Log("[VRMModelManager] Already loading a model; please wait.");
                return;
            }

            isLoading = true;
            OnModelsChanged?.Invoke();
            try
            {
                Vrm10Instance instance = await VrmRuntimeLoader.LoadPathAsync(path);

                if (instance == null)
                {
                    Debug.LogError($"[VRMModelManager] Failed to load VRM: {path}");
                    return;
                }

                var root = instance.gameObject;
                if (spawnPoint != null)
                {
                    root.transform.SetPositionAndRotation(spawnPoint.position, spawnPoint.rotation);
                }

                var entry = new Entry
                {
                    displayName = Path.GetFileNameWithoutExtension(path),
                    sourcePath = path,
                    root = root,
                };
                entries.Add(entry);
                SwitchTo(entries.Count - 1);
            }
            catch (Exception e)
            {
                Debug.LogError($"[VRMModelManager] Error loading '{path}': {e.Message}");
            }
            finally
            {
                isLoading = false;
                OnModelsChanged?.Invoke();
            }
        }

        /// <summary>Switch the active model to the entry at <paramref name="index"/>.</summary>
        public void SwitchTo(int index)
        {
            if (index < 0 || index >= entries.Count)
                return;
            if (index == activeIndex)
                return;

            // Hide every model, show only the chosen one.
            for (int i = 0; i < entries.Count; i++)
            {
                var root = entries[i].root;
                if (root != null)
                    root.SetActive(i == index);
            }

            activeIndex = index;

            if (controller != null && entries[index].root != null)
                controller.BindModel(entries[index].root);

            OnModelsChanged?.Invoke();
        }

        /// <summary>Remove a loaded model (cannot remove the last remaining one).</summary>
        public void Remove(int index)
        {
            if (index < 0 || index >= entries.Count)
                return;
            if (entries.Count <= 1)
            {
                Debug.Log("[VRMModelManager] Cannot remove the only model.");
                return;
            }

            var entry = entries[index];
            entries.RemoveAt(index);

            if (entry.root != null)
                Destroy(entry.root);

            // Re-resolve the active selection.
            if (index == activeIndex)
            {
                activeIndex = -1;
                SwitchTo(Mathf.Clamp(index, 0, entries.Count - 1));
            }
            else
            {
                if (index < activeIndex)
                    activeIndex--;
                OnModelsChanged?.Invoke();
            }
        }
    }
}
