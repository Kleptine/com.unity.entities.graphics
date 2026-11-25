#if URP_10_0_0_OR_NEWER && (SURFACE_CACHE_SUPPORTED || UNITY_EDITOR)

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine.Rendering.Universal;

namespace Unity.Entities.Graphics.Tests.SurfaceCache
{
    internal partial class SurfaceCacheProducerTests
    {
        const float k_CullingDistance = 100.0f;
        static readonly float3 k_FarPosition = new float3(1000.0f, 0.0f, 0.0f);

        [Test]
        public void Culling_NoObservers_FarEntityIsVisible()
        {
            CreateRenderableEntityWithBounds(k_FarPosition);

            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count);
            Assert.IsTrue(m_Changed[0].Visible, "Without observers nothing should be culled.");
            Assert.AreEqual(0, SurfaceCacheEntityChangeBridge.CulledEntityCount);
        }

        [Test]
        public void Culling_NewEntityOutOfRange_InvisibleRecordIsPushedAndCounted()
        {
            SetObservers(float3.zero);
            var near = CreateRenderableEntityWithBounds(float3.zero);
            var far = CreateRenderableEntityWithBounds(k_FarPosition);

            UpdateAndDrain();

            Assert.IsTrue(RecordFor(near).Visible, "An entity next to the observer should be visible.");
            Assert.IsFalse(RecordFor(far).Visible, "An entity beyond the culling distance should be culled.");
            Assert.IsFalse(m_Manager.IsComponentEnabled<SurfaceCacheExtracted>(far));
            Assert.IsTrue(m_Manager.IsComponentEnabled<SurfaceCacheExtracted>(near));
            Assert.AreEqual(1, SurfaceCacheEntityChangeBridge.CulledEntityCount);
            Assert.AreEqual(2, SurfaceCacheEntityChangeBridge.TrackedEntityCount);
        }

        [Test]
        public void Culling_StateUnchanged_NothingIsPushed()
        {
            SetObservers(float3.zero);
            CreateRenderableEntityWithBounds(float3.zero);
            CreateRenderableEntityWithBounds(k_FarPosition);
            UpdateAndDrain();

            UpdateAndDrain();

            Assert.AreEqual(0, m_Changed.Count, "Entities that keep their culling state should not be pushed again.");
        }

        [Test]
        public void Culling_ObserverMoves_OnlyEntitiesThatCrossTheBoundaryArePushed()
        {
            SetObservers(float3.zero);
            var near = CreateRenderableEntityWithBounds(float3.zero);
            var far = CreateRenderableEntityWithBounds(k_FarPosition);
            UpdateAndDrain();

            SetObservers(k_FarPosition);
            UpdateAndDrain();

            Assert.AreEqual(2, m_Changed.Count);
            Assert.IsFalse(RecordFor(near).Visible, "The entity the observer left should be culled.");
            Assert.IsTrue(RecordFor(far).Visible, "The entity the observer reached should be visible.");
            AssertResolvesToTestAssets(RecordFor(far));
        }

        [Test]
        public void Culling_EntityVisibleToAnyObserver_IsVisible()
        {
            SetObservers(float3.zero, k_FarPosition);
            CreateRenderableEntityWithBounds(float3.zero);
            CreateRenderableEntityWithBounds(k_FarPosition);

            UpdateAndDrain();

            Assert.AreEqual(2, m_Changed.Count);
            Assert.IsTrue(m_Changed[0].Visible && m_Changed[1].Visible, "Each entity is near one of the observers.");
        }

        [Test]
        public void Culling_EntityCoversTooSmallAnAngle_IsCulled()
        {
            SetObservers(float3.zero, minAngleDegrees: 5.0f);
            var close = CreateRenderableEntityWithBounds(new float3(5.0f, 0.0f, 0.0f));
            var distant = CreateRenderableEntityWithBounds(new float3(50.0f, 0.0f, 0.0f));

            UpdateAndDrain();

            Assert.IsTrue(RecordFor(close).Visible, "A unit cube 5 m away covers about 20 degrees.");
            Assert.IsFalse(RecordFor(distant).Visible, "A unit cube 50 m away covers about 2 degrees.");
        }

