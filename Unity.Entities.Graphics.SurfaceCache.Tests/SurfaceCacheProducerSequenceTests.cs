#if URP_10_0_0_OR_NEWER && (SURFACE_CACHE_SUPPORTED || UNITY_EDITOR)

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Random = Unity.Mathematics.Random;

namespace Unity.Entities.Graphics.Tests.SurfaceCache
{
    internal partial class SurfaceCacheProducerTests
    {
        // Few instances and long sequences, so the same instance often changes several times between drains.
        static readonly int[] k_SequenceSeeds = Enumerable.Range(1, 32).ToArray();

        // A selected level, an unselected one, one past the last level, and one equal to the producer's sentinel.
        static readonly int[] k_SequenceGlobalIlluminationLods = { 0, 1, 8, -2 };
        const int k_SequenceLodLevelCount = 8;

        [Test]
        public void RandomChangeSequences_ConsumerViewMatchesEntities([ValueSource(nameof(k_SequenceSeeds))] int seed)
        {
            RunRandomChangeSequence(seed, count: 4, steps: 2000);
        }

        [Test]
        public void RandomChangeSequencesWithParallelTransformJob_ConsumerViewMatchesEntities([Values(1, 2, 3, 4)] int seed)
        {
            RunRandomChangeSequence(seed, k_ParallelCount, steps: 100);
        }

        void RunRandomChangeSequence(int seed, int count, int steps)
        {
            var random = new Random((uint)seed * 7919u + 1u);
            var model = new Dictionary<EntityId, SurfaceCacheEntityInstanceRecord>();
            var group = CreateLodGroup(globalIlluminationLod: 0);
            int globalIlluminationLodIndex = 0;
            float step = 0f;

            var prototype = CreateRenderableEntity();
            m_Manager.AddComponent<Prefab>(prototype);

            var live = CreateRenderableEntities(count).ToList();
            for (int i = 0; i < live.Count; i++)
            {
                if (i % 2 == 0)
                    m_Manager.AddComponent<OtherChunkTag>(live[i]);
                if (i % 3 == 0)
                    m_Manager.AddComponentData(live[i], new MeshLODComponent { Group = group, LODMask = 1 << 0 });
            }

            var log = new List<string>();
            for (int s = 0; s < steps; s++)
            {
                log.Add("step " + s + ":");
                int changeCount = random.NextInt(1, 4);
                for (int c = 0; c < changeCount; c++)
                {
                    int change = random.NextInt(14);
                    if (change == 0)
                    {
                        step++;
                        for (int i = 0; i < live.Count; i++)
                        {
                            if (m_Manager.HasComponent<LocalToWorld>(live[i]))
                                m_Manager.SetComponentData(live[i], new LocalToWorld { Value = float4x4.Translate(new float3(i, step, 0f)) });
                        }

                        log.Add("move all");
                        continue;
                    }

                    if (change == 1)
                    {
                        globalIlluminationLodIndex = (globalIlluminationLodIndex + 1) % k_SequenceGlobalIlluminationLods.Length;
                        var globalIlluminationLod = new GlobalIlluminationLOD { Value = k_SequenceGlobalIlluminationLods[globalIlluminationLodIndex] };
                        if (m_Manager.HasComponent<GlobalIlluminationLOD>(group))
                            m_Manager.SetComponentData(group, globalIlluminationLod);
                        else
                            m_Manager.AddComponentData(group, globalIlluminationLod);
                        log.Add("GI LOD " + globalIlluminationLod.Value);
                        continue;
                    }

                    if (change == 3)
                    {
                        ToggleComponent<MeshLODGroupComponent>(group);
                        log.Add("toggle group component");
                        continue;
                    }

                    if (change == 4)
                    {
                        ToggleComponent<GlobalIlluminationLOD>(group);
                        log.Add("toggle GI LOD component");
                        continue;
                    }

                    if (change == 2)
                    {
                        var spawned = m_Manager.Instantiate(prototype);
                        if (random.NextBool())
                            m_Manager.AddComponent<OtherChunkTag>(spawned);
                        if (random.NextBool())
                            m_Manager.AddComponentData(spawned, new MeshLODComponent { Group = group, LODMask = 1 << 0 });
                        live.Add(spawned);
                        log.Add("spawn " + spawned);
                        continue;
                    }

                    if (live.Count == 0)
                        continue;

                    int index = random.NextInt(live.Count);
                    var entity = live[index];
                    switch (change)
                    {
                        case 5:
                            if (!m_Manager.HasComponent<LocalToWorld>(entity))
                                break;
                            step++;
                            m_Manager.SetComponentData(entity, new LocalToWorld { Value = float4x4.Translate(new float3(index, step, 0f)) });
                            log.Add("move " + entity);
                            break;

                        case 6:
                            if (!m_Manager.HasComponent<MaterialMeshInfo>(entity))
                                break;
                            m_Manager.SetComponentData(entity, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
                            log.Add("dirty " + entity);
                            break;

                        case 7:
                            ToggleComponent<DisableRendering>(entity);
                            log.Add("toggle hidden " + entity);
                            break;

                        case 8:
                            ToggleComponent<DeformedMeshIndex>(entity);
                            log.Add("toggle deformed " + entity);
                            break;

                        case 9:
                            if (m_Manager.HasComponent<MeshLODComponent>(entity))
                                m_Manager.RemoveComponent<MeshLODComponent>(entity);
                            else
                                m_Manager.AddComponentData(entity, new MeshLODComponent { Group = group, LODMask = 1 << 0 });
                            log.Add("toggle LOD membership " + entity);
                            break;

                        case 10:
                            if (m_Manager.HasComponent<MaterialMeshInfo>(entity))
                                m_Manager.RemoveComponent<MaterialMeshInfo>(entity);
                            else
                                m_Manager.AddComponentData(entity, MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));
                            log.Add("toggle MaterialMeshInfo " + entity);
                            break;

                        case 11:
                            if (m_Manager.HasComponent<LocalToWorld>(entity))
                            {
                                m_Manager.RemoveComponent<LocalToWorld>(entity);
                            }
                            else
                            {
                                step++;
                                m_Manager.AddComponentData(entity, new LocalToWorld { Value = float4x4.Translate(new float3(index, step, 0f)) });
                            }

                            log.Add("toggle LocalToWorld " + entity);
                            break;

                        default:
                            m_Manager.DestroyEntity(entity);
                            live[index] = live[live.Count - 1];
                            live.RemoveAt(live.Count - 1);
                            log.Add("destroy " + entity);
                            break;
                    }
                }

                int consumerAction = random.NextInt(20);
                if (consumerAction == 19)
                {
                    SurfaceCacheEntityChangeBridge.UnregisterConsumer();
                    Assert.IsTrue(SurfaceCacheEntityChangeBridge.TryRegisterConsumer());
                    model.Clear();
                    log.Add("re-register");
                }

                m_System.Update();

                if (consumerAction >= 16 && consumerAction < 19)
                {
                    BeginAndAbortDrain();
                    log.Add("abort drain");
                }
                else if (consumerAction >= 12)
                {
                    string context = $"seed {seed}, changes since the last drain: {string.Join(", ", log)}";
                    DrainIntoModel(model, context);
                    AssertModelMatchesEntities(model, live, context);
                    log.Clear();
                }
            }

            m_System.Update();
            DrainIntoModel(model, $"seed {seed}, final drain");
            AssertModelMatchesEntities(model, live, $"seed {seed}, final drain");
        }

