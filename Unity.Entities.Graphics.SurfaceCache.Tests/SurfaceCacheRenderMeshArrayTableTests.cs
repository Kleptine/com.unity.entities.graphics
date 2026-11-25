#if URP_10_0_0_OR_NEWER && (SURFACE_CACHE_SUPPORTED || UNITY_EDITOR)

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Rendering;
using UnityEngine;

namespace Unity.Entities.Graphics.Tests.SurfaceCache
{
    internal class SurfaceCacheRenderMeshArrayTableTests
    {
        World m_World;
        EntityManager m_Manager;
        SurfaceCacheRenderMeshArrayTable m_Table;
        readonly List<Object> m_Assets = new();

        [SetUp]
        public void SetUp()
        {
            m_World = new World("SurfaceCacheRenderMeshArrayTableTests");
            m_Manager = m_World.EntityManager;
            m_Table = SurfaceCacheRenderMeshArrayTable.Create();
        }

        [TearDown]
        public void TearDown()
        {
            m_Table.Dispose();

            if (m_World != null && m_World.IsCreated)
                m_World.Dispose();

            foreach (var asset in m_Assets)
                Object.DestroyImmediate(asset);
            m_Assets.Clear();
        }

        [Test]
        public void SingleMeshAndMaterial_ResolvesToSameAssetsAsRenderMeshArray()
        {
            var mesh = CreateMesh();
            var material = CreateMaterial();
            var entity = CreateRenderableEntity(new RenderMeshArray(new[] { material }, new[] { mesh }),
                MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

            m_Table.Update(m_Manager);

            var entry = GetEntry(entity);
            var materialMeshInfo = m_Manager.GetComponentData<MaterialMeshInfo>(entity);
            var renderMeshArray = m_Manager.GetSharedComponent<RenderMeshArray>(entity);

            Assert.AreEqual(renderMeshArray.GetMesh(materialMeshInfo).GetEntityId(),
                SurfaceCacheRenderMeshArrayTable.GetMesh(entry, materialMeshInfo),
                "The table should resolve the same mesh as RenderMeshArray.GetMesh.");
            Assert.AreEqual(renderMeshArray.GetMaterial(materialMeshInfo).GetEntityId(),
                SurfaceCacheRenderMeshArrayTable.GetMaterial(entry, materialMeshInfo),
                "The table should resolve the same material as RenderMeshArray.GetMaterial.");
        }

        [Test]
        public void MultipleMeshesAndMaterials_ResolveByArrayIndex()
        {
            var meshA = CreateMesh();
            var meshB = CreateMesh();
            var materialA = CreateMaterial();
            var materialB = CreateMaterial();
            var renderMeshArray = new RenderMeshArray(new[] { materialA, materialB }, new[] { meshA, meshB });

            // Second mesh, second material.
            var entity = CreateRenderableEntity(renderMeshArray, MaterialMeshInfo.FromRenderMeshArrayIndices(1, 1));

            m_Table.Update(m_Manager);

            var entry = GetEntry(entity);
            var materialMeshInfo = m_Manager.GetComponentData<MaterialMeshInfo>(entity);

            Assert.AreEqual(meshB.GetEntityId(), SurfaceCacheRenderMeshArrayTable.GetMesh(entry, materialMeshInfo));
            Assert.AreEqual(materialB.GetEntityId(), SurfaceCacheRenderMeshArrayTable.GetMaterial(entry, materialMeshInfo));
        }

        [Test]
        public void MaterialMeshIndexRange_ExposesOneEntryPerSubMesh()
        {
            var mesh = CreateMesh();
            var materialA = CreateMaterial();
            var materialB = CreateMaterial();
            var indices = new[]
            {
                new MaterialMeshIndex { MaterialIndex = 0, MeshIndex = 0, SubMeshIndex = 0 },
                new MaterialMeshIndex { MaterialIndex = 1, MeshIndex = 0, SubMeshIndex = 1 },
            };
            var renderMeshArray = new RenderMeshArray(new[] { materialA, materialB }, new[] { mesh }, indices);
            var entity = CreateRenderableEntity(renderMeshArray, MaterialMeshInfo.FromMaterialMeshIndexRange(0, 2));

            m_Table.Update(m_Manager);

            var entry = GetEntry(entity);
            var materialMeshInfo = m_Manager.GetComponentData<MaterialMeshInfo>(entity);

            Assert.AreEqual(2, entry.MaterialMeshIndices.Length, "The range entries should be mirrored verbatim.");
            Assert.AreEqual(mesh.GetEntityId(), SurfaceCacheRenderMeshArrayTable.GetMesh(entry, materialMeshInfo),
                "A ranged instance should resolve to the first mesh of the range, like RenderMeshArray does.");
            Assert.AreEqual(materialA.GetEntityId(), entry.Materials[entry.MaterialMeshIndices[0].MaterialIndex]);
            Assert.AreEqual(materialB.GetEntityId(), entry.Materials[entry.MaterialMeshIndices[1].MaterialIndex]);
        }

        [Test]
        public void ContentsChanged_EntryIsRebuilt()
        {
            var mesh = CreateMesh();
            var materialA = CreateMaterial();
            var materialB = CreateMaterial();
            var entity = CreateRenderableEntity(new RenderMeshArray(new[] { materialA }, new[] { mesh }),
                MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

            m_Table.Update(m_Manager);
            var materialMeshInfo = m_Manager.GetComponentData<MaterialMeshInfo>(entity);
            Assert.AreEqual(materialA.GetEntityId(),
                SurfaceCacheRenderMeshArrayTable.GetMaterial(GetEntry(entity), materialMeshInfo));

            m_Manager.SetSharedComponent(entity, new RenderMeshArray(new[] { materialB }, new[] { mesh }));
            m_Table.Update(m_Manager);

            Assert.AreEqual(materialB.GetEntityId(),
                SurfaceCacheRenderMeshArrayTable.GetMaterial(GetEntry(entity), materialMeshInfo),
                "A changed RenderMeshArray should be rebuilt rather than served from the stale entry.");
        }

        [Test]
        public void RecycledSharedIndexWithNewContents_EntryIsRebuilt()
        {
            var mesh = CreateMesh();
            var materialA = CreateMaterial();
            var materialB = CreateMaterial();
            var materialMeshInfo = MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0);

            var entityA = CreateRenderableEntity(new RenderMeshArray(new[] { materialA }, new[] { mesh }), materialMeshInfo);
            int sharedIndexA = m_Manager.GetSharedComponentIndex<RenderMeshArray>(entityA);
            m_Table.Update(m_Manager);

            // No update in between, so the table still holds A's entry under the index B is about to reuse.
            m_Manager.DestroyEntity(entityA);
            var entityB = CreateRenderableEntity(new RenderMeshArray(new[] { materialB }, new[] { mesh }), materialMeshInfo);
            Assert.AreEqual(sharedIndexA, m_Manager.GetSharedComponentIndex<RenderMeshArray>(entityB),
                "Precondition: Entities should hand the freed shared component index to the new value.");

            m_Table.Update(m_Manager);

            Assert.AreEqual(materialB.GetEntityId(),
                SurfaceCacheRenderMeshArrayTable.GetMaterial(GetEntry(entityB), materialMeshInfo),
                "A recycled index with new contents should be rebuilt rather than served from the stale entry.");
        }

        [Test]
        public void RenderMeshArrayNoLongerUsed_EntryIsDropped()
        {
            var mesh = CreateMesh();
            var material = CreateMaterial();
            var entity = CreateRenderableEntity(new RenderMeshArray(new[] { material }, new[] { mesh }),
                MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

            m_Table.Update(m_Manager);
            Assert.AreEqual(1, m_Table.Entries.Count, "The one RenderMeshArray in the world should be mirrored.");

            m_Manager.DestroyEntity(entity);
            m_Table.Update(m_Manager);

            Assert.AreEqual(0, m_Table.Entries.Count,
                "An entry whose RenderMeshArray is no longer referenced should be dropped.");
        }

        [Test]
        public void RuntimeMeshAndMaterial_ResolveToNone()
        {
            var mesh = CreateMesh();
            var material = CreateMaterial();
            var entity = CreateRenderableEntity(new RenderMeshArray(new[] { material }, new[] { mesh }),
                MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

            m_Table.Update(m_Manager);
            var entry = GetEntry(entity);

            // Runtime ids are registered with Entities Graphics rather than a RenderMeshArray, so the table cannot
            // resolve them and the caller has to fall back.
            var runtime = MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0);
            runtime.MeshID = new UnityEngine.Rendering.BatchMeshID { value = 1 };
            runtime.MaterialID = new UnityEngine.Rendering.BatchMaterialID { value = 1 };

            Assert.IsTrue(runtime.IsRuntimeMesh, "Precondition: the MaterialMeshInfo should describe a runtime mesh.");
            Assert.AreEqual(EntityId.None, SurfaceCacheRenderMeshArrayTable.GetMesh(entry, runtime));
            Assert.AreEqual(EntityId.None, SurfaceCacheRenderMeshArrayTable.GetMaterial(entry, runtime));
        }

        [Test]
        public void OutOfRangeIndices_ResolveToNoneRatherThanThrowing()
        {
            var mesh = CreateMesh();
            var material = CreateMaterial();
            var entity = CreateRenderableEntity(new RenderMeshArray(new[] { material }, new[] { mesh }),
                MaterialMeshInfo.FromRenderMeshArrayIndices(0, 0));

            m_Table.Update(m_Manager);
            var entry = GetEntry(entity);

            // RenderMeshArray would throw here; a job cannot, so the table reports None instead.
            var outOfRange = MaterialMeshInfo.FromRenderMeshArrayIndices(99, 99);

            Assert.AreEqual(EntityId.None, SurfaceCacheRenderMeshArrayTable.GetMesh(entry, outOfRange));
            Assert.AreEqual(EntityId.None, SurfaceCacheRenderMeshArrayTable.GetMaterial(entry, outOfRange));
        }

        SurfaceCacheRenderMeshArrayTable.Entry GetEntry(Entity entity)
        {
            int sharedIndex = m_Manager.GetSharedComponentIndex<RenderMeshArray>(entity);
            Assert.IsTrue(m_Table.Entries.TryGetValue(sharedIndex, out var entry),
                "The table should hold an entry for the entity's RenderMeshArray shared component index.");
            return entry;
        }

        Entity CreateRenderableEntity(RenderMeshArray renderMeshArray, MaterialMeshInfo materialMeshInfo)
        {
            var entity = m_Manager.CreateEntity();
            m_Manager.AddComponentData(entity, materialMeshInfo);
            m_Manager.AddSharedComponent(entity, renderMeshArray);
            return entity;
        }

        Mesh CreateMesh()
        {
            var mesh = new Mesh { name = "SurfaceCacheRenderMeshArrayTableTests Mesh" };
            mesh.vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f) };
            mesh.triangles = new[] { 0, 1, 2 };
            m_Assets.Add(mesh);
            return mesh;
        }

        Material CreateMaterial()
        {
            var material = new Material(Shader.Find("Hidden/InternalErrorShader"))
            {
                name = "SurfaceCacheRenderMeshArrayTableTests Material"
            };
            m_Assets.Add(material);
            return material;
        }
    }
}

#endif
