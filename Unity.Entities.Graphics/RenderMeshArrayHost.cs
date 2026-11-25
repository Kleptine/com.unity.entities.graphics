using System;
using UnityEngine;

namespace Unity.Rendering
{
    // Backing store for RenderMeshArray's variable-length mesh/material data.
    // Lives in editor memory after baking; serialized into the subscene's bundled assets
    // alongside the entities that reference it via UnityObjectRef<RenderMeshArrayHost>.
    //
    // Meshes and materials are held as direct UnityEngine.Object references, not UnityObjectRef:
    // Unity's serializer remaps Object reference fields across sessions and builds, whereas a
    // UnityObjectRef serialized as a plain ScriptableObject field persists a stale instance id.
    //
    // The hash is owned by the RenderMeshArray shared component (RenderMeshArray.Hash128).
    internal class RenderMeshArrayHost : ScriptableObject
    {
        [SerializeField] internal Material[]         Materials;
        [SerializeField] internal Mesh[]             Meshes;
        [SerializeField] internal MaterialMeshIndex[] MaterialMeshIndices;

        // Number of worlds that currently hold a RenderMeshArray referencing this host. Applied on
        // the main thread by RenderMeshArray.ProcessPendingHostReleases; the host is destroyed when
        // it reaches zero. Not serialized: a freshly loaded host starts at zero and is counted up as
        // worlds add the value.
        [NonSerialized] internal int RefCount;

        // True only for hosts we created in memory (baking / runtime construction), which we own and
        // must destroy ourselves. Hosts deserialized from a subscene or prefab are owned by the
        // content loading system and must never be destroyed here. Not serialized, so a host loaded
        // from content correctly defaults to false.
        [NonSerialized] internal bool CreatedInMemory;
    }
}