        void ToggleComponent<T>(Entity entity) where T : unmanaged, IComponentData
        {
            if (m_Manager.HasComponent<T>(entity))
                m_Manager.RemoveComponent<T>(entity);
            else
                m_Manager.AddComponent<T>(entity);
        }

        // Applies a drain in the order SurfaceCacheWorldAdapter does: transforms, then destroys, then records.
        static void DrainIntoModel(Dictionary<EntityId, SurfaceCacheEntityInstanceRecord> model, string context)
        {
            SurfaceCacheEntityChangeBridge.BeginDrain(out var changed, out _, out _, out var destroyed, out var transforms);
            try
            {
                var changedKeys = new HashSet<EntityId>();
                foreach (var record in changed)
                    Assert.IsTrue(changedKeys.Add(record.Key), $"{context}: a key should have at most one record per drain.");

                foreach (var transform in transforms)
                {
                    Assert.IsFalse(changedKeys.Contains(transform.Key), $"{context}: a transform should be folded into its key's record.");
                    Assert.IsTrue(model.TryGetValue(transform.Key, out var known), $"{context}: a transform should only arrive for a delivered key.");
                    known.LocalToWorld = transform.LocalToWorld;
                    model[transform.Key] = known;
                }

                foreach (var key in destroyed)
                    model.Remove(key);

                foreach (var record in changed)
                    model[record.Key] = record;
            }
            finally
            {
                SurfaceCacheEntityChangeBridge.EndDrain();
            }
        }

        void AssertModelMatchesEntities(Dictionary<EntityId, SurfaceCacheEntityInstanceRecord> model, List<Entity> live, string context)
        {
            var tracked = live
                .Where(entity => m_Manager.HasComponent<MaterialMeshInfo>(entity)
                    && m_Manager.HasComponent<LocalToWorld>(entity)
                    && !m_Manager.HasComponent<DeformedMeshIndex>(entity))
                .ToList();
            Assert.AreEqual(tracked.Count, model.Count, $"{context}: the consumer should know exactly the tracked instances.");

            foreach (var entity in tracked)
            {
                Assert.IsTrue(model.TryGetValue(MakeKey(entity), out var record), $"{context}: {entity} is missing.");
                Matrix4x4 localToWorld = m_Manager.GetComponentData<LocalToWorld>(entity).Value;
                Assert.AreEqual(localToWorld, record.LocalToWorld, $"{context}: {entity} has a stale transform.");
                Assert.AreEqual(IsExpectedVisible(entity), record.Visible, $"{context}: {entity} has a stale visibility.");
                Assert.AreEqual(m_Mesh.GetEntityId(), record.Mesh, $"{context}: {entity} has the wrong mesh.");
            }
        }

        // Written out rather than calling the production selection, so the two can disagree.
        bool IsExpectedVisible(Entity entity)
        {
            if (m_Manager.HasComponent<DisableRendering>(entity))
                return false;

            if (!m_Manager.HasComponent<MeshLODComponent>(entity))
                return true;

            var membership = m_Manager.GetComponentData<MeshLODComponent>(entity);
            if (!m_Manager.HasComponent<MeshLODGroupComponent>(membership.Group))
                return true;

            if (!m_Manager.HasComponent<GlobalIlluminationLOD>(membership.Group))
                return false;

            int level = m_Manager.GetComponentData<GlobalIlluminationLOD>(membership.Group).Value;
            return level >= 0 && level < k_SequenceLodLevelCount && (membership.LODMask & (1 << level)) != 0;
        }
    }
}

#endif
