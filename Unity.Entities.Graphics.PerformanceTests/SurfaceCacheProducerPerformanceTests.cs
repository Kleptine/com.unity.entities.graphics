#if URP_10_0_0_OR_NEWER

using NUnit.Framework;
using Unity.Collections;
using Unity.PerformanceTesting;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Unity.Entities.Graphics.PerformanceTests
{
    /// <summary>
    /// Measures the surface cache entity producer and the change bridge separately, so it is visible which of the
    /// two actually dominates and how each scales with instance count.
    /// </summary>
    /// <remarks>
    /// The interesting axis is what kind of change is reported. Transform-only changes are the high frequency case
    /// (anything moving reports every frame), whereas full records are only produced when an instance's mesh,
    /// materials, visibility or chunk layout changes. Measuring them apart is what tells us where to spend effort.
    ///
    /// No consumer world is built: these tests stop at the bridge, which is the ECS-side boundary.
    /// </remarks>
    [TestFixture]
    internal class SurfaceCacheProducerPerformanceTests
    {
        World m_World;
        EntityManager m_Manager;
        SurfaceCacheMeshInstanceProducerSystem m_System;
        EntityQuery m_Query;
        Mesh m_Mesh;
        Material m_Material;

        const int k_Iterations = 10;
        const int k_WarmupIterations = 10;

        [SetUp]
        public void SetUp()
        {
            Assume.That(!SurfaceCacheEntityChangeBridge.IsConsumerPresent,
                "These tests require exclusive ownership of the SurfaceCacheEntityChangeBridge.");

            m_World = new World("SurfaceCacheProducerPerformanceTests");
            m_Manager = m_World.EntityManager;
            m_System = m_World.GetOrCreateSystemManaged<SurfaceCacheMeshInstanceProducerSystem>();
            m_System.Enabled = true;

            m_Mesh = new Mesh { name = "Perf Mesh" };
            m_Mesh.vertices = new[] { Vector3.zero, Vector3.up, Vector3.right };
            m_Mesh.triangles = new[] { 0, 1, 2 };
            m_Material = new Material(Shader.Find("Hidden/InternalErrorShader")) { name = "Perf Material" };

            Assert.IsTrue(SurfaceCacheEntityChangeBridge.TryRegisterConsumer());
        }

        [TearDown]
        public void TearDown()
        {
            SurfaceCacheEntityChangeBridge.UnregisterConsumer();

            if (m_World != null && m_World.IsCreated)
                m_World.Dispose();

            Object.DestroyImmediate(m_Mesh);
            Object.DestroyImmediate(m_Material);

        }

        /// <summary>
        /// The high frequency path: every instance moves every frame, so every instance produces a transform record.
        /// </summary>
        [Test, Performance]
        public void Producer_TransformOnlyChanges([Values(1000, 10000, 100000)] int instanceCount)
        {
            CreateInstances(instanceCount);

            // Settle: the first update reports every instance as new and adds the tracking component.
            UpdateAndDrain();
            UpdateAndDrain();

            var producerGroup = new SampleGroup("Producer transform-only", SampleUnit.Millisecond);
            var drainGroup = new SampleGroup("Bridge drain transform-only", SampleUnit.Millisecond);
            for (int i = 0; i < k_WarmupIterations + k_Iterations; i++)
            {
                DirtyAllTransforms();
                bool measured = i >= k_WarmupIterations;

                if (measured)
                {
                    using (Measure.Scope(producerGroup))
                        m_System.Update();
                }
                else
                {
                    m_System.Update();
                }

                int drained;
                if (measured)
                {
                    using (Measure.Scope(drainGroup))
                        drained = DrainAndCount();
                }
                else
                {
                    drained = DrainAndCount();
                }

                Assert.AreEqual(instanceCount, drained,
                    "Every instance moved, so every instance should produce a transform record.");
            }
        }

        /// <summary>
        /// The full record path: dirtying MaterialMeshInfo forces mesh and material resolution for every instance.
        /// </summary>
        [Test, Performance]
        public void Producer_FullRecordChanges([Values(1000, 10000, 100000)] int instanceCount)
        {
            CreateInstances(instanceCount);

            UpdateAndDrain();
            UpdateAndDrain();

            var producerGroup = new SampleGroup("Producer full records", SampleUnit.Millisecond);
            var drainGroup = new SampleGroup("Bridge drain full records", SampleUnit.Millisecond);
            for (int i = 0; i < k_WarmupIterations + k_Iterations; i++)
            {
                DirtyAllMaterialMeshInfos();
                bool measured = i >= k_WarmupIterations;

                if (measured)
                {
                    using (Measure.Scope(producerGroup))
                        m_System.Update();
                }
                else
                {
                    m_System.Update();
                }

                int drained;
                if (measured)
                {
                    using (Measure.Scope(drainGroup))
                        drained = DrainAndCount();
                }
                else
                {
                    drained = DrainAndCount();
                }

                Assert.AreEqual(instanceCount, drained,
                    "Every instance changed, so every instance should produce a full record.");
            }
        }

        /// <summary>
        /// Steady state: nothing changed, so change version filtering should make this nearly free. This is the
        /// control for the other two.
        /// </summary>
        [Test, Performance]
        public void Producer_NoChanges([Values(1000, 10000, 100000)] int instanceCount)
        {
            CreateInstances(instanceCount);

            UpdateAndDrain();
            UpdateAndDrain();

            var producerGroup = new SampleGroup("Producer idle", SampleUnit.Millisecond);

            for (int i = 0; i < k_WarmupIterations; i++)
                UpdateAndDrain();

            for (int i = 0; i < k_Iterations; i++)
            {
                using (Measure.Scope(producerGroup))
                    m_System.Update();

                Assert.AreEqual(0, DrainAndCount(), "Nothing changed, so nothing should have been produced.");
            }
        }

        /// <summary>
        /// One-off cost of every instance entering the cache, which is also what a consumer resync replays.
        /// </summary>
        [Test, Performance]
        public void Producer_NewInstances([Values(1000, 10000, 100000)] int instanceCount)
        {
            var producerGroup = new SampleGroup("Producer new instances", SampleUnit.Millisecond);

            CreateInstances(instanceCount);

            using (Measure.Scope(producerGroup))
                m_System.Update();

            Assert.AreEqual(instanceCount, DrainAndCount(),
                "Every new instance should produce a full record.");
        }

        /// <summary>
        /// Repeated streaming: the same number of instances enter again after earlier batches left, so the bridge's
        /// storage has already grown and only the per-instance cost remains.
        /// </summary>
        [Test, Performance]
        public void Producer_NewInstancesWarm([Values(1000, 10000, 100000)] int instanceCount)
        {
            var producerGroup = new SampleGroup("Producer new instances (warm)", SampleUnit.Millisecond);
            var drainGroup = new SampleGroup("Bridge drain new instances (warm)", SampleUnit.Millisecond);

            for (int i = 0; i < k_WarmupIterations + k_Iterations; i++)
            {
                CreateInstances(instanceCount);
                bool measured = i >= k_WarmupIterations;

                if (measured)
                {
                    using (Measure.Scope(producerGroup))
                        m_System.Update();
                }
                else
                {
                    m_System.Update();
                }

                int drained;
                if (measured)
                {
                    using (Measure.Scope(drainGroup))
                        drained = DrainAndCount();
                }
                else
                {
                    drained = DrainAndCount();
                }

                Assert.AreEqual(instanceCount, drained, "Every new instance should produce a full record.");

                m_Manager.DestroyEntity(m_Query);
                UpdateAndDrain();
            }
        }

        /// <summary>
        /// Streaming while things move: a small batch of instances enters in the same update as every tracked
        /// instance moves, so tracking the batch and the transform scan share an update.
        /// </summary>
        [Test, Performance]
        public void Producer_MovesWithNewInstances([Values(10000, 100000)] int instanceCount)
        {
            const int newPerUpdate = 100;

            CreateInstances(instanceCount);
            UpdateAndDrain();
            UpdateAndDrain();

            var prefab = m_Manager.Instantiate(m_Query.ToEntityArray(Allocator.Temp)[0]);
            m_Manager.AddComponent<Prefab>(prefab);

            var producerGroup = new SampleGroup("Producer moves with new instances", SampleUnit.Millisecond);
            var drainGroup = new SampleGroup("Bridge drain moves with new instances", SampleUnit.Millisecond);
            for (int i = 0; i < k_WarmupIterations + k_Iterations; i++)
            {
                DirtyAllTransforms();
                var added = m_Manager.Instantiate(prefab, newPerUpdate, Allocator.Temp);
                bool measured = i >= k_WarmupIterations;

                if (measured)
                {
                    using (Measure.Scope(producerGroup))
                        m_System.Update();
                }
                else
                {
                    m_System.Update();
                }

                int changedCount, transformCount;
                if (measured)
                {
                    using (Measure.Scope(drainGroup))
                        DrainCounts(out changedCount, out transformCount);
                }
                else
                {
                    DrainCounts(out changedCount, out transformCount);
                }

                Assert.AreEqual(newPerUpdate, changedCount, "Every new instance should produce a full record.");
                Assert.AreEqual(instanceCount, transformCount, "Every moved instance should produce a transform record.");

                m_Manager.DestroyEntity(added);
                added.Dispose();
                UpdateAndDrain();
            }
        }

        static void DrainCounts(out int changedCount, out int transformCount)
        {
            SurfaceCacheEntityChangeBridge.BeginDrain(out var changed, out _, out _, out _, out var transforms);
            changedCount = changed.Length;
            transformCount = transforms.Length;
            SurfaceCacheEntityChangeBridge.EndDrain();
        }

        void CreateInstances(int instanceCount)
        {
            var archetype = m_Manager.CreateArchetype(
                typeof(LocalToWorld), typeof(MaterialMeshInfo), typeof(RenderMeshArray));
            m_Manager.CreateEntity(archetype, instanceCount, Allocator.Temp).Dispose();

            m_Query = m_Manager.CreateEntityQuery(typeof(MaterialMeshInfo), typeof(LocalToWorld));

            // One shared RenderMeshArray for the whole set, which is the normal subscene shape.
            m_Manager.SetSharedComponent(m_Query, new RenderMeshArray(new[] { m_Material }, new[] { m_Mesh }));

            var materialMeshInfos = new NativeArray<MaterialMeshInfo>(instanceCount, Allocator.Temp);
            var value = MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0);
            for (int i = 0; i < instanceCount; i++)
                materialMeshInfos[i] = value;

            m_Query.CopyFromComponentDataArray(materialMeshInfos);
            materialMeshInfos.Dispose();

            DirtyAllTransforms();
        }

        // Bulk writes so the setup cost stays outside the measured scope as much as possible: this is one memcpy
        // per chunk plus a change version bump, rather than a per-entity managed call.
        void DirtyAllTransforms()
        {
            int count = m_Query.CalculateEntityCount();
            var values = new NativeArray<LocalToWorld>(count, Allocator.Temp);
            var transform = new LocalToWorld { Value = Unity.Mathematics.float4x4.identity };
            for (int i = 0; i < count; i++)
                values[i] = transform;

            m_Query.CopyFromComponentDataArray(values);
            values.Dispose();
        }

        void DirtyAllMaterialMeshInfos()
        {
            int count = m_Query.CalculateEntityCount();
            var values = new NativeArray<MaterialMeshInfo>(count, Allocator.Temp);
            var value = MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0);
            for (int i = 0; i < count; i++)
                values[i] = value;

            m_Query.CopyFromComponentDataArray(values);
            values.Dispose();
        }

        void UpdateAndDrain()
        {
            m_System.Update();
            DrainAndCount();
        }

        int DrainAndCount()
        {
            SurfaceCacheEntityChangeBridge.BeginDrain(out var changed, out _, out _, out var destroyed, out var transforms);
            int count = changed.Length + destroyed.Length + transforms.Length;
            SurfaceCacheEntityChangeBridge.EndDrain();
            return count;
        }

    }
}

#endif
