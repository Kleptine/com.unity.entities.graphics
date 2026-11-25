#if URP_10_0_0_OR_NEWER && (SURFACE_CACHE_SUPPORTED || UNITY_EDITOR)

using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Entities.Graphics;
using Unity.Jobs;
using Unity.Transforms;
using UnityEngine.Rendering.Universal;

namespace Unity.Rendering
{
    [BurstCompile]
    internal struct SurfaceCacheTransformChangeJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<ArchetypeChunk> Chunks;
        [ReadOnly] public EntityTypeHandle EntityHandle;
        [ReadOnly] public ComponentTypeHandle<LocalToWorld> LocalToWorldHandle;

        public NativeList<SurfaceCacheEntityTransformRecord>.ParallelWriter TransformChanged;

        public void Execute(int index)
        {
            var chunk = Chunks[index];
            var entities = chunk.GetNativeArray(EntityHandle);
            var localToWorlds = chunk.GetNativeArray(ref LocalToWorldHandle);

            int count = chunk.Count;
            var records = new NativeArray<SurfaceCacheEntityTransformRecord>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < count; i++)
            {
                records[i] = new SurfaceCacheEntityTransformRecord
                {
                    Key = entities[i],
                    LocalToWorld = localToWorlds[i].Value,
                };
            }

            unsafe
            {
                TransformChanged.AddRangeNoResize(records.GetUnsafeReadOnlyPtr(), count);
            }
        }

        public static bool NeedsFullRecords(
            in ArchetypeChunk chunk,
            ref ComponentTypeHandle<MaterialMeshInfo> materialMeshInfoHandle,
            ref ComponentTypeHandle<MeshLODComponent> lodGroupMembershipHandle,
            SharedComponentTypeHandle<RenderMeshArray> renderMeshArrayHandle,
            SharedComponentTypeHandle<RenderFilterSettings> renderFilterSettingsHandle,
            uint lastSystemVersion,
            bool fullResync,
            in NativeHashSet<Entity> changedLODGroups)
        {
            if (fullResync
                || chunk.DidOrderChange(lastSystemVersion)
                || chunk.DidChange(ref materialMeshInfoHandle, lastSystemVersion)
                || chunk.DidChange(renderMeshArrayHandle, lastSystemVersion)
                || chunk.DidChange(renderFilterSettingsHandle, lastSystemVersion))
                return true;

            if (!chunk.Has(ref lodGroupMembershipHandle))
                return false;

            if (chunk.DidChange(ref lodGroupMembershipHandle, lastSystemVersion))
                return true;

            if (changedLODGroups.IsEmpty)
                return false;

            // A group change never dirties its renderers' chunks, so the only way to find them is to ask each
            // entity which group it points at.
            var lodGroupMemberships = chunk.GetNativeArray(ref lodGroupMembershipHandle);
            for (int i = 0; i < lodGroupMemberships.Length; i++)
            {
                if (changedLODGroups.Contains(lodGroupMemberships[i].Group))
                    return true;
            }

            return false;
        }
    }
}

#endif