        [Test]
        public void Culling_OnlyDistanceChanges_EntityIsPushedOnceEachTimeItCrossesTheRange()
        {
            var position = new float3(300.0f, 0.0f, 0.0f);
            SetObservers(MakeObserver(float3.zero, maxDistance: 100.0f));
            var entity = CreateRenderableEntityWithBounds(position);
            UpdateAndDrain();
            Assert.IsFalse(RecordFor(entity).Visible, "A unit cube 300 m away is beyond a 100 m culling distance.");

            SetObservers(MakeObserver(float3.zero, maxDistance: 500.0f));
            UpdateAndDrain();
            Assert.AreEqual(1, m_Changed.Count);
            Assert.IsTrue(RecordFor(entity).Visible, "Raising the culling distance past the entity should bring it back.");
            Assert.IsTrue(m_Manager.IsComponentEnabled<SurfaceCacheExtracted>(entity));

            UpdateAndDrain();
            Assert.AreEqual(0, m_Changed.Count, "The entity should only be pushed back once.");

            SetObservers(MakeObserver(float3.zero, maxDistance: 100.0f));
            UpdateAndDrain();
            Assert.AreEqual(1, m_Changed.Count);
            Assert.IsFalse(RecordFor(entity).Visible, "Lowering the culling distance again should cull it.");
            Assert.AreEqual(1, SurfaceCacheEntityChangeBridge.CulledEntityCount);
        }

        [Test]
        public void Culling_OnlyMinAngleChanges_EntityIsPushedOnceEachTimeItCrossesTheThreshold()
        {
            SetObservers(float3.zero, minAngleDegrees: 5.0f);
            var entity = CreateRenderableEntityWithBounds(new float3(50.0f, 0.0f, 0.0f));
            UpdateAndDrain();
            Assert.IsFalse(RecordFor(entity).Visible, "A unit cube 50 m away covers about 2 degrees.");

            SetObservers(float3.zero, minAngleDegrees: 1.0f);
            UpdateAndDrain();
            Assert.AreEqual(1, m_Changed.Count);
            Assert.IsTrue(RecordFor(entity).Visible, "Lowering the minimum angle below the entity's should bring it back.");

            UpdateAndDrain();
            Assert.AreEqual(0, m_Changed.Count, "The entity should only be pushed back once.");

            SetObservers(float3.zero, minAngleDegrees: 5.0f);
            UpdateAndDrain();
            Assert.AreEqual(1, m_Changed.Count);
            Assert.IsFalse(RecordFor(entity).Visible, "Raising the minimum angle again should cull it.");
        }

        [Test]
        public void Culling_ScreenSizeObserverResolutionChanges_EntityIsCulledOnlyAtTheLowerResolution()
        {
            var entity = CreateRenderableEntityWithBounds(new float3(50.0f, 0.0f, 0.0f));
            SetObservers(MakeScreenSizeObserver(60.0f, 1080));
            UpdateAndDrain();
            Assert.IsTrue(RecordFor(entity).Visible, "A unit cube 50 m away covers about 32 pixels at 1080p with a 60 degree field of view.");

            SetObservers(MakeScreenSizeObserver(60.0f, 360));
            UpdateAndDrain();
            Assert.AreEqual(1, m_Changed.Count);
            Assert.IsFalse(RecordFor(entity).Visible, "At 360p the same cube covers about 11 pixels, below the 16 pixel minimum.");

            SetObservers(MakeScreenSizeObserver(60.0f, 1080));
            UpdateAndDrain();
            Assert.AreEqual(1, m_Changed.Count);
            Assert.IsTrue(RecordFor(entity).Visible, "Going back to 1080p should bring the cube back.");
        }

