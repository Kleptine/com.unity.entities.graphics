using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine.Assertions;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.Rendering
{
    /// <summary>
    /// A struct storing material and mesh array indices.
    /// </summary>
    [Serializable]
    public struct MaterialMeshIndex
    {
        public int MaterialIndex;
        public int MeshIndex;
        public int SubMeshIndex;
    }

    /// <summary>
    /// Represents which materials and meshes to use to render an entity.
    /// </summary>
    /// <remarks>
    /// This struct supports both a serializable static encoding in which case Material and Mesh are
    /// array indices to some array (typically a RenderMeshArray), and direct use of
    /// runtime BatchRendererGroup BatchMaterialID / BatchMeshID values.
    /// </remarks>
    public struct MaterialMeshInfo : IComponentData, IEnableableComponent
    {
        /// <summary>
        /// The material ID.
        /// </summary>
        /// <remarks>
        /// The material ID can be one of the following:
        ///
        /// * A literal Material ID received from the RegisterMaterial API, encoded as a positive integer.
        /// * An array index to the RenderMeshArray shared component of the entity, encoded as a negative integer.
        ///
        /// Use the literal Material ID to change the material at runtime.
        /// Use the array index to store the material ID to disk during entity baking.
        /// </remarks>
        public int Material;

        /// <summary>
        /// The mesh ID.
        /// </summary>
        public int Mesh;

        /// <summary>
        /// The bit packed sub-mesh related data.
        /// </summary>
        SubMeshIndexInfo32 m_SubMeshIndexInfo;

        /// <summary>
        /// The sub-mesh ID.
        /// </summary>
        public ushort SubMesh
        {
            get => m_SubMeshIndexInfo.SubMesh;
            set => m_SubMeshIndexInfo.SubMesh = value;
        }

        /// <summary>
        /// The MaterialMeshIndex range.
        /// </summary>
        public RangeInt MaterialMeshIndexRange => m_SubMeshIndexInfo.MaterialMeshIndexRangeAsInt;

        /// <summary>
        /// True if the MaterialMeshInfo is using a MaterialMeshIndex range.
        /// </summary>
        public bool HasMaterialMeshIndexRange => m_SubMeshIndexInfo.HasMaterialMeshIndexRange;

        /// <summary>
        /// The sub-mesh ID.
        /// </summary>
        [Obsolete("Use SubMesh instead.", true)]
        public sbyte Submesh { get => (sbyte)SubMesh; set => SubMesh = (ushort)value; }

        /// <summary>
        /// Converts the given array index (typically the index inside RenderMeshArray) into
        /// a negative number that denotes that array position.
        /// </summary>
        /// <param name="index">The index to convert.</param>
        /// <returns>Returns the converted index.</returns>
        public static int ArrayIndexToStaticIndex(int index) => (index < 0)
            ? index
            : (-index - 1);

        /// <summary>
        /// Converts the given static index (a negative value) to a valid array index.
        /// </summary>
        /// <param name="staticIndex">The index to convert.</param>
        /// <returns>Returns the converted index.</returns>
        public static int StaticIndexToArrayIndex(int staticIndex) => math.abs(staticIndex) - 1;

        /// <summary>
        /// Creates an instance of MaterialMeshInfo from material and mesh/sub-mesh indices in the corresponding RenderMeshArray.
        /// </summary>
        /// <param name="materialIndexInRenderMeshArray">The material index in <see cref="RenderMeshArray.Materials"/>.</param>
        /// <param name="meshIndexInRenderMeshArray">The mesh index in <see cref="RenderMeshArray.Meshes"/>.</param>
        /// <param name="submeshIndex">An optional submesh ID.</param>
        /// <returns>Returns the MaterialMeshInfo instance that contains the material and mesh indices.</returns>
        public static MaterialMeshInfo FromRenderMeshArrayIndices(int materialIndexInRenderMeshArray,
            int meshIndexInRenderMeshArray,
            ushort submeshIndex = 0) =>
            new(
                ArrayIndexToStaticIndex(materialIndexInRenderMeshArray),
                ArrayIndexToStaticIndex(meshIndexInRenderMeshArray),
                new SubMeshIndexInfo32(submeshIndex)
            );

        /// <summary>
        /// Creates an instance of MaterialMeshInfo from a range of material/mesh/submesh index in the corresponding RenderMeshArray.
        /// </summary>
        /// <param name="rangeStart">The first index of the range in <see cref="RenderMeshArray.MaterialMeshIndices"/>.</param>
        /// <param name="rangeLength">The length of the range in <see cref="RenderMeshArray.MaterialMeshIndices"/>.</param>
        /// <returns></returns>
        public static MaterialMeshInfo FromMaterialMeshIndexRange(int rangeStart, int rangeLength)
            => new(0, 0, new SubMeshIndexInfo32((ushort)rangeStart, (byte)rangeLength));

        MaterialMeshInfo(int material, int mesh, SubMeshIndexInfo32 subMeshIndexInfo)
        {
            Material = material;
            Mesh = mesh;
            m_SubMeshIndexInfo = subMeshIndexInfo;
        }

        /// <summary>
        /// Creates an instance of MaterialMeshInfo from material and mesh/sub-mesh IDs registered with <see cref="EntitiesGraphicsSystem"/>
        /// </summary>
        /// <param name="materialID">The material ID from <see cref="EntitiesGraphicsSystem.RegisterMaterial"/>.</param>
        /// <param name="meshID">The mesh ID from <see cref="EntitiesGraphicsSystem.RegisterMesh"/>.</param>
        /// <param name="submeshIndex">An optional submesh ID.</param>
        public MaterialMeshInfo(BatchMaterialID materialID, BatchMeshID meshID, ushort submeshIndex = 0)
            : this((int)materialID.value, (int)meshID.value, new SubMeshIndexInfo32(submeshIndex)) {}

        /// <summary>
        /// The mesh ID property.
        /// </summary>
        public BatchMeshID MeshID
        {
            get
            {
                Assert.IsTrue(IsRuntimeMesh);
                return new BatchMeshID { value = (uint)Mesh };
            }

            set => Mesh = (int) value.value;
        }

        /// <summary>
        /// The material ID property.
        /// </summary>
        public BatchMaterialID MaterialID
        {
            get
            {
                Assert.IsTrue(IsRuntimeMaterial);
                return new BatchMaterialID() { value = (uint)Material };
            }

            set => Material = (int) value.value;
        }

        internal bool IsRuntimeMaterial => !HasMaterialMeshIndexRange && Material >= 0;

        internal bool IsRuntimeMesh => !HasMaterialMeshIndexRange && Mesh >= 0;

        internal int MeshArrayIndex
        {
            get => IsRuntimeMesh ? -1 : StaticIndexToArrayIndex(Mesh);
            set => Mesh = ArrayIndexToStaticIndex(value);
        }

        internal int MaterialArrayIndex
        {
            get => IsRuntimeMaterial ? -1 : StaticIndexToArrayIndex(Material);
            set => Material = ArrayIndexToStaticIndex(value);
        }
    }

    internal struct AssetHash
    {
        public static void UpdateAsset(ref xxHash3.StreamingState hash, EntityId asset)
        {
            // In the editor we can compute a stable serializable hash using an asset GUID
#if UNITY_EDITOR
            bool success = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long localId);
            hash.Update(success);
            if (!success)
            {
                hash.Update(asset);
                return;
            }
            var guidBytes = Encoding.UTF8.GetBytes(guid);

            hash.Update(guidBytes.Length);
            for (int j = 0; j < guidBytes.Length; ++j)
                hash.Update(guidBytes[j]);
            hash.Update(localId);
#else
            // In standalone, we have to resort to using the instance ID which is not serializable,
            // but should be usable in the context of this execution.
            hash.Update(asset);
#endif
        }
    }

    // Create unmanaged to managed to method allocation
    internal static unsafe class CallFromBurstRenderMeshArrayHelper
    {
        struct Functions
        {
            public IntPtr AddTo;
        }
        internal delegate void AddToDelegate(
            EntityManager* em, ArchetypeChunk* chunks, int chunksLength,
            UnityObjectRef<Material>* materialsPtr, int materialsLength,
            UnityObjectRef<Mesh>* meshesPtr, int meshesLength,
            MaterialMeshIndex* materialMeshIndicesPtr, int materialMeshIndicesLength,
            SceneSection* sceneSection);
        static AddToDelegate s_AddToGCDefeat;
        static readonly SharedStatic<Functions> k_Functions = SharedStatic<Functions>.GetOrCreate<Functions>();

#if UNITY_EDITOR
        [ExcludeFromBurstCompatTesting("References managed engine API")]
        [UnityEditor.InitializeOnLoadMethod]
        public static void EditorInitializeOnLoadMethod() => Init();
#else
        [ExcludeFromBurstCompatTesting("References managed engine API")]
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void RuntimeInitialization() => Init();
#endif

        public static void Init()
        {
            s_AddToGCDefeat = RenderMeshArray.AddTo;
            k_Functions.Data.AddTo = Marshal.GetFunctionPointerForDelegate(s_AddToGCDefeat);
        }

        public static void AddRenderMeshArrayTo(
            EntityManager em, NativeArray<ArchetypeChunk> chunks,
            NativeArray<UnityObjectRef<Material>> materials,
            NativeArray<UnityObjectRef<Mesh>> meshes,
            NativeArray<MaterialMeshIndex> materialMeshIndices,
            SceneSection sceneSection)
        {
            ((delegate* unmanaged[Cdecl] <
                EntityManager*,
                ArchetypeChunk*, int,
                UnityObjectRef<Material>*, int,
                UnityObjectRef<Mesh>*, int,
                MaterialMeshIndex*, int,
                SceneSection*,
                void>)k_Functions.Data.AddTo)(
                    &em,
                    (ArchetypeChunk*)chunks.GetUnsafeReadOnlyPtr(), chunks.Length,
                    (UnityObjectRef<Material>*)materials.GetUnsafeReadOnlyPtr(), materials.Length,
                    (UnityObjectRef<Mesh>*)meshes.GetUnsafeReadOnlyPtr(), meshes.Length,
                    (MaterialMeshIndex*)materialMeshIndices.GetUnsafeReadOnlyPtr(), materialMeshIndices.Length,
                    &sceneSection);
        }
    }

    /// <summary>
    /// A shared component that contains meshes and materials.
    /// </summary>
    public struct RenderMeshArray : ISharedComponentData, IEquatable<RenderMeshArray>, IRefCounted
    {
        // Variable-length data lives on an internal ScriptableObject host so that
        // RenderMeshArray itself is fully unmanaged (UnityObjectRef + uint4 = blittable).
        // The hot path never reads the host — it goes through BRGRenderMeshArrays,
        // a fully unmanaged side-table keyed by shared-component index.
        [SerializeField] internal UnityObjectRef<RenderMeshArrayHost> m_Host;

        // Memoize the expensive 128-bit hash. Owned by the shared component; the host doesn't duplicate it.
        [SerializeField] private uint4 m_Hash128;

        /// <summary>
        /// Constructs an instance of RenderMeshArray from an array of materials and an array of meshes.
        /// </summary>
        /// <param name="materials">The array of materials to use in the RenderMeshArray.</param>
        /// <param name="meshes">The array of meshes to use in the RenderMeshArray.</param>
        /// <param name="materialMeshIndices">The array of MaterialMeshIndex to use in the RenderMeshArray.</param>
        public RenderMeshArray(Material[] materials, Mesh[] meshes, MaterialMeshIndex[] materialMeshIndices = null)
        {
            if (meshes == null || materials == null)
                throw new ArgumentException("RenderMeshArray meshes and materials must be non-null.");

            var host = ScriptableObject.CreateInstance<RenderMeshArrayHost>();
            host.CreatedInMemory = true;
            host.Meshes = (Mesh[])meshes.Clone();
            host.Materials = (Material[])materials.Clone();
            host.MaterialMeshIndices = materialMeshIndices?.ToArray();

            m_Host = host;
            m_Hash128 = ComputeHash128(host);
        }

        /// <summary>
        /// Constructs an instance of RenderMeshArray from an array of materials and an array of meshes
        /// and sets it on the provided chunk.
        /// </summary>
        /// <param name="em">The EntityManager which will add the component onto the chunk</param>
        /// <param name="chunksPtr">A pointer to the array of chunks to add the RenderMeshArray onto</param>
        /// <param name="chunksLength">The length of the chunk array</param>
        /// <param name="materialsPtr">A pointer to the array of materials to use in the RenderMeshArray</param>
        /// <param name="materialsLength">The length of the material array</param>
        /// <param name="meshesPtr">A pointer to the array of meshes to use in the RenderMeshArray.</param>
        /// <param name="meshesLength">The length of the mesh array</param>
        /// <param name="materialMeshIndicesPtr">A pointer to the array of MaterialMeshIndex to use in the RenderMeshArray.</param>
        /// <param name="materialMeshIndicesLength">The length of the mesh material indices array</param>
        [MonoPInvokeCallback(typeof(CallFromBurstRenderMeshArrayHelper.AddToDelegate))]
        internal static unsafe void AddTo(EntityManager* em,
            ArchetypeChunk* chunksPtr, int chunksLength,
            UnityObjectRef<Material>* materialsPtr, int materialsLength,
            UnityObjectRef<Mesh>* meshesPtr, int meshesLength,
            MaterialMeshIndex* materialMeshIndicesPtr, int materialMeshIndicesLength,
            SceneSection* sceneSection)
        {
            // Each section loads its own host, so arrays from different sections must not be equal
            var newArray = new RenderMeshArray(
                new ReadOnlySpan<UnityObjectRef<Material>>(materialsPtr, materialsLength),
                new ReadOnlySpan<UnityObjectRef<Mesh>>(meshesPtr, meshesLength),
                new ReadOnlySpan<MaterialMeshIndex>(materialMeshIndicesPtr, materialMeshIndicesLength),
                *sceneSection);

            // AddSharedComponent skips chunks that already have the component, so set those explicitly first.
            var renderMeshArrayTypeHandle = (*em).GetSharedComponentTypeHandle<RenderMeshArray>();
            for (int i = 0; i < chunksLength; i++)
            {
                if (chunksPtr[i].Has(renderMeshArrayTypeHandle))
                    (*em).SetSharedComponent(chunksPtr[i], newArray);
            }

            var chunkArray = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<ArchetypeChunk>(chunksPtr, chunksLength, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref chunkArray, AtomicSafetyHandle.GetTempMemoryHandle());
#endif
            (*em).AddSharedComponent(chunkArray, newArray);
        }

        /// <summary>
        /// Constructs an instance of RenderMeshArray from an array of materials and an array of meshes.
        /// </summary>
        /// <param name="materials">The array of materials to use in the RenderMeshArray.</param>
        /// <param name="meshes">The array of meshes to use in the RenderMeshArray.</param>
        /// <param name="materialMeshIndices">The array of MaterialMeshIndex to use in the RenderMeshArray.</param>
        public RenderMeshArray(ReadOnlySpan<UnityObjectRef<Material>> materials, ReadOnlySpan<UnityObjectRef<Mesh>> meshes, ReadOnlySpan<MaterialMeshIndex> materialMeshIndices = default)
            : this(materials, meshes, materialMeshIndices, default)
        {
        }

        RenderMeshArray(ReadOnlySpan<UnityObjectRef<Material>> materials, ReadOnlySpan<UnityObjectRef<Mesh>> meshes, ReadOnlySpan<MaterialMeshIndex> materialMeshIndices, SceneSection sceneSection)
        {
            var host = ScriptableObject.CreateInstance<RenderMeshArrayHost>();
            host.CreatedInMemory = true;
            host.Meshes = ToMeshArray(meshes);
            host.Materials = ToMaterialArray(materials);
            host.MaterialMeshIndices = materialMeshIndices.ToArray();

            m_Host = host;
            m_Hash128 = ComputeHash128(host, sceneSection);
        }

        /// <summary>
        /// Accessor property for the mesh references array.
        /// </summary>
        public UnityObjectRef<Mesh>[] MeshReferences
        {
            get => ToRefArray(MeshObjects);
            [Obsolete("Setters on RenderMeshArray are deprecated; construct a new RenderMeshArray and re-apply it instead. RenderMeshArray will become immutable in a future release.", false)]
            set => ReplaceHost(ToMeshArray(value), MaterialObjects, MaterialMeshIndices);
        }

        /// <summary>
        /// Accessor property for the material references array.
        /// </summary>
        public UnityObjectRef<Material>[] MaterialReferences
        {
            get => ToRefArray(MaterialObjects);
            [Obsolete("Setters on RenderMeshArray are deprecated; construct a new RenderMeshArray and re-apply it instead. RenderMeshArray will become immutable in a future release.", false)]
            set => ReplaceHost(MeshObjects, ToMaterialArray(value), MaterialMeshIndices);
        }

        /// <summary>
        /// Accessor property for the MaterialMeshIndex array.
        /// </summary>
        public MaterialMeshIndex[] MaterialMeshIndices
        {
            get => m_Host.Value != null ? m_Host.Value.MaterialMeshIndices : null;
            [Obsolete("Setters on RenderMeshArray are deprecated; construct a new RenderMeshArray and re-apply it instead. RenderMeshArray will become immutable in a future release.", false)]
            set => ReplaceHost(MeshObjects, MaterialObjects, value);
        }

        // Setter helper for the obsolete mutating API. The current host also backs the stored
        // shared component value and every entity using it, so we never edit it in place: we build
        // a fresh host with its own copies of the arrays and leave the old one untouched.
        void ReplaceHost(Mesh[] meshes, Material[] materials, MaterialMeshIndex[] mmi)
        {
            // meshes/materials must be non-null; mmi is optional.
            if (meshes == null || materials == null)
                throw new ArgumentException("RenderMeshArray meshes and materials must be non-null.");

            var newHost = ScriptableObject.CreateInstance<RenderMeshArrayHost>();
            newHost.CreatedInMemory = true;
            newHost.Meshes = (Mesh[])meshes.Clone();
            newHost.Materials = (Material[])materials.Clone();
            newHost.MaterialMeshIndices = mmi;
            m_Host = newHost;
            m_Hash128 = ComputeHash128(newHost);
        }

        // Direct access to the host's backing object arrays, avoiding the UnityObjectRef[]
        // allocation that MeshReferences/MaterialReferences make on every get. Used by the
        // in-package hot paths (registration, baking) that want the Mesh/Material objects.
        internal Mesh[] MeshObjects => m_Host.Value != null ? m_Host.Value.Meshes : null;
        internal Material[] MaterialObjects => m_Host.Value != null ? m_Host.Value.Materials : null;

        static Mesh[] ToMeshArray(UnityObjectRef<Mesh>[] refs)
        {
            if (refs == null)
                return null;
            var result = new Mesh[refs.Length];
            for (int i = 0; i < refs.Length; i++)
                result[i] = refs[i];
            return result;
        }

        static Material[] ToMaterialArray(UnityObjectRef<Material>[] refs)
        {
            if (refs == null)
                return null;
            var result = new Material[refs.Length];
            for (int i = 0; i < refs.Length; i++)
                result[i] = refs[i];
            return result;
        }

        static Mesh[] ToMeshArray(ReadOnlySpan<UnityObjectRef<Mesh>> refs)
        {
            var result = new Mesh[refs.Length];
            for (int i = 0; i < refs.Length; i++)
                result[i] = refs[i];
            return result;
        }

        static Material[] ToMaterialArray(ReadOnlySpan<UnityObjectRef<Material>> refs)
        {
            var result = new Material[refs.Length];
            for (int i = 0; i < refs.Length; i++)
                result[i] = refs[i];
            return result;
        }

        static UnityObjectRef<Mesh>[] ToRefArray(Mesh[] objects)
        {
            if (objects == null)
                return null;
            var result = new UnityObjectRef<Mesh>[objects.Length];
            for (int i = 0; i < objects.Length; i++)
                result[i] = objects[i];
            return result;
        }

        static UnityObjectRef<Material>[] ToRefArray(Material[] objects)
        {
            if (objects == null)
                return null;
            var result = new UnityObjectRef<Material>[objects.Length];
            for (int i = 0; i < objects.Length; i++)
                result[i] = objects[i];
            return result;
        }

        struct HostRefDelta
        {
            public UnityObjectRef<RenderMeshArrayHost> Host;
            public int Delta;
        }

        // Host reference-count deltas, applied on the main thread in ProcessPendingHostReleases.
        // Retain/Release run on arbitrary threads (subscene deserialization invokes them from a
        // loader thread), where resolving the host (UnityObjectRef.Value) or destroying it is
        // illegal, so they only record the delta here.
        static readonly ConcurrentQueue<HostRefDelta> k_SPendingHostRefDeltas = new();

        /// <summary>
        /// Called by the Entities runtime when an instance of this shared component is added to a
        /// world. Counts the world against the backing host so it can be released deterministically.
        /// </summary>
        public void Retain() => RecordHostRefDelta(1);

        /// <summary>
        /// Called by the Entities runtime when the last instance of this shared component is removed
        /// from a world. The backing host is destroyed once no world references it.
        /// </summary>
        public void Release() => RecordHostRefDelta(-1);

        void RecordHostRefDelta(int delta)
        {
            // Plain field read (no host resolution), so it is safe off the main thread.
            if (m_Host.EntityId != EntityId.None)
                k_SPendingHostRefDeltas.Enqueue(new HostRefDelta { Host = m_Host, Delta = delta });
        }

        // Applies the queued host reference-count deltas and destroys any host no world references
        // anymore. Deltas are summed before destroying so an add/remove of the same host within one
        // batch nets out correctly. Must be called on the main thread.
        internal static void ProcessPendingHostReleases()
        {
            if (k_SPendingHostRefDeltas.IsEmpty)
                return;

            var touched = new List<RenderMeshArrayHost>();
            while (k_SPendingHostRefDeltas.TryDequeue(out var delta))
            {
                var host = delta.Host.Value;
                if (host == null)
                    continue;
                host.RefCount += delta.Delta;
                touched.Add(host);
            }

            // Only destroy hosts we created in memory. Hosts loaded from a subscene or prefab are
            // owned by the content loading system and must not be destroyed here.
            foreach (var host in touched)
                if (host != null && host.RefCount <= 0 && host.CreatedInMemory)
                    UnityEngine.Object.DestroyImmediate(host);
        }

        /// <summary>
        /// Accessor property for the meshes array.
        /// </summary>
        [Obsolete("Meshes has been deprecated; use MeshReferences instead.", false)]
        public Mesh[] Meshes
        {
            get
            {
                var meshes = MeshObjects;
                return meshes != null ? (Mesh[])meshes.Clone() : null;
            }
            set => ReplaceHost(value, MaterialObjects, MaterialMeshIndices);
        }

        /// <summary>
        /// Accessor property for the materials array.
        /// </summary>
        [Obsolete("Materials has been deprecated; use MaterialReferences instead.", false)]
        public Material[] Materials
        {
            get
            {
                var materials = MaterialObjects;
                return materials != null ? (Material[])materials.Clone() : null;
            }
            set => ReplaceHost(MeshObjects, value, MaterialMeshIndices);
        }

        internal Mesh GetMeshWithStaticIndex(int staticMeshIndex)
        {
            Assert.IsTrue(staticMeshIndex <= 0, "Mesh index must be a static index (non-positive)");

            if (staticMeshIndex >= 0)
                return null;

            return m_Host.Value.Meshes[MaterialMeshInfo.StaticIndexToArrayIndex(staticMeshIndex)];
        }

        internal Material GetMaterialWithStaticIndex(int staticMaterialIndex)
        {
            Assert.IsTrue(staticMaterialIndex <= 0, "Material index must be a static index (non-positive)");

            if (staticMaterialIndex >= 0)
                return null;

            return m_Host.Value.Materials[MaterialMeshInfo.StaticIndexToArrayIndex(staticMaterialIndex)];
        }

        /// <summary>
        /// Returns a 128-bit hash that (almost) uniquely identifies the contents of the component.
        /// </summary>
        /// <remarks>
        /// This is useful to help make comparisons between RenderMeshArray instances less resource intensive.
        /// </remarks>
        /// <returns>Returns the 128-bit hash value.</returns>
        public uint4 GetHash128()
        {
            return m_Hash128;
        }

        /// <summary>
        /// Does nothing. The 128-bit hash value is computed when the RenderMeshArray is constructed.
        /// </summary>
        [Obsolete("ResetHash128 is deprecated; the hash is computed automatically when the RenderMeshArray is constructed. This method will be removed in a future release.", false)]
        public void ResetHash128()
        {
            // Recomputing the hash would drop the scene section that baking adds to it
        }

        /// <summary>
        /// Returns the 128-bit hash value of the component.
        /// </summary>
        /// <remarks>
        /// This is equivalent to calling <see cref="GetHash128"/>.
        /// </remarks>
        /// <returns>Returns the 128-bit hash value.</returns>
        [Obsolete("ComputeHash128 is deprecated; use GetHash128 instead. The hash is computed automatically when the RenderMeshArray is constructed and the result is identical. This method will be removed in a future release.", false)]
        public uint4 ComputeHash128()
        {
            return GetHash128();
        }

        internal static uint4 ComputeHash128(RenderMeshArrayHost host) => ComputeHash128(host, default);

        static uint4 ComputeHash128(RenderMeshArrayHost host, SceneSection sceneSection)
        {
            var hash = new xxHash3.StreamingState(false);

            hash.Update(sceneSection.SceneGUID);
            hash.Update(sceneSection.Section);

            var meshes              = host != null ? host.Meshes              : null;
            var materials           = host != null ? host.Materials           : null;
            var materialMeshIndices = host != null ? host.MaterialMeshIndices : null;

            int numMeshes           = meshes?.Length              ?? 0;
            int numMaterials        = materials?.Length           ?? 0;
            int numMatMeshIndices   = materialMeshIndices?.Length ?? 0;

            hash.Update(numMeshes);
            hash.Update(numMaterials);
            hash.Update(numMatMeshIndices);

            for (int i = 0; i < numMeshes; ++i)
            {
                UnityObjectRef<Mesh> meshRef = meshes[i];
                AssetHash.UpdateAsset(ref hash, meshRef.EntityId);
            }

            for (int i = 0; i < numMaterials; ++i)
            {
                UnityObjectRef<Material> materialRef = materials[i];
                AssetHash.UpdateAsset(ref hash, materialRef.EntityId);
            }

            for (int i = 0; i < numMatMeshIndices; ++i)
            {
                MaterialMeshIndex matMeshIndex = materialMeshIndices[i];
                hash.Update(matMeshIndex.MaterialIndex);
                hash.Update(matMeshIndex.MeshIndex);
                hash.Update(matMeshIndex.SubMeshIndex);
            }

            uint4 H = hash.DigestHash128();

            // Make sure the hash is never exactly zero, to keep zero as a null value
            if (math.all(H == uint4.zero))
                return new uint4(1, 0, 0, 0);

            return H;
        }

        /// <summary>
        /// Combines a list of RenderMeshes into one RenderMeshArray.
        /// </summary>
        /// <param name="renderMeshes">The list of RenderMesh instances to combine.</param>
        /// <returns>Returns a RenderMeshArray instance that contains all of the meshes and materials. The <see cref="RenderMeshArray.MaterialMeshIndices"/> field is left to null.</returns>
        public static RenderMeshArray CombineRenderMeshes(List<RenderMeshUnmanaged> renderMeshes)
        {
            var meshes = new Dictionary<UnityObjectRef<Mesh>, bool>(renderMeshes.Count);
            var materials = new Dictionary<UnityObjectRef<Material>, bool>(renderMeshes.Count);

            foreach (var renderMesh in renderMeshes)
            {
                meshes[renderMesh.mesh] = true;
                if (renderMesh.materialForSubMesh != null)
                    materials[renderMesh.materialForSubMesh] = true;
            }

            return new RenderMeshArray(materials.Keys.ToArray(), meshes.Keys.ToArray());
        }

        /// <summary>
        /// **Obsolete.** Use CombineRenderMeshes(List&lt;RenderMeshUnmanaged&gt;) instead.
        /// </summary>
        /// <remarks>
        /// Combines a list of RenderMeshes into one RenderMeshArray.
        /// </remarks>
        /// <param name="renderMeshes">The list of RenderMesh instances to combine.</param>
        /// <returns>Returns a RenderMeshArray instance that contains all of the meshes and materials. The <see cref="RenderMeshArray.MaterialMeshIndices"/> field is left to null.</returns>
        [Obsolete("Use CombineRenderMeshes(List<RenderMeshUnmanaged>) instead. (RemovedAfter Entities 2.0)")]
        public static RenderMeshArray CombineRenderMeshes(List<RenderMesh> renderMeshes)
        {
            var meshes = new Dictionary<Mesh, bool>(renderMeshes.Count);
            var materials = new Dictionary<Material, bool>(renderMeshes.Count);

            foreach (var renderMesh in renderMeshes)
            {
                meshes[renderMesh.mesh] = true;

                if (renderMesh.materials != null)
                {
                    foreach (var material in renderMesh.materials)
                    {
                        if (material != null)
                            materials[material] = true;
                    }
                }
            }

            return new RenderMeshArray(materials.Keys.ToArray(), meshes.Keys.ToArray());
        }

        /// <summary>
        /// Combines a list of RenderMeshArrays into one RenderMeshArray.
        /// </summary>
        /// <param name="renderMeshArrays">The list of RenderMeshArray instances to combine.</param>
        /// <returns>Returns a RenderMeshArray instance that contains all of the meshes and materials. The <see cref="RenderMeshArray.MaterialMeshIndices"/> field is left to null.</returns>
        public static RenderMeshArray CombineRenderMeshArrays(List<RenderMeshArray> renderMeshArrays)
        {
            int totalMeshes = 0;
            int totalMaterials = 0;

            foreach (var rma in renderMeshArrays)
            {
                if (rma.MeshObjects == null || rma.MaterialObjects == null)
                    throw new ArgumentException(
                        "Cannot combine a RenderMeshArray that has no backing data (it is default-initialized or its host has been unloaded).",
                        nameof(renderMeshArrays));

                totalMeshes += rma.MeshObjects.Length;
                totalMaterials += rma.MaterialObjects.Length;
            }

            var meshes = new Dictionary<UnityObjectRef<Mesh>, bool>(totalMeshes);
            var materials = new Dictionary<UnityObjectRef<Material>, bool>(totalMaterials);

            foreach (var rma in renderMeshArrays)
            {
                foreach (var mesh in rma.MeshObjects)
                {
                    if (mesh != null)
                        meshes[mesh] = true;
                }

                foreach (var material in rma.MaterialObjects)
                {
                    if (material != null)
                        materials[material] = true;
                }
            }

            return new RenderMeshArray(materials.Keys.ToArray(), meshes.Keys.ToArray());
        }

        /// <summary>
        /// Creates the new instance of the RenderMeshArray from given mesh and material lists, removing duplicate entries.
        /// </summary>
        /// <param name="materialsWithDuplicates">The list of the materials.</param>
        /// <param name="meshesWithDuplicates">The list of the meshes.</param>
        /// <returns>Returns a RenderMeshArray instance that contains all off the meshes and materials, and with no duplicates. The <see cref="RenderMeshArray.MaterialMeshIndices"/> field is left to null.</returns>
        public static RenderMeshArray CreateWithDeduplication(
            List<Material> materialsWithDuplicates, List<Mesh> meshesWithDuplicates)
        {
            var meshes = new Dictionary<UnityObjectRef<Mesh>, bool>(meshesWithDuplicates.Count);
            var materials = new Dictionary<UnityObjectRef<Material>, bool>(materialsWithDuplicates.Count);

            foreach (var mat in materialsWithDuplicates)
                materials[mat] = true;

            foreach (var mesh in meshesWithDuplicates)
                meshes[mesh] = true;

            return new RenderMeshArray(materials.Keys.ToArray(), meshes.Keys.ToArray());
        }

        /// <summary>
        /// Creates the new instance of the RenderMeshArray from given mesh and material lists, removing duplicate entries.
        /// </summary>
        /// <param name="materialsWithDuplicates">The list of the materials.</param>
        /// <param name="meshesWithDuplicates">The list of the meshes.</param>
        /// <returns>Returns a RenderMeshArray instance that contains all off the meshes and materials, and with no duplicates. The <see cref="RenderMeshArray.MaterialMeshIndices"/> field is left to null.</returns>
        public static RenderMeshArray CreateWithDeduplication(
            List<UnityObjectRef<Material>> materialsWithDuplicates, List<UnityObjectRef<Mesh>> meshesWithDuplicates)
        {
            var meshes = new Dictionary<UnityObjectRef<Mesh>, bool>(meshesWithDuplicates.Count);
            var materials = new Dictionary<UnityObjectRef<Material>, bool>(materialsWithDuplicates.Count);

            foreach (var mat in materialsWithDuplicates)
                materials[mat] = true;

            foreach (var mesh in meshesWithDuplicates)
                meshes[mesh] = true;

            return new RenderMeshArray(materials.Keys.ToArray(), meshes.Keys.ToArray());
        }

        /// <summary>
        /// Gets the material for given MaterialMeshInfo.
        /// </summary>
        /// <param name="materialMeshInfo">The MaterialMeshInfo to use.</param>
        /// <returns>Returns the associated material instance, or null if the material is runtime.</returns>
        public Material GetMaterial(MaterialMeshInfo materialMeshInfo)
        {
            if (materialMeshInfo.IsRuntimeMaterial)
                return null;

            // When using an index range, just return the first material of the range
            if (materialMeshInfo.HasMaterialMeshIndexRange)
            {
                RangeInt range = materialMeshInfo.MaterialMeshIndexRange;
                Assert.IsTrue(range.length > 0);

                int firstMaterialIndex = MaterialMeshIndices[range.start].MaterialIndex;
                return MaterialObjects[firstMaterialIndex];
            }
            else
            {
                return MaterialObjects[materialMeshInfo.MaterialArrayIndex];
            }
        }

        /// <summary>
        /// Gets the materials for given MaterialMeshInfo.
        /// </summary>
        /// <param name="materialMeshInfo">The MaterialMeshInfo to use.</param>
        /// <returns>Returns the associated material instances, or null if the material is runtime.</returns>
        public List<Material> GetMaterials(MaterialMeshInfo materialMeshInfo)
        {
            if (materialMeshInfo.IsRuntimeMaterial)
                return null;

            if (materialMeshInfo.HasMaterialMeshIndexRange)
            {
                RangeInt range = materialMeshInfo.MaterialMeshIndexRange;
                Assert.IsTrue(range.length > 0);

                var materials = new List<Material>(range.length);

                for (int i = range.start; i < range.end; i++)
                {
                    int materialIndex = MaterialMeshIndices[i].MaterialIndex;
                    materials.Add(MaterialObjects[materialIndex]);
                }

                return materials;
            }
            else
            {
                var material = MaterialObjects[materialMeshInfo.MaterialArrayIndex];
                return new List<Material> { material };
            }
        }

        /// <summary>
        /// Gets the mesh for given MaterialMeshInfo.
        /// </summary>
        /// <param name="materialMeshInfo">The MaterialMeshInfo to use.</param>
        /// <returns>Returns the associated Mesh instance or null if the mesh is runtime.</returns>
        public Mesh GetMesh(MaterialMeshInfo materialMeshInfo)
        {
            if (materialMeshInfo.IsRuntimeMesh)
                return null;

            // When using an index range, just return the first mesh of the range
            if (materialMeshInfo.HasMaterialMeshIndexRange)
            {
                RangeInt range = materialMeshInfo.MaterialMeshIndexRange;
                Assert.IsTrue(range.length > 0);

                int firstMeshIndex = MaterialMeshIndices[range.start].MeshIndex;
                return MeshObjects[firstMeshIndex];
            }
            else
            {
                return MeshObjects[materialMeshInfo.MeshArrayIndex];
            }
        }

        /// <summary>
        /// Determines whether two object instances are equal based on their hashes.
        /// </summary>
        /// <param name="other">The object to compare with the current object.</param>
        /// <returns>Returns true if the specified object is equal to the current object. Otherwise, returns false.</returns>
        public bool Equals(RenderMeshArray other)
        {
            return math.all(GetHash128() == other.GetHash128());
        }

        /// <summary>
        /// Determines whether two object instances are equal based on their hashes.
        /// </summary>
        /// <param name="obj">The object to compare with the current object.</param>
        /// <returns>Returns true if the specified object is equal to the current object. Otherwise, returns false.</returns>
        public override bool Equals(object obj)
        {
            return obj is RenderMeshArray other && Equals(other);
        }

        /// <summary>
        /// Calculates the hash code for this object.
        /// </summary>
        /// <returns>The hash code.</returns>
        public override int GetHashCode()
        {
            return (int) GetHash128().x;
        }

        /// <summary>
        /// The equality operator == returns true if its operands are equal, false otherwise.
        /// </summary>
        /// <param name="left">The left instance to compare.</param>
        /// <param name="right">The right instance to compare.</param>
        /// <returns>True if left and right instances are equal and false otherwise.</returns>
        public static bool operator ==(RenderMeshArray left, RenderMeshArray right)
        {
            return left.Equals(right);
        }

        /// <summary>
        /// The not equality operator != returns false if its operands are equal, true otherwise.
        /// </summary>
        /// <param name="left">The left instance to compare.</param>
        /// <param name="right">The right instance to compare.</param>
        /// <returns>False if left and right instances are equal and true otherwise.</returns>
        public static bool operator !=(RenderMeshArray left, RenderMeshArray right)
        {
            return !left.Equals(right);
        }
    }
}
