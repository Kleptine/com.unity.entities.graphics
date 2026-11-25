#if URP_10_0_0_OR_NEWER && (SURFACE_CACHE_SUPPORTED || UNITY_EDITOR)

using System;
using UnityEngine.Assertions;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Unity.Rendering
{
    /// <summary>
    /// Unmanaged, Burst-readable mirror of every <see cref="RenderMeshArray"/> in a world, keyed by shared
    /// component index and holding assets as <see cref="EntityId"/>s.
    /// </summary>
    /// <remarks>
    /// Exists because RenderMeshArray's arrays live on a managed host that a job cannot read.
    /// <see cref="Update"/> is main thread only; the map is safe to read from a job once updated.
    /// </remarks>
    internal struct SurfaceCacheRenderMeshArrayTable : IDisposable
    {
        internal struct Entry
        {
            public UnsafeList<EntityId> Meshes;
            public UnsafeList<EntityId> Materials;
            public UnsafeList<MaterialMeshIndex> MaterialMeshIndices;
            // Detects a recycled shared component index now referring to different contents.
            public uint4 Hash128;

            public void Dispose()
            {
                if (Meshes.IsCreated)
                    Meshes.Dispose();
                if (Materials.IsCreated)
                    Materials.Dispose();
                if (MaterialMeshIndices.IsCreated)
                    MaterialMeshIndices.Dispose();
            }
        }

        NativeHashMap<int, Entry> m_Entries;

        public NativeHashMap<int, Entry> Entries => m_Entries;

        public static SurfaceCacheRenderMeshArrayTable Create()
        {
            return new SurfaceCacheRenderMeshArrayTable
            {
                m_Entries = new NativeHashMap<int, Entry>(64, Allocator.Persistent),
            };
        }

        public void Dispose()
        {
            if (!m_Entries.IsCreated)
                return;

            foreach (var pair in m_Entries)
            {
                var entry = pair.Value;
                entry.Dispose();
            }

            m_Entries.Dispose();
        }

        /// <summary>Rebuilds entries whose contents changed; steady state is one pass over the unique values.</summary>
        public void Update(EntityManager entityManager)
        {
            entityManager.GetAllUniqueSharedComponents<RenderMeshArray>(
                out var renderMeshArrays, out var sharedIndices, Allocator.Temp);

            // Null host: the default value, or an unloaded host asset. Mirrors RegisterMaterialsAndMeshesSystem.
            var live = new NativeHashSet<int>(renderMeshArrays.Length, Allocator.Temp);
            for (int i = 0; i < renderMeshArrays.Length; i++)
            {
                var renderMeshArray = renderMeshArrays[i];
                if (renderMeshArray.MeshObjects == null || renderMeshArray.MaterialObjects == null)
                    continue;

                int sharedIndex = sharedIndices[i];
                live.Add(sharedIndex);

                var hash = renderMeshArray.GetHash128();
                if (m_Entries.TryGetValue(sharedIndex, out var existing))
                {
                    if (!math.any(existing.Hash128 != hash))
                        continue;

                    existing.Dispose();
                }

                m_Entries[sharedIndex] = Build(renderMeshArray, hash);
            }

            RemoveEntriesNotIn(live);
        }

        void RemoveEntriesNotIn(NativeHashSet<int> live)
        {
            var dead = new NativeList<int>(m_Entries.Count, Allocator.Temp);
            foreach (var pair in m_Entries)
            {
                if (!live.Contains(pair.Key))
                    dead.Add(pair.Key);
            }

            foreach (var sharedIndex in dead)
            {
                var entry = m_Entries[sharedIndex];
                entry.Dispose();
                m_Entries.Remove(sharedIndex);
            }
        }

        static Entry Build(in RenderMeshArray renderMeshArray, uint4 hash)
        {
            // Not MeshReferences/MaterialReferences: those allocate a fresh UnityObjectRef[] on every get.
            var meshes = renderMeshArray.MeshObjects;
            var materials = renderMeshArray.MaterialObjects;
            var materialMeshIndices = renderMeshArray.MaterialMeshIndices;

            var entry = new Entry
            {
                Meshes = new UnsafeList<EntityId>(meshes.Length, Allocator.Persistent),
                Materials = new UnsafeList<EntityId>(materials.Length, Allocator.Persistent),
                MaterialMeshIndices = new UnsafeList<MaterialMeshIndex>(
                    materialMeshIndices?.Length ?? 0, Allocator.Persistent),
                Hash128 = hash,
            };

            foreach (var mesh in meshes)
                entry.Meshes.Add(mesh != null ? mesh.GetEntityId() : EntityId.None);

            foreach (var material in materials)
                entry.Materials.Add(material != null ? material.GetEntityId() : EntityId.None);

            if (materialMeshIndices != null)
            {
                foreach (var materialMeshIndex in materialMeshIndices)
                    entry.MaterialMeshIndices.Add(materialMeshIndex);
            }

            return entry;
        }

        /// <summary>
        /// Mirrors <see cref="RenderMeshArray.GetMesh"/>, but returns <see cref="EntityId.None"/> where it would
        /// throw: runtime meshes, which the caller resolves through Entities Graphics, and out-of-range indices.
        /// </summary>
        public static EntityId GetMesh(in Entry entry, MaterialMeshInfo materialMeshInfo)
        {
            if (materialMeshInfo.IsRuntimeMesh)
                return EntityId.None;

            int meshIndex;
            if (materialMeshInfo.HasMaterialMeshIndexRange)
            {
                // When using an index range, the first mesh of the range wins.
                var range = materialMeshInfo.MaterialMeshIndexRange;
                if (range.length <= 0 || (uint)range.start >= (uint)entry.MaterialMeshIndices.Length)
                    return EntityId.None;

                meshIndex = entry.MaterialMeshIndices[range.start].MeshIndex;
            }
            else
            {
                meshIndex = materialMeshInfo.MeshArrayIndex;
            }

            if ((uint)meshIndex >= (uint)entry.Meshes.Length)
                return EntityId.None;

            return entry.Meshes[meshIndex];
        }

        /// <summary>
        /// Mirrors <see cref="RenderMeshArray.GetMaterial"/>. Ranged instances have one material per submesh and are
        /// walked through <see cref="Entry.MaterialMeshIndices"/> instead.
        /// </summary>
        public static EntityId GetMaterial(in Entry entry, MaterialMeshInfo materialMeshInfo)
        {
            if (materialMeshInfo.IsRuntimeMaterial)
                return EntityId.None;

            Assert.IsFalse(materialMeshInfo.HasMaterialMeshIndexRange,
                "Ranged instances are walked through Entry.MaterialMeshIndices instead.");

            int materialIndex = materialMeshInfo.MaterialArrayIndex;

            if ((uint)materialIndex >= (uint)entry.Materials.Length)
                return EntityId.None;

            return entry.Materials[materialIndex];
        }
    }
}

#endif
