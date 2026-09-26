using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UniGLTF;

namespace CCMediaPipe
{
    /// <summary>
    /// Loads a plain glTF/glb model at runtime using UniGLTF (already embedded
    /// for VRM support). Lives in the same assembly as <see cref="VrmRuntimeLoader"/>
    /// because UniGLTF's importer types are referenced here, not from
    /// Assembly-CSharp.
    ///
    /// Returned object: the loaded hierarchy's root <see cref="GameObject"/>. It
    /// owns its runtime resources (meshes/materials/textures) via the attached
    /// RuntimeGltfInstance, so destroying the GameObject frees them.
    ///
    /// MMD note: native MMD files (.pmx/.pmd) are not supported by Unity or
    /// UniGLTF at runtime. Convert an MMD stage to glTF/glb (e.g. via Blender's
    /// MMD Tools + glTF export) and load the resulting .glb here.
    /// </summary>
    public static class GltfSceneLoader
    {
        /// <summary>Extensions this loader can import.</summary>
        public static bool IsSupported(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".glb" || ext == ".gltf";
        }

        /// <summary>
        /// Load a glTF/glb from disk. Parsing runs on a background thread; Unity
        /// object creation is spread across frames so large stages do not freeze
        /// the app. Returns the root GameObject, or null on failure.
        /// </summary>
        public static async Task<GameObject> LoadPathAsync(string path, CancellationToken ct = default)
        {
            if (!IsSupported(path))
            {
                Debug.LogWarning($"[GltfSceneLoader] Unsupported file (need .glb/.gltf): {path}");
                return null;
            }
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[GltfSceneLoader] File not found: {path}");
                return null;
            }

            GltfData data = await Task.Run(() => new AutoGltfFileParser(path).Parse(), ct);

            try
            {
                using (var context = new ImporterContext(data))
                {
                    RuntimeGltfInstance instance = await context.LoadAsync(new RuntimeOnlyAwaitCaller());
                    instance.ShowMeshes();
                    instance.EnableUpdateWhenOffscreen();
                    return instance.Root;
                }
            }
            finally
            {
                data.Dispose();
            }
        }
    }
}
