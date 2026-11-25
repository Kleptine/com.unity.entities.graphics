#if URP_10_0_0_OR_NEWER && (SURFACE_CACHE_SUPPORTED || UNITY_EDITOR)

using System;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Entities.Graphics;
using Unity.Jobs;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Unity.Rendering
{
    [BurstCompile]
    internal struct SurfaceCacheFullRecordJob : IJob
    {
        [ReadOnly] public NativeArray<ArchetypeChunk> Chunks;
        [ReadOnly] public NativeArray<v128> PushMasks;
        [ReadOnly] public EntityTypeHandle EntityHandle;
        [ReadOnly] public ComponentTypeHandle<MaterialMeshInfo> MaterialMeshInfoHandle;
        [ReadOnly] public ComponentTypeHandle<GlobalIlluminationMeshLod> GlobalIlluminationMeshLodHandle;
        [ReadOnly] public ComponentTypeHandle<LocalToWorld> LocalToWorldHandle;
        [ReadOnly] public ComponentTypeHandle<DisableRendering> DisableRenderingHandle;
        [ReadOnly] public ComponentTypeHandle<Disabled> DisabledHandle;
        [ReadOnly] public ComponentTypeHandle<MeshLODComponent> LODGroupMembershipHandle;
        [ReadOnly] public ComponentTypeHandle<ExcludedFromGlobalIllumination> ExcludedFromGlobalIlluminationHandle;
        [ReadOnly] public SharedComponentTypeHandle<RenderMeshArray> RenderMeshArrayHandle;
        [ReadOnly] public SharedComponentTypeHandle<RenderFilterSettings> RenderFilterSettingsHandle;
        [ReadOnly] public ComponentLookup<MeshLODGroupComponent> LODGroupLookup;
        [ReadOnly] public ComponentLookup<GlobalIlluminationLOD> GlobalIlluminationLODLookup;
        [ReadOnly] public NativeHashMap<int, SurfaceCacheRenderMeshArrayTable.Entry> RenderMeshArrays;
        [ReadOnly] public ComponentTypeHandle<WorldRenderBounds> WorldRenderBoundsHandle;
        [ReadOnly] public ComponentTypeHandle<ChunkWorldRenderBounds> ChunkWorldRenderBoundsHandle;
        [ReadOnly] public NativeArray<SurfaceCacheCullingObserver> CullingObservers;

        public ComponentTypeHandle<SurfaceCacheExtracted> ExtractedHandle;
        public NativeList<Entity> CulledUntrackedEntities;

        public SurfaceCacheEntityChangeWriter Writer;

        // Material override components are only read on the main thread, so those chunks fall back too.
        [ReadOnly] public NativeArray<bool> ChunkHasMaterialOverrides;

        public NativeList<int> FallbackChunkIndices;

        public void Execute()
        {
            var materials = new NativeList<SurfaceCacheSubMeshMaterial>(8, Allocator.Temp);
            for (int c = 0; c < Chunks.Length; c++)
            {
                var chunk = Chunks[c];
                if (ChunkHasMaterialOverrides[c] || HasRuntimeIds(chunk))
                {
                    FallbackChunkIndices.Add(c);
                    continue;
                }

                PushChunk(chunk, PushMasks[c], ref materials);
            }
        }

        bool HasRuntimeIds(in ArchetypeChunk chunk)
        {
            var materialMeshInfos = chunk.GetNativeArray(ref MaterialMeshInfoHandle);
            for (int i = 0; i < materialMeshInfos.Length; i++)
            {
                var info = materialMeshInfos[i];
                if (info.IsRuntimeMesh || (!info.HasMaterialMeshIndexRange && info.IsRuntimeMaterial))
                    return true;
            }

            return false;
        }

        void PushChunk(in ArchetypeChunk chunk, in v128 pushMask, ref NativeList<SurfaceCacheSubMeshMaterial> materials)
        {
            int renderMeshArrayIndex = chunk.GetSharedComponentIndex(RenderMeshArrayHandle);
            SurfaceCacheRenderMeshArrayTable.Entry entry = default;
            bool hasEntry = renderMeshArrayIndex != -1 && RenderMeshArrays.TryGetValue(renderMeshArrayIndex, out entry);
            uint renderingLayerMask = chunk.Has(RenderFilterSettingsHandle)
                ? chunk.GetSharedComponent(RenderFilterSettingsHandle).RenderingLayerMask
                : RenderFilterSettings.Default.RenderingLayerMask;
            bool chunkIsHidden = chunk.Has(ref DisableRenderingHandle) || chunk.Has(ref DisabledHandle);
            bool chunkHasLODGroupMembership = chunk.Has(ref LODGroupMembershipHandle);
            bool chunkIsExcluded = chunk.Has(ref ExcludedFromGlobalIlluminationHandle);

            var entities = chunk.GetNativeArray(EntityHandle);
            var materialMeshInfos = chunk.GetNativeArray(ref MaterialMeshInfoHandle);
            var giMeshLods = chunk.Has(ref GlobalIlluminationMeshLodHandle) ? chunk.GetNativeArray(ref GlobalIlluminationMeshLodHandle) : default;
            var localToWorlds = chunk.GetNativeArray(ref LocalToWorldHandle);
            var lodGroupMemberships = chunkHasLODGroupMembership ? chunk.GetNativeArray(ref LODGroupMembershipHandle) : default;
            v128 culled = SurfaceCacheEntityCulling.RecordCulledState(
                chunk, pushMask, ref WorldRenderBoundsHandle, ref ChunkWorldRenderBoundsHandle, ref ExtractedHandle,
                CullingObservers, entities, CulledUntrackedEntities);

            for (int i = 0; i < chunk.Count; i++)
            {
                if (!SurfaceCacheEntityCulling.IsBitSet(pushMask, i))
                    continue;

                bool inGlobalIlluminationLod = !chunkIsExcluded
                    && (!chunkHasLODGroupMembership || GlobalIlluminationLODSelection.IsInGlobalIlluminationLOD(
                        lodGroupMemberships[i], LODGroupLookup, GlobalIlluminationLODLookup));
                var record = new SurfaceCacheEntityInstanceRecord
                {
                    Key = entities[i],
                    MeshLod = giMeshLods.IsCreated ? giMeshLods[i].Value : 0,
                    LocalToWorld = localToWorlds[i].Value,
                    RenderingLayerMask = renderingLayerMask,
                    Visible = !chunkIsHidden
                        && inGlobalIlluminationLod
                        && !SurfaceCacheEntityCulling.IsBitSet(culled, i)
                        && chunk.IsComponentEnabled(ref MaterialMeshInfoHandle, i),
                };

                materials.Clear();
                record.Mesh = Resolve(materialMeshInfos[i], hasEntry, entry, ref materials);
                Writer.PushChanged(record, materials.AsArray().AsReadOnlySpan(), ReadOnlySpan<SurfaceCacheMaterialOverride>.Empty);
            }
        }

        static EntityId Resolve(
            in MaterialMeshInfo materialMeshInfo,
            bool hasEntry,
            in SurfaceCacheRenderMeshArrayTable.Entry entry,
            ref NativeList<SurfaceCacheSubMeshMaterial> materials)
        {
            if (!hasEntry)
                return EntityId.None;

            var mesh = SurfaceCacheRenderMeshArrayTable.GetMesh(entry, materialMeshInfo);
            if (mesh == EntityId.None)
                return mesh;

            if (!materialMeshInfo.HasMaterialMeshIndexRange)
            {
                materials.Add(new SurfaceCacheSubMeshMaterial
                {
                    SubMeshIndex = materialMeshInfo.SubMesh,
                    Material = SurfaceCacheRenderMeshArrayTable.GetMaterial(entry, materialMeshInfo),
                });
                return mesh;
            }

            var range = materialMeshInfo.MaterialMeshIndexRange;
            if (range.length <= 0 || (uint)range.start >= (uint)entry.MaterialMeshIndices.Length)
                return mesh;

            // Entries referencing a mesh other than the range's first are ignored, matching GetMesh.
            int firstMeshIndex = entry.MaterialMeshIndices[range.start].MeshIndex;
            int end = range.start + range.length;
            if (end > entry.MaterialMeshIndices.Length)
                end = entry.MaterialMeshIndices.Length;

            for (int i = range.start; i < end; i++)
            {
                var materialMeshIndex = entry.MaterialMeshIndices[i];
                if (materialMeshIndex.MeshIndex != firstMeshIndex)
                    continue;

                materials.Add(new SurfaceCacheSubMeshMaterial
                {
                    SubMeshIndex = materialMeshIndex.SubMeshIndex,
                    Material = (uint)materialMeshIndex.MaterialIndex < (uint)entry.Materials.Length
                        ? entry.Materials[materialMeshIndex.MaterialIndex]
                        : EntityId.None,
                });
            }

            return mesh;
        }
    }
}

#endif
