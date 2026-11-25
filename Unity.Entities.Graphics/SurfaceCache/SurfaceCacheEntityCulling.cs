#if URP_10_0_0_OR_NEWER && (SURFACE_CACHE_SUPPORTED || UNITY_EDITOR)

using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Rendering.Universal;

namespace Unity.Rendering
{
    internal struct SurfaceCacheExtracted : IComponentData, IEnableableComponent
    {
    }

    internal static class SurfaceCacheEntityCulling
    {
        public static bool IsCulled(in AABB bounds, NativeArray<SurfaceCacheCullingObserver> observers)
        {
            float radius = math.length(bounds.Extents);
            for (int i = 0; i < observers.Length; i++)
            {
                var observer = observers[i];
                if (!IsWithinDistance(bounds, observer))
                    continue;

                if (IsLargeEnough(radius, math.distance(bounds.Center, observer.Position), observer.MinAngle))
                    return false;
            }

            return true;
        }

        public static bool IsEveryEntityCulled(in AABB chunkBounds, NativeArray<SurfaceCacheCullingObserver> observers)
        {
            float radius = math.length(chunkBounds.Extents);
            for (int i = 0; i < observers.Length; i++)
            {
                var observer = observers[i];
                if (!IsWithinDistance(chunkBounds, observer))
                    continue;

                float nearestDistance = math.distance(math.clamp(observer.Position, chunkBounds.Min, chunkBounds.Max), observer.Position);
                if (IsLargeEnough(radius, nearestDistance, observer.MinAngle))
                    return false;
            }

            return true;
        }

        public static v128 ComputeCulledMask(
            in ArchetypeChunk chunk,
            ref ComponentTypeHandle<WorldRenderBounds> worldRenderBoundsHandle,
            ref ComponentTypeHandle<ChunkWorldRenderBounds> chunkWorldRenderBoundsHandle,
            NativeArray<SurfaceCacheCullingObserver> observers)
        {
            if (observers.Length == 0 || !chunk.Has(ref worldRenderBoundsHandle))
                return default;

            if (chunk.HasChunkComponent(ref chunkWorldRenderBoundsHandle)
                && IsEveryEntityCulled(chunk.GetChunkComponentData(ref chunkWorldRenderBoundsHandle).Value, observers))
                return FirstBits(chunk.Count);

            var worldRenderBounds = chunk.GetNativeArray(ref worldRenderBoundsHandle);
            ulong lower = 0;
            ulong upper = 0;
            for (int i = 0; i < worldRenderBounds.Length; i++)
            {
                if (!IsCulled(worldRenderBounds[i].Value, observers))
                    continue;

                if (i < 64)
                    lower |= 1ul << i;
                else
                    upper |= 1ul << (i - 64);
            }

            return new v128(lower, upper);
        }

        public static v128 RecordCulledState(
            in ArchetypeChunk chunk,
            in v128 pushMask,
            ref ComponentTypeHandle<WorldRenderBounds> worldRenderBoundsHandle,
            ref ComponentTypeHandle<ChunkWorldRenderBounds> chunkWorldRenderBoundsHandle,
            ref ComponentTypeHandle<SurfaceCacheExtracted> extractedHandle,
            NativeArray<SurfaceCacheCullingObserver> observers,
            NativeArray<Entity> entities,
            NativeList<Entity> culledUntrackedEntities)
        {
            v128 culled = ComputeCulledMask(chunk, ref worldRenderBoundsHandle, ref chunkWorldRenderBoundsHandle, observers);
            bool isTracked = chunk.Has(ref extractedHandle);
            for (int i = 0; i < chunk.Count; i++)
            {
                if (!IsBitSet(pushMask, i))
                    continue;

                bool isCulled = IsBitSet(culled, i);
                if (isTracked)
                    chunk.SetComponentEnabled(ref extractedHandle, i, !isCulled);
                else if (isCulled)
                    culledUntrackedEntities.Add(entities[i]);
            }

            return culled;
        }

        public static bool IsBitSet(in v128 mask, int index)
        {
            ulong word = index < 64 ? mask.ULong0 : mask.ULong1;
            return (word & (1ul << (index & 63))) != 0;
        }

        public static v128 FirstBits(int count)
        {
            return new v128(
                count >= 64 ? ~0ul : (1ul << count) - 1,
                count >= 128 ? ~0ul : count <= 64 ? 0ul : (1ul << (count - 64)) - 1);
        }

        public static bool IsEmpty(in v128 mask)
        {
            return (mask.ULong0 | mask.ULong1) == 0;
        }

        public static int CountBits(in v128 mask)
        {
            return math.countbits(mask.ULong0) + math.countbits(mask.ULong1);
        }

        static bool IsWithinDistance(in AABB bounds, in SurfaceCacheCullingObserver observer)
        {
            float3 closest = math.clamp(observer.Position, bounds.Min, bounds.Max);
            return math.distancesq(closest, observer.Position) <= observer.MaxDistance * observer.MaxDistance;
        }

        static bool IsLargeEnough(float radius, float distance, float minAngle)
        {
            return radius >= distance || 2.0f * math.atan2(radius, distance) >= minAngle;
        }
    }

    [BurstCompile]
    internal struct SurfaceCacheCullingChangeJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<ArchetypeChunk> Chunks;
        [ReadOnly] public NativeArray<SurfaceCacheCullingObserver> Observers;
        [ReadOnly] public ComponentTypeHandle<WorldRenderBounds> WorldRenderBoundsHandle;
        [ReadOnly] public ComponentTypeHandle<ChunkWorldRenderBounds> ChunkWorldRenderBoundsHandle;
        [ReadOnly] public ComponentTypeHandle<SurfaceCacheExtracted> ExtractedHandle;

        [WriteOnly] public NativeArray<v128> ChangedMasks;

        public void Execute(int index)
        {
            var chunk = Chunks[index];
            v128 culled = SurfaceCacheEntityCulling.ComputeCulledMask(
                chunk, ref WorldRenderBoundsHandle, ref ChunkWorldRenderBoundsHandle, Observers);
            v128 all = SurfaceCacheEntityCulling.FirstBits(chunk.Count);
            v128 extracted = chunk.Has(ref ExtractedHandle) ? chunk.GetEnableableBits(ref ExtractedHandle) : all;

            ChangedMasks[index] = new v128(
                ~(culled.ULong0 ^ extracted.ULong0) & all.ULong0,
                ~(culled.ULong1 ^ extracted.ULong1) & all.ULong1);
        }
    }

    [BurstCompile]
    internal struct SurfaceCacheMarkCulledJob : IJob
    {
        [ReadOnly] public NativeArray<Entity> Entities;
        public ComponentLookup<SurfaceCacheExtracted> ExtractedLookup;

        public void Execute()
        {
            for (int i = 0; i < Entities.Length; i++)
                ExtractedLookup.SetComponentEnabled(Entities[i], false);
        }
    }
}

#endif