        [Test]
        public void Culling_ScreenSizeObserverFieldOfViewChanges_EntityIsCulledOnlyAtTheWiderFieldOfView()
        {
            var entity = CreateRenderableEntityWithBounds(new float3(50.0f, 0.0f, 0.0f));
            SetObservers(MakeScreenSizeObserver(60.0f, 1080));
            UpdateAndDrain();
            Assert.IsTrue(RecordFor(entity).Visible);

            SetObservers(MakeScreenSizeObserver(120.0f, 1080));
            UpdateAndDrain();
            Assert.AreEqual(1, m_Changed.Count);
            Assert.IsFalse(RecordFor(entity).Visible, "With a 120 degree field of view the cube covers about 11 pixels, below the 16 pixel minimum.");

            SetObservers(MakeScreenSizeObserver(30.0f, 1080));
            UpdateAndDrain();
            Assert.AreEqual(1, m_Changed.Count);
            Assert.IsTrue(RecordFor(entity).Visible, "Zooming in to 30 degrees should bring the cube back.");
        }

        [Test]
        public void Culling_Statistics_CountChunksOnlyWhenAllTheirEntitiesAreCulled()
        {
            SetObservers(float3.zero);
            var mixedChunkNear = CreateRenderableEntityWithBounds(float3.zero);
            var mixedChunkFar = CreateRenderableEntityWithBounds(k_FarPosition);
            for (int i = 0; i < 2; i++)
            {
                var far = CreateRenderableEntityWithBounds(k_FarPosition + new float3(i + 1, 0.0f, 0.0f));
                m_Manager.AddComponent<OtherChunkTag>(far);
            }
            Assume.That(m_Manager.GetChunk(mixedChunkNear), Is.EqualTo(m_Manager.GetChunk(mixedChunkFar)));

            UpdateAndDrain();

            Assert.AreEqual(4, SurfaceCacheEntityChangeBridge.TrackedEntityCount);
            Assert.AreEqual(3, SurfaceCacheEntityChangeBridge.CulledEntityCount);
            Assert.AreEqual(2, SurfaceCacheEntityChangeBridge.TrackedChunkCount);
            Assert.AreEqual(1, SurfaceCacheEntityChangeBridge.CulledChunkCount, "Only the chunk with no entity in range is culled.");

            SetObservers(k_FarPosition);
            UpdateAndDrain();

            Assert.AreEqual(1, SurfaceCacheEntityChangeBridge.CulledEntityCount);
            Assert.AreEqual(0, SurfaceCacheEntityChangeBridge.CulledChunkCount, "Both chunks now keep at least one entity.");

            SurfaceCacheEntityChangeBridge.SetCullingObservers(default);
            UpdateAndDrain();

            Assert.AreEqual(0, SurfaceCacheEntityChangeBridge.CulledEntityCount);
            Assert.AreEqual(0, SurfaceCacheEntityChangeBridge.CulledChunkCount);
            Assert.AreEqual(2, SurfaceCacheEntityChangeBridge.TrackedChunkCount);
        }

        [Test]
        public void Culling_ObserversCleared_CulledEntitiesArePushedVisibleOnce()
        {
            SetObservers(float3.zero);
            var far = CreateRenderableEntityWithBounds(k_FarPosition);
            UpdateAndDrain();

            SurfaceCacheEntityChangeBridge.SetCullingObservers(default);
            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count);
            Assert.IsTrue(RecordFor(far).Visible, "Clearing the observers should bring culled entities back.");

