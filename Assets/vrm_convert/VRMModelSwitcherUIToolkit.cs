using UnityEngine;
using UnityEngine.UIElements;
using System.Collections.Generic;

namespace CCMediaPipe
{
    /// <summary>
    /// Modern UI Toolkit-based overlay with two foldable screens: "Models" and "Scenes".
    /// Uses UXML for structure and USS for styling (CSS-like approach).
    /// Drag-and-drop support via WindowsFileDropHandler for .vrm, .glb/.gltf, and image files.
    /// </summary>
    [RequireComponent(typeof(VRMModelManager))]
    public class VRMModelSwitcherUIToolkit : MonoBehaviour
    {
        [Tooltip("Show the panel.")]
        public bool showPanel = true;

        private enum Tab { Models, Scenes }
        private Tab activeTab = Tab.Models;

        private VRMModelManager manager;
        private VRMSceneManager sceneManager;
        private MotionCaptureRecorder motionRecorder;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        private WindowsFileDropHandler dropHandler;
#endif

        private UIDocument uiDocument;
        private VisualElement rootContainer;
        private VisualElement modelsScreen;
        private VisualElement scenesScreen;
        private ScrollView modelsScroll;
        private ScrollView scenesScroll;
        private VisualElement modelsList;
        private VisualElement scenesList;
        private Label modelsCount;
        private Label scenesCount;
        private Button closeButton;
        private Button tabModels;
        private Button tabScenes;
        private Button loadModelButton;
        private Button loadSceneButton;
        private Button recordButton;
        private Button stopButton;
        private VisualElement recordingSection;

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

            SetupUI();
        }

        private void SetupUI()
        {
            // Create UIDocument component
            uiDocument = gameObject.AddComponent<UIDocument>();
            
            // Load UXML and USS from Resources
            var visualTree = Resources.Load<VisualTreeAsset>("UI/VRMModelSwitcherUI");
            var styleSheet = Resources.Load<StyleSheet>("UI/VRMModelSwitcherUI");
            
            if (visualTree == null)
            {
                Debug.LogError("Failed to load UXML from Resources/UI/VRMModelSwitcherUI.uxml");
                return;
            }

            // Instantiate the UI
            uiDocument.visualTreeAsset = visualTree;
            var root = uiDocument.rootVisualElement;
            
            if (styleSheet != null)
                root.styleSheets.Add(styleSheet);

            // Cache references to UI elements
            rootContainer = root.Q<VisualElement>("panel-container");
            modelsScreen = root.Q<VisualElement>("models-screen");
            scenesScreen = root.Q<VisualElement>("scenes-screen");
            modelsScroll = root.Q<ScrollView>("models-scroll");
            scenesScroll = root.Q<ScrollView>("scenes-scroll");
            modelsList = root.Q<VisualElement>("models-list");
            scenesList = root.Q<VisualElement>("scenes-list");
            modelsCount = root.Q<Label>("models-count");
            scenesCount = root.Q<Label>("scenes-count");
            recordingSection = root.Q<VisualElement>("recording-section");

            // Setup buttons
            closeButton = root.Q<Button>("close-button");
            tabModels = root.Q<Button>("tab-models");
            tabScenes = root.Q<Button>("tab-scenes");
            loadModelButton = root.Q<Button>("load-model-button");
            loadSceneButton = root.Q<Button>("load-scene-button");
            recordButton = root.Q<Button>("record-button");
            stopButton = root.Q<Button>("stop-button");

            // Register callbacks
            closeButton?.RegisterCallback<ClickEvent>(evt => showPanel = false);
            tabModels?.RegisterCallback<ClickEvent>(evt => SwitchToTab(Tab.Models));
            tabScenes?.RegisterCallback<ClickEvent>(evt => SwitchToTab(Tab.Scenes));
            loadModelButton?.RegisterCallback<ClickEvent>(evt => LoadModel());
            loadSceneButton?.RegisterCallback<ClickEvent>(evt => LoadScene());
            recordButton?.RegisterCallback<ClickEvent>(evt => StartRecording());
            stopButton?.RegisterCallback<ClickEvent>(evt => StopRecording());

            // Hide recording section if no recorder
            if (recordingSection != null && motionRecorder == null)
                recordingSection.style.display = DisplayStyle.None;

            // Subscribe to manager events
            if (manager != null)
                manager.OnModelsChanged += UpdateUI;
            if (sceneManager != null)
                sceneManager.OnScenesChanged += UpdateUI;

            UpdateUI();
        }

