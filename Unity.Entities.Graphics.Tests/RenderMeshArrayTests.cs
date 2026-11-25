using System.Collections.Generic;
using NUnit.Framework;
using Unity.Rendering;
using UnityEngine;

namespace Unity.Entities.Graphics.Tests
{
    internal class RenderMeshArrayTests
    {
        Mesh m_Mesh;
        Material m_Material;
        readonly List<RenderMeshArrayHost> m_UnusedHosts = new();

        [SetUp]
        public void SetUp()
        {
            m_Mesh = new Mesh();
            m_Material = new Material(Shader.Find("Hidden/InternalErrorShader"));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var host in m_UnusedHosts)
                UnityEngine.Object.DestroyImmediate(host);
            m_UnusedHosts.Clear();

            UnityEngine.Object.DestroyImmediate(m_Mesh);
            UnityEngine.Object.DestroyImmediate(m_Material);
        }

        [Test]
        public void SameMeshesAndMaterials_AreEqual()
        {
            var a = new RenderMeshArray(new[] { m_Material }, new[] { m_Mesh });
            var b = new RenderMeshArray(new[] { m_Material }, new[] { m_Mesh });
            m_UnusedHosts.Add(a.m_Host.Value);
            m_UnusedHosts.Add(b.m_Host.Value);

            Assert.That(a == b, Is.True, "RenderMeshArrays with the same meshes and materials should be equal");
            Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()), "RenderMeshArrays with the same meshes and materials should have the same hash code");
        }
    }
}
