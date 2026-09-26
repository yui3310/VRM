using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UniVRM10;

namespace CCMediaPipe
{
    /// <summary>
    /// Thin wrapper around <see cref="Vrm10.LoadPathAsync"/> that lives in its own
    /// assembly. That assembly explicitly references UniGLTF.Utils (which is not
    /// auto-referenced), so the IAwaitCaller type used by the VRM loader's
    /// optional parameters resolves here. The public surface only exposes
    /// Vrm10Instance / GameObject, so callers in Assembly-CSharp never need that
    /// reference themselves.
    /// </summary>
    public static class VrmRuntimeLoader
    {
        /// <summary>Load a VRM (or migrate VRM 0.x) from disk on the main thread.</summary>
        public static Task<Vrm10Instance> LoadPathAsync(string path, CancellationToken ct = default)
        {
            return Vrm10.LoadPathAsync(
                path,
                canLoadVrm0X: true,
                controlRigGenerationOption: ControlRigGenerationOption.Generate,
                showMeshes: true,
                ct: ct);
        }
    }
}