        private void OnDestroy()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (dropHandler != null)
                dropHandler.OnFilesDropped -= HandleFilesDropped;
#endif
            if (manager != null)
                manager.OnModelsChanged -= UpdateUI;
            if (sceneManager != null)
                sceneManager.OnScenesChanged -= UpdateUI;
        }

        private void Update()
        {
            if (rootContainer != null)
                rootContainer.style.display = showPanel ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void SwitchToTab(Tab tab)
        {
            activeTab = tab;
            
            if (modelsScreen != null && scenesScreen != null)
            {
                modelsScreen.style.display = tab == Tab.Models ? DisplayStyle.Flex : DisplayStyle.None;
                scenesScreen.style.display = tab == Tab.Scenes ? DisplayStyle.Flex : DisplayStyle.None;
            }

            if (tabModels != null && tabScenes != null)
            {
                if (tab == Tab.Models)
                {
                    tabModels.AddToClassList("tab-active");
                    tabScenes.RemoveFromClassList("tab-active");
                }
                else
                {
                    tabScenes.AddToClassList("tab-active");
                    tabModels.RemoveFromClassList("tab-active");
                }
            }

            UpdateUI();
        }

        private void UpdateUI()
        {
            if (manager == null) return;

            // Update models list
            if (modelsList != null && activeTab == Tab.Models)
            {
                modelsList.Clear();
                var models = manager.Models;
                
                for (int i = 0; i < models.Count; i++)
                {
                    var model = models[i];
                    var item = CreateListItem(
                        model.displayName,
                        manager.ActiveIndex == i,
                        () => manager.SwitchTo(i),
                        () => manager.Remove(i)
                    );
                    modelsList.Add(item);
                }

                if (modelsCount != null)
                    modelsCount.text = $"{models.Count} model{(models.Count != 1 ? "s" : "")}";
            }

            // Update scenes list
            if (scenesList != null && activeTab == Tab.Scenes && sceneManager != null)
            {
                scenesList.Clear();
                var scenes = sceneManager.Scenes;
                
                for (int i = 0; i < scenes.Count; i++)
                {
                    var scene = scenes[i];
                    var item = CreateListItem(
                        scene.displayName,
                        sceneManager.ActiveIndex == i,
                        () => sceneManager.SwitchTo(i),
                        () => sceneManager.Remove(i)
                    );
                    scenesList.Add(item);
                }

                if (scenesCount != null)
                    scenesCount.text = $"{scenes.Count} scene{(scenes.Count != 1 ? "s" : "")}";
            }
        }

        private VisualElement CreateListItem(string label, bool isActive, System.Action onSelect, System.Action onRemove)
        {
            var item = new VisualElement();
            item.AddToClassList("list-item");
            if (isActive)
                item.AddToClassList("list-item-active");

            var itemLabel = new Label(label);
            itemLabel.AddToClassList("item-label");
            item.Add(itemLabel);

            var removeBtn = new Button { text = "×" };
            removeBtn.AddToClassList("remove-button");
            removeBtn.RegisterCallback<ClickEvent>(evt => 
            {
                evt.StopPropagation();
                onRemove?.Invoke();
            });
            item.Add(removeBtn);

            item.RegisterCallback<ClickEvent>(evt => onSelect?.Invoke());

            return item;
        }

        private void LoadModel()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            string path = WindowsFileDialog.OpenVrm();
            if (!string.IsNullOrEmpty(path))
                manager.LoadAndSwitch(path);
#else
            Debug.LogWarning("File dialog not available on this platform");
#endif
        }

        private void LoadScene()
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            // Show a simple menu to choose between image and 3D scene
            // For now, we'll use OpenGltf as default, user can drag-drop images
            string path = WindowsFileDialog.OpenGltf();
            if (!string.IsNullOrEmpty(path) && sceneManager != null)
            {
                sceneManager.Add3DScene(path);
            }
#else
            Debug.LogWarning("File dialog not available on this platform");
#endif
        }

        private void StartRecording()
        {
            if (motionRecorder != null)
                motionRecorder.StartRecording();
        }

        private void StopRecording()
        {
            if (motionRecorder != null)
                motionRecorder.StopRecording();
        }

        private void HandleFilesDropped(string[] paths)
        {
            foreach (var path in paths)
            {
                var lower = path.ToLowerInvariant();
                
                if (lower.EndsWith(".vrm"))
                {
                    manager.LoadAndSwitch(path);
                    SwitchToTab(Tab.Models);
                }
                else if (lower.EndsWith(".glb") || lower.EndsWith(".gltf"))
                {
                    sceneManager?.Add3DScene(path);
                    SwitchToTab(Tab.Scenes);
                }
                else if (lower.EndsWith(".png") || lower.EndsWith(".jpg") || lower.EndsWith(".jpeg"))
                {
                    sceneManager?.AddImageScene(path);
                    SwitchToTab(Tab.Scenes);
                }
            }
        }
    }
}