            UpdateAndDrain();
            Assert.AreEqual(0, m_Changed.Count, "The entity should only be pushed back once.");
        }

        [Test]
        public void Culling_EntityWithoutBounds_IsNeverCulled()
        {
            SetObservers(k_FarPosition);
            CreateRenderableEntity();

            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count);
            Assert.IsTrue(m_Changed[0].Visible, "Without bounds there is nothing to cull against.");
        }

        [Test]
        public void Culling_InfiniteDistance_FarEntitiesAreCulledOnlyBySize()
        {
            var veryFarPosition = new float3(100000.0f, 0.0f, 0.0f);
            SetObservers(MakeObserver(float3.zero, 1.0f, float.PositiveInfinity));
            var large = CreateRenderableEntityWithBounds(veryFarPosition, 5000.0f);
            var small = CreateRenderableEntityWithBounds(veryFarPosition, 0.5f);

            UpdateAndDrain();

            Assert.IsTrue(RecordFor(large).Visible, "An infinite distance should never cull by distance.");
            Assert.IsFalse(RecordFor(small).Visible, "Size culling should still apply with an infinite distance.");
        }

        [Test]
        public void Culling_InfiniteDistanceAndChunkFarAway_EveryEntityIsVisible()
        {
            SetObservers(MakeObserver(float3.zero, 0.0f, float.PositiveInfinity));
            var entities = new Entity[8];
            for (int i = 0; i < entities.Length; i++)
            {
                entities[i] = CreateRenderableEntityWithBounds(k_FarPosition + new float3(i, 0.0f, 0.0f));
                m_Manager.AddChunkComponentData<ChunkWorldRenderBounds>(entities[i]);
            }
            var chunk = m_Manager.GetChunk(entities[0]);
            m_Manager.SetChunkComponentData(chunk, new ChunkWorldRenderBounds
            {
                Value = new AABB
                {
                    Center = k_FarPosition + new float3((entities.Length - 1) * 0.5f, 0.0f, 0.0f),
                    Extents = new float3(entities.Length * 0.5f, 0.5f, 0.5f),
                },
            });

            UpdateAndDrain();

            Assert.AreEqual(entities.Length, m_Changed.Count);
            foreach (var record in m_Changed)
                Assert.IsTrue(record.Visible, "The chunk bounds should not reject a chunk when the distance is infinite.");
        }

        [Test]
        public void Culling_ChunkOutOfRange_EveryEntityIsCulled()
        {
            SetObservers(float3.zero);
            var entities = new Entity[8];
            for (int i = 0; i < entities.Length; i++)
            {
                entities[i] = CreateRenderableEntityWithBounds(k_FarPosition + new float3(i, 0.0f, 0.0f));
                m_Manager.AddChunkComponentData<ChunkWorldRenderBounds>(entities[i]);
            }
            var chunk = m_Manager.GetChunk(entities[0]);
            m_Manager.SetChunkComponentData(chunk, new ChunkWorldRenderBounds
            {
                Value = new AABB
                {
                    Center = k_FarPosition + new float3((entities.Length - 1) * 0.5f, 0.0f, 0.0f),
                    Extents = new float3(entities.Length * 0.5f, 0.5f, 0.5f),
                },
            });

            UpdateAndDrain();

            Assert.AreEqual(entities.Length, m_Changed.Count);
            foreach (var record in m_Changed)
                Assert.IsFalse(record.Visible);
        }

        [Test]
        public void Culling_TransformMovesEntityIntoRange_EntityIsPushedVisible()
        {
            SetObservers(float3.zero);
            var entity = CreateRenderableEntityWithBounds(k_FarPosition);
            UpdateAndDrain();

            m_Manager.SetComponentData(entity, new LocalToWorld { Value = float4x4.identity });
            m_Manager.SetComponentData(entity, new WorldRenderBounds { Value = UnitBoundsAt(float3.zero) });
            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count);
            Assert.IsTrue(RecordFor(entity).Visible);
        }

        [Test]
        public void Culling_ObserverLeavesWhileAnotherStillSees_EntityIsNotPushed()
        {
            SetObservers(MakeObserver(float3.zero), MakeObserver(new float3(50.0f, 0.0f, 0.0f)));
            var entity = CreateRenderableEntityWithBounds(float3.zero);
            UpdateAndDrain();
            Assert.IsTrue(RecordFor(entity).Visible);

            SetObservers(MakeObserver(k_FarPosition), MakeObserver(new float3(50.0f, 0.0f, 0.0f)));
            UpdateAndDrain();

            Assert.AreEqual(0, m_Changed.Count, "The second observer still keeps the entity, so its state does not change.");
            Assert.IsTrue(m_Manager.IsComponentEnabled<SurfaceCacheExtracted>(entity));
        }

        [Test]
        public void Culling_ObserverRemoved_EntitiesOnlyItKeptAreCulled()
        {
            SetObservers(MakeObserver(float3.zero), MakeObserver(k_FarPosition));
            var near = CreateRenderableEntityWithBounds(float3.zero);
            var far = CreateRenderableEntityWithBounds(k_FarPosition);
            UpdateAndDrain();

            SetObservers(MakeObserver(float3.zero));
            UpdateAndDrain();

            Assert.AreEqual(1, m_Changed.Count, "Only the entity the removed observer kept should be pushed.");
            Assert.IsFalse(RecordFor(far).Visible);
            Assert.IsTrue(m_Manager.IsComponentEnabled<SurfaceCacheExtracted>(near));
            Assert.AreEqual(1, SurfaceCacheEntityChangeBridge.CulledEntityCount);
        }

        [Test]
        public void Culling_ObserversWithDifferentSettings_EachKeepsWhatItsOwnSettingsKeep()
        {
            SetObservers(
                MakeObserver(float3.zero, minAngleDegrees: 0.0f, maxDistance: 100.0f),
                MakeObserver(float3.zero, minAngleDegrees: 5.0f, maxDistance: 1000.0f));
            var nearAndSmall = CreateRenderableEntityWithBounds(new float3(50.0f, 0.0f, 0.0f));
            var farAndSmall = CreateRenderableEntityWithBounds(new float3(500.0f, 0.0f, 0.0f));
            var farAndLarge = CreateRenderableEntityWithBounds(new float3(0.0f, 0.0f, 500.0f), extents: 50.0f);
            var beyondBoth = CreateRenderableEntityWithBounds(new float3(0.0f, 0.0f, 2000.0f), extents: 50.0f);

            UpdateAndDrain();

            Assert.IsTrue(RecordFor(nearAndSmall).Visible, "Within the first observer's distance, size does not matter.");
            Assert.IsFalse(RecordFor(farAndSmall).Visible, "Too far for the first observer and too small for the second.");
            Assert.IsTrue(RecordFor(farAndLarge).Visible, "Large enough for the second observer.");
            Assert.IsFalse(RecordFor(beyondBoth).Visible, "Beyond both observers' distances.");
        }

        [Test]
        public void Culling_ObserverStepsBackAndForthAcrossTheBoundary_EachCrossingPushesOnce()
        {
            var entity = CreateRenderableEntityWithBounds(new float3(150.0f, 0.0f, 0.0f));
            float[] observerSteps = { 0.0f, 20.0f, 40.0f, 60.0f, 80.0f, 100.0f, 80.0f, 60.0f, 40.0f, 20.0f, 0.0f, 60.0f };

            bool? wasVisible = null;
            foreach (float x in observerSteps)
            {
                SetObservers(MakeObserver(new float3(x, 0.0f, 0.0f)));
                UpdateAndDrain();

                bool isVisible = 149.5f - x <= k_CullingDistance;
                if (wasVisible == isVisible)
                {
                    Assert.AreEqual(0, m_Changed.Count, $"Observer at {x}: no crossing, so nothing should be pushed.");
                }
                else
                {
                    Assert.AreEqual(1, m_Changed.Count, $"Observer at {x}: the crossing should push exactly one record.");
                    Assert.AreEqual(isVisible, RecordFor(entity).Visible, $"Observer at {x}");
                }

                Assert.AreEqual(isVisible, m_Manager.IsComponentEnabled<SurfaceCacheExtracted>(entity), $"Observer at {x}");
                wasVisible = isVisible;
            }
        }

        [Test]
        public void Culling_ObserverMovesAcrossManyChunks_ExactlyTheCrossingEntitiesArePushed()
        {
            var positions = new Dictionary<Entity, float>();
            var entities = new Entity[k_ParallelCount];
            for (int i = 0; i < k_ParallelCount; i++)
            {
                entities[i] = CreateRenderableEntityWithBounds(new float3(i, 0.0f, 0.0f));
                positions.Add(entities[i], i);
            }
            foreach (int x in new[] { 0, 50, 4000, 4050 })
                positions.Add(CreateRuntimeIdTwin(entities[x]), x);

            SetObservers(MakeObserver(float3.zero));
            UpdateAndDrain();
            Assert.AreEqual(positions.Count, m_Changed.Count);
            foreach (var pair in positions)
                Assert.AreEqual(IsInRange(0.0f, pair.Value), RecordFor(pair.Key).Visible, $"Entity at {pair.Value}");

            SetObservers(MakeObserver(new float3(4000.0f, 0.0f, 0.0f)));
            UpdateAndDrain();

            var crossing = positions
                .Where(pair => IsInRange(0.0f, pair.Value) != IsInRange(4000.0f, pair.Value))
                .Select(pair => pair.Key)
                .ToList();
            AssertSameKeys(crossing, m_Changed.Select(record => record.Key), "Only entities that crossed the boundary should be pushed.");
            foreach (var entity in crossing)
                Assert.AreEqual(IsInRange(4000.0f, positions[entity]), RecordFor(entity).Visible, $"Entity at {positions[entity]}");

            int expectedCulled = positions.Values.Count(x => !IsInRange(4000.0f, x));
            Assert.AreEqual(expectedCulled, SurfaceCacheEntityChangeBridge.CulledEntityCount);
        }

        static bool IsInRange(float observerX, float entityX)
        {
            return math.abs(entityX - observerX) - 0.5f <= k_CullingDistance;
        }

        [Test]
        public void CullingStatistics_SecondWorldWithoutEntities_CountsAreSummedOverWorlds()
        {
            SetObservers(float3.zero);
            CreateRenderableEntityWithBounds(float3.zero);
            CreateRenderableEntityWithBounds(k_FarPosition);
            var otherWorld = new World("SurfaceCacheProducerTests Other");
            try
            {
                var otherSystem = otherWorld.GetOrCreateSystemManaged<SurfaceCacheMeshInstanceProducerSystem>();
                otherSystem.Enabled = true;

                m_System.Update();
                otherSystem.Update();
                Drain();

                Assert.AreEqual(2, SurfaceCacheEntityChangeBridge.TrackedEntityCount, "An empty world must not hide the counts of another world.");
                Assert.AreEqual(1, SurfaceCacheEntityChangeBridge.CulledEntityCount);

                otherWorld.Dispose();

                Assert.AreEqual(2, SurfaceCacheEntityChangeBridge.TrackedEntityCount, "Disposing one world must only remove its own counts.");
                Assert.AreEqual(1, SurfaceCacheEntityChangeBridge.CulledEntityCount);
            }
            finally
            {
                if (otherWorld.IsCreated)
                    otherWorld.Dispose();
            }
        }

        Entity CreateRenderableEntityWithBounds(float3 position, float extents = 0.5f)
        {
            var entity = CreateRenderableEntity();
            m_Manager.SetComponentData(entity, new LocalToWorld { Value = float4x4.Translate(position) });
            m_Manager.AddComponentData(entity, new WorldRenderBounds { Value = new AABB { Center = position, Extents = extents } });
            return entity;
        }

        static AABB UnitBoundsAt(float3 position)
        {
            return new AABB { Center = position, Extents = 0.5f };
        }

        static void SetObservers(float3 position, float minAngleDegrees = 0.0f)
        {
            SetObservers(MakeObserver(position, minAngleDegrees));
        }

        static void SetObservers(float3 first, float3 second)
        {
            SetObservers(MakeObserver(first), MakeObserver(second));
        }

        static void SetObservers(params SurfaceCacheCullingObserver[] observers)
        {
            SurfaceCacheEntityChangeBridge.SetCullingObservers(observers);
        }

        static SurfaceCacheCullingObserver MakeScreenSizeObserver(float verticalFieldOfViewDegrees, int pixelHeight)
        {
            return new SurfaceCacheCullingObserver
            {
                Position = float3.zero,
                MaxDistance = k_CullingDistance,
                MinAngle = SurfaceCacheGIRendererFeature.ScreenSizeToAngle(16.0f, verticalFieldOfViewDegrees, pixelHeight),
            };
        }

        static SurfaceCacheCullingObserver MakeObserver(float3 position, float minAngleDegrees = 0.0f, float maxDistance = k_CullingDistance)
        {
            return new SurfaceCacheCullingObserver
            {
                Position = position,
                MaxDistance = maxDistance,
                MinAngle = math.radians(minAngleDegrees),
            };
        }
    }
}

#endif
