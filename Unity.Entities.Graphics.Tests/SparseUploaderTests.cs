using System;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.Rendering.Tests
{
    public class SparseUploaderTests
    {
        struct ExampleStruct
        {
            public int someData;
        }

        GraphicsBuffer m_Buffer;
        SparseUploader m_Uploader;

        private void Setup<T>(int count) where T : struct
        {
            m_Buffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, GraphicsBuffer.UsageFlags.None, count, UnsafeUtility.SizeOf<T>());
            m_Uploader = new SparseUploader(m_Buffer);
        }

        private void Setup<T>(T[] initialData) where T : struct
        {
            m_Buffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, GraphicsBuffer.UsageFlags.None, initialData.Length, UnsafeUtility.SizeOf<T>());
            m_Buffer.SetData(initialData);
            m_Uploader = new SparseUploader(m_Buffer);
        }

        private void Teardown()
        {
            m_Uploader.Dispose();
            m_Buffer.Dispose();
        }

        private float4x4 GenerateTestMatrix(int i)
        {
            var trans = float4x4.Translate(new float3(i * 0.2f, -i * 0.4f, math.cos(i * math.PI * 0.02f)));
            var rot = float4x4.EulerXYZ(i * 0.1f, math.PI * 0.5f, -i * 0.3f);
            return math.mul(trans, rot);
        }

        static float4x4 ExpandMatrix(float3x4 mat)
        {
            return new float4x4(
                new float4(mat.c0.x, mat.c0.y, mat.c0.z, 0.0f),
                new float4(mat.c1.x, mat.c1.y, mat.c1.z, 0.0f),
                new float4(mat.c2.x, mat.c2.y, mat.c2.z, 0.0f),
                new float4(mat.c3.x, mat.c3.y, mat.c3.z, 1.0f));
        }

        static float3x4 PackMatrix(float4x4 mat)
        {
            return new float3x4(
                mat.c0.xyz,
                mat.c1.xyz,
                mat.c2.xyz,
                mat.c3.xyz);
        }

        internal unsafe class TestSparseUploader
        {
            public ThreadedSparseUploader ThreadedSparseUploader;
            private NativeArray<int> m_ExpectedData;

            public TestSparseUploader(ThreadedSparseUploader sparseUploader, NativeArray<int> expectedData = default)
            {
                ThreadedSparseUploader = sparseUploader;
                m_ExpectedData = expectedData;
            }

            public static implicit operator ThreadedSparseUploader(TestSparseUploader tsu) => tsu.ThreadedSparseUploader;

            // Generics in constructors are not supported, so use this instead.
            public TestSparseUploader WithInitialData<T>(T[] initialData) where T : unmanaged
            {
                int totalSize = UnsafeUtility.SizeOf<T>() * initialData.Length;
                Assert.AreEqual(0, totalSize % sizeof(int));

                int totalInts = totalSize / sizeof(int);
                m_ExpectedData = new NativeArray<int>(totalInts, Allocator.Temp, NativeArrayOptions.ClearMemory);

                UnsafeUtility.MemCpy(
                    m_ExpectedData.GetUnsafePtr(),
                    UnsafeUtility.AddressOf(ref initialData[0]),
                    totalSize);

                return this;
            }

            private byte* DstPointer(int offset = 0) => (byte*)m_ExpectedData.GetUnsafePtr() + offset;

            public void AddUpload(void* src, int size, int offsetInBytes, int repeatCount = 1)
            {
                Assert.AreEqual(0, size % sizeof(int));
                Assert.AreEqual(0, offsetInBytes % sizeof(int));

                byte* dst = DstPointer(offsetInBytes);

                for (int i = 0; i < repeatCount; ++i)
                {
                    UnsafeUtility.MemCpy(dst, src, size);
                    dst += size;
                }

                ThreadedSparseUploader.AddUpload(src, size, offsetInBytes, repeatCount);
            }

            public void AddUpload<T>(T val, int offsetInBytes, int repeatCount = 1) where T : unmanaged
            {
                var size = UnsafeUtility.SizeOf<T>();
                AddUpload(&val, size, offsetInBytes, repeatCount);
                ThreadedSparseUploader.AddUpload(val, offsetInBytes, repeatCount);
            }

            public void AddUpload<T>(NativeArray<T> array, int offsetInBytes, int repeatCount = 1) where T : unmanaged
            {
                var size = UnsafeUtility.SizeOf<T>() * array.Length;
                AddUpload(array.GetUnsafeReadOnlyPtr(), size, offsetInBytes, repeatCount);
                ThreadedSparseUploader.AddUpload(array, offsetInBytes, repeatCount);
            }

            public void AddMatrixUpload(void* src, int numMatrices, int offset, int offsetInverse,
                ThreadedSparseUploader.MatrixType srcType, ThreadedSparseUploader.MatrixType dstType)
            {
                float* srcFloats = (float*)src;
                byte* dstBytes = DstPointer();

                float* dst = (float*)(dstBytes + offset);
                float* dstInverse = (offsetInverse < 0) ? null : (float*)(dstBytes + offsetInverse);

                Assert.Less(offset, m_ExpectedData.Length * sizeof(int));
                Assert.Less(offsetInverse, m_ExpectedData.Length * sizeof(int));

                bool srcIs4x4 = srcType == ThreadedSparseUploader.MatrixType.MatrixType4x4;
                bool dstIs4x4 = dstType == ThreadedSparseUploader.MatrixType.MatrixType4x4;

                // 3 supported cases:
                // - 4x4 to 4x4
                // - 3x4 to 3x4
                // - 4x4 to 3x4
                Assert.False(!srcIs4x4 && dstIs4x4, "MatrixUpload from 3x4 to 4x4 is not supported");

                int srcStride = srcIs4x4 ? 16 : 12;
                int dstStride = dstIs4x4 ? 16 : 12;

                int srcSize = srcStride * sizeof(float);
                int dstSize = dstStride * sizeof(float);

                for (int i = 0; i < numMatrices; ++i)
                {
                    float* srcMatrix = srcFloats + i * srcStride;

                    if (srcType == dstType)
                    {
                        UnsafeUtility.MemCpy(
                            dst + i * dstStride,
                            srcMatrix,
                            dstSize);
                    }
                    else if (srcIs4x4)
                    {
                        float4x4 m;
                        UnsafeUtility.MemCpy(&m, srcMatrix, srcSize);
                        float3x4 m3 = PackMatrix(m);
                        UnsafeUtility.MemCpy(
                            dst + i * dstStride,
                            &m3,
                            dstSize);
                    }

                    if (dstInverse != null)
                    {
                        float4x4 m;

                        if (srcIs4x4)
                        {
                            UnsafeUtility.MemCpy(&m, srcMatrix, srcSize);
                        }
                        else
                        {
                            float3x4 m3;
                            UnsafeUtility.MemCpy(&m3, srcMatrix, srcSize);
                            m = ExpandMatrix(m3);
                        }

                        float4x4 mi = math.fastinverse(m);

                        if (dstIs4x4)
                        {
                            UnsafeUtility.MemCpy(
                                dstInverse + i * dstStride,
                                &mi,
                                dstSize);
                        }
                        else
                        {
                            float3x4 m3 = PackMatrix(mi);
                            UnsafeUtility.MemCpy(
                                dstInverse + i * dstStride,
                                &m3,
                                dstSize);
                        }
                    }
                }

                if (offsetInverse < 0)
                {
                    ThreadedSparseUploader.AddMatrixUpload(src, numMatrices, offset, srcType, dstType);
                }
                else
                {
                    ThreadedSparseUploader.AddMatrixUploadAndInverse(src, numMatrices, offset, offsetInverse, srcType, dstType);
                }
            }

            public void AddStridedUpload(void* src, uint elemSize, uint srcStride, uint count, uint dstOffset, int dstStride)
            {
                Assert.Greater(elemSize, 0);
                Assert.LessOrEqual(elemSize, 64 * sizeof(int));
                Assert.Greater(count, 0);
                Assert.NotZero(dstStride);
                Assert.Zero(elemSize % sizeof(float));
                Assert.Zero(srcStride % sizeof(float));
                Assert.Zero(dstOffset % sizeof(float));
                Assert.Zero(dstStride % sizeof(float));

                byte* srcBytes = (byte*)src;
                byte* dstBytes = DstPointer((int)dstOffset);

                for (int i = 0; i < count; ++i)
                {
                    UnsafeUtility.MemCpy(dstBytes, srcBytes, elemSize);

                    srcBytes += srcStride;
                    dstBytes += dstStride;
                }

                ThreadedSparseUploader.AddStridedUpload(src, elemSize, srcStride, count, dstOffset, dstStride);
            }

            public void ValidateBitExact(void* actualResult)
            {
                int totalSize = m_ExpectedData.Length * sizeof(int);

                int* actualInts = (int*)actualResult;

                for (int i = 0; i < m_ExpectedData.Length; ++i)
                {
                    int expected = m_ExpectedData[i];
                    int actual = actualInts[i];

                    if (expected != actual)
                    {
                        float fExpected = BitConverter.ToSingle(BitConverter.GetBytes(expected));
                        float fActual = BitConverter.ToSingle(BitConverter.GetBytes(actual));

                        Assert.AreEqual(expected, actual, $"Value mismatch at index {i}: {actual} ({fActual}) != {expected} ({fExpected})");
                    }
                }
            }

            public void ValidateApproximateFloat(void* actualResult, float delta = 0.001f)
            {
                int totalFloats = m_ExpectedData.Length;

                float* expectedFloats = (float*)m_ExpectedData.GetUnsafePtr();
                float* actualFloats = (float*)actualResult;

                for (int i = 0; i < totalFloats; ++i)
                {
                    CompareFloats(expectedFloats[i], actualFloats[i], delta);
                }
            }

            public void ValidateBitExact(GraphicsBuffer buffer)
            {
                int[] actualData = new int[m_ExpectedData.Length];
                buffer.GetData(actualData);
                ValidateBitExact(UnsafeUtility.AddressOf(ref actualData[0]));
            }

            public void ValidateApproximateFloat(GraphicsBuffer buffer, float delta = 0.001f)
            {
                float[] actualData = new float[m_ExpectedData.Length];
                buffer.GetData(actualData);
                ValidateApproximateFloat(UnsafeUtility.AddressOf(ref actualData[0]), delta);
            }
        }

        [Test]
        public void ReplaceBuffer()
        {
            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var initialData = new ExampleStruct[64];
            for (int i = 0; i < initialData.Length; ++i)
                initialData[i] = new ExampleStruct { someData = i };

            Setup(initialData);

            var newBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, GraphicsBuffer.UsageFlags.None,
                initialData.Length * 2, UnsafeUtility.SizeOf<ExampleStruct>());

            m_Uploader.ReplaceBuffer(newBuffer, true);
            m_Buffer.Dispose();
            m_Buffer = newBuffer;

            var resultingData = new ExampleStruct[initialData.Length];
            m_Buffer.GetData(resultingData);

            for (int i = 0; i < resultingData.Length; ++i)
                Assert.AreEqual(i, resultingData[i].someData);

            Teardown();
        }

        [Test]
        public void NoUploads()
        {
            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            Setup<float>(1);

            var tsu = m_Uploader.Begin(0, 0, 0);
            m_Uploader.EndAndCommit(tsu);

            tsu = m_Uploader.Begin(1024, 1024, 1);
            m_Uploader.EndAndCommit(tsu);

            Teardown();
        }

        [Test]
        public void SmallUpload()
        {
            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var initialData = new ExampleStruct[64];
            for (int i = 0; i < initialData.Length; ++i)
                initialData[i] = new ExampleStruct { someData = 0 };

            Setup(initialData);

            var structSize = UnsafeUtility.SizeOf<ExampleStruct>();
            var totalSize = structSize * initialData.Length;

            var tsu = new TestSparseUploader(m_Uploader.Begin(totalSize, structSize, initialData.Length))
                .WithInitialData(initialData);

            for (int i = 0; i < initialData.Length; ++i)
                tsu.AddUpload(new ExampleStruct { someData = i }, i * 4);

            m_Uploader.EndAndCommit(tsu);

            tsu.ValidateBitExact(m_Buffer);

            Teardown();
        }

        [Test]
        public void BasicUploads()
        {
            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var initialData = new ExampleStruct[1024];
            for (int i = 0; i < initialData.Length; ++i)
                initialData[i] = new ExampleStruct { someData = i };

            Setup(initialData);

            var structSize = UnsafeUtility.SizeOf<ExampleStruct>();
            var totalSize = structSize * initialData.Length;
            var tsu =
                new TestSparseUploader(m_Uploader.Begin(totalSize, totalSize, initialData.Length))
                    .WithInitialData(initialData);

            tsu.AddUpload(new ExampleStruct { someData = 7 }, 4);
            m_Uploader.EndAndCommit(tsu);

            tsu.ValidateBitExact(m_Buffer);

            tsu.ThreadedSparseUploader = m_Uploader.Begin(structSize, structSize, 1);
            tsu.AddUpload(new ExampleStruct { someData = 13 }, 8);
            m_Uploader.EndAndCommit(tsu);

            tsu.ValidateBitExact(m_Buffer);

            Teardown();
        }

        [Test]
        public unsafe void BigUploads()
        {
            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var initialData = new ExampleStruct[4 * 1024];
            for (int i = 0; i < initialData.Length; ++i)
                initialData[i] = new ExampleStruct { someData = i };

            Setup(initialData);

            var newData = new ExampleStruct[312];
            for (int i = 0; i < newData.Length; ++i)
                newData[i] = new ExampleStruct { someData = i + 3000 };

            var newData2 = new ExampleStruct[316];
            for (int i = 0; i < newData2.Length; ++i)
                newData2[i] = new ExampleStruct { someData = i + 4000 };

            var structSize = UnsafeUtility.SizeOf<ExampleStruct>();
            var totalSize = structSize * (newData.Length + newData2.Length);

            var tsu =
                new TestSparseUploader(m_Uploader.Begin(totalSize, totalSize, initialData.Length))
                    .WithInitialData(initialData);

            fixed (void* ptr = newData)
                tsu.AddUpload(ptr, newData.Length * 4, 512 * 4);
            fixed (void* ptr2 = newData2)
                tsu.AddUpload(ptr2, newData2.Length * 4, 1136 * 4);
            m_Uploader.EndAndCommit(tsu);

            tsu.ValidateBitExact(m_Buffer);

            Teardown();
        }

        [Test]
        public void SplatUpload()
        {
            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var initialData = new ExampleStruct[64];

            for (int i = 0; i < initialData.Length; ++i)
                initialData[i] = new ExampleStruct { someData = 0 };

            Setup(initialData);

            var structSize = UnsafeUtility.SizeOf<ExampleStruct>();

            var tsu =
                new TestSparseUploader(m_Uploader.Begin(structSize, structSize, 1))
                    .WithInitialData(initialData);

            tsu.AddUpload(new ExampleStruct { someData = 1 }, 0, 64);
            m_Uploader.EndAndCommit(tsu);

            tsu.ValidateBitExact(m_Buffer);

            Teardown();
        }

        struct UploadJob : IJobParallelFor
        {
            public ThreadedSparseUploader uploader;

            public void Execute(int index)
            {
                uploader.AddUpload(new ExampleStruct { someData = index }, index * 4);
            }
        }

        [Test]
        public void UploadFromJobs()
        {
            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var initialData = new ExampleStruct[4 * 1024];
            var stride = UnsafeUtility.SizeOf<ExampleStruct>();

            for (int i = 0; i < initialData.Length; ++i)
                initialData[i] = new ExampleStruct { someData = 0 };

            Setup(initialData);

            var job = new UploadJob();
            var totalSize = initialData.Length * stride;
            job.uploader = m_Uploader.Begin(totalSize, stride, initialData.Length);
            job.Schedule(initialData.Length, 64).Complete();
            m_Uploader.EndAndCommit(job.uploader);

            var resultingData = new ExampleStruct[initialData.Length];
            m_Buffer.GetData(resultingData);

            for (int i = 0; i < resultingData.Length; ++i)
                Assert.AreEqual(i, resultingData[i].someData);

            Teardown();
        }

        static void CompareFloats(float expected, float actual, float delta = 0.00001f)
        {
            Assert.LessOrEqual(math.abs(expected - actual), delta);
        }

        static void CompareMatrices(float4x4 expected, float4x4 actual, float delta = 0.00001f)
        {
            for (int i = 0; i < 4; ++i)
            {
                for (int j = 0; j < 4; ++j)
                {
                    CompareFloats(expected[i][j], actual[i][j], delta);
                }
            }
        }

        [Test]
        public unsafe void MatrixUploads4x4()
        {
            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            if (GraphicsSettings.currentRenderPipeline != null)
                Assert.Ignore("Fails when an SRP is active: UUM-160230");

            var numMatrices = 1025;
            var initialData = new float4x4[numMatrices];

            for (int i = 0; i < numMatrices; ++i)
                initialData[i] = float4x4.zero;

            Setup(initialData);

            var matSize = UnsafeUtility.SizeOf<float4x4>();
            var totalSize = numMatrices * matSize;

            var tsu = new TestSparseUploader(m_Uploader.Begin(totalSize, totalSize, 1))
                .WithInitialData(initialData);

            var deltaData = new NativeArray<float4x4>(numMatrices, Allocator.Temp);
            for (int i = 0; i < numMatrices; ++i)
                deltaData[i] = GenerateTestMatrix(i);
            tsu.AddMatrixUpload(deltaData.GetUnsafeReadOnlyPtr(), numMatrices, 0, -1,
                ThreadedSparseUploader.MatrixType.MatrixType4x4,
                ThreadedSparseUploader.MatrixType.MatrixType4x4);
            m_Uploader.EndAndCommit(tsu);
            deltaData.Dispose();

            tsu.ValidateApproximateFloat(m_Buffer);

            Teardown();
        }

        [Test]
        public unsafe void MatrixUploads4x4To3x4()
        {
            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            if (GraphicsSettings.currentRenderPipeline != null && Application.platform == RuntimePlatform.OSXEditor)
                Assert.Ignore("Fails on macOS when an SRP is active: UUM-160230");

            var numMatrices = 1025;
            var initialData = new float3x4[numMatrices];

            for (int i = 0; i < numMatrices; ++i)
                initialData[i] = float3x4.zero;

            Setup(initialData);

            var matSize = UnsafeUtility.SizeOf<float4x4>();
            var totalSize = numMatrices * matSize;

            var tsu =
                new TestSparseUploader(m_Uploader.Begin(totalSize, totalSize, 1))
                    .WithInitialData(initialData);

            var deltaData = new NativeArray<float4x4>(numMatrices, Allocator.Temp);
            for (int i = 0; i < numMatrices; ++i)
                deltaData[i] = GenerateTestMatrix(i);
            tsu.AddMatrixUpload(deltaData.GetUnsafeReadOnlyPtr(), numMatrices, 0, -1,
                ThreadedSparseUploader.MatrixType.MatrixType4x4,
                ThreadedSparseUploader.MatrixType.MatrixType3x4);
            m_Uploader.EndAndCommit(tsu);
            deltaData.Dispose();

            tsu.ValidateApproximateFloat(m_Buffer);

            Teardown();
        }

        [Test]
        public unsafe void InverseMatrixUploads4x4()
        {
            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var numMatrices = 1025;
            var initialData = new float4x4[numMatrices * 2];

            for (int i = 0; i < initialData.Length; ++i)
                initialData[i] = float4x4.zero;

            Setup(initialData);

            var matSize = UnsafeUtility.SizeOf<float4x4>();
            var totalSize = numMatrices * matSize;

            var tsu =
                new TestSparseUploader(m_Uploader.Begin(totalSize, totalSize, 1))
                    .WithInitialData(initialData);

            var deltaData = new NativeArray<float4x4>(numMatrices, Allocator.Temp);
            for (int i = 0; i < numMatrices; ++i)
                deltaData[i] = GenerateTestMatrix(i);
            tsu.AddMatrixUpload(deltaData.GetUnsafeReadOnlyPtr(), numMatrices, 0, numMatrices * 64,
                ThreadedSparseUploader.MatrixType.MatrixType4x4,
                ThreadedSparseUploader.MatrixType.MatrixType4x4);
            m_Uploader.EndAndCommit(tsu);

            deltaData.Dispose();

            tsu.ValidateApproximateFloat(m_Buffer);

            Teardown();
        }

        [Test]
        public unsafe void InverseMatrixUploads4x4To3x4()
        {
            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var numMatrices = 1025;
            var initialData = new float3x4[numMatrices * 2];

            for (int i = 0; i < initialData.Length; ++i)
                initialData[i] = float3x4.zero;

            Setup(initialData);

            var matSize = UnsafeUtility.SizeOf<float4x4>();
            var totalSize = numMatrices * matSize;

            var tsu =
                new TestSparseUploader(m_Uploader.Begin(totalSize, totalSize, 1))
                    .WithInitialData(initialData);

            var deltaData = new NativeArray<float4x4>(numMatrices, Allocator.Temp);
            for (int i = 0; i < numMatrices; ++i)
                deltaData[i] = GenerateTestMatrix(i);
            tsu.AddMatrixUpload(deltaData.GetUnsafeReadOnlyPtr(), numMatrices, 0, numMatrices * 48,
                ThreadedSparseUploader.MatrixType.MatrixType4x4,
                ThreadedSparseUploader.MatrixType.MatrixType3x4);
            m_Uploader.EndAndCommit(tsu);

            deltaData.Dispose();

            tsu.ValidateApproximateFloat(m_Buffer);

            Teardown();
        }

        [Test]
        public void HugeUploadCount()
        {
            const int k_HugeCount = 100_000;

            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var initialData = new ExampleStruct[k_HugeCount];

            Setup(initialData);

            var structSize = UnsafeUtility.SizeOf<ExampleStruct>();
            var tsu =
                new TestSparseUploader(m_Uploader.Begin(structSize * k_HugeCount, structSize, k_HugeCount))
                    .WithInitialData(initialData);

            for (int i = 0; i < initialData.Length; ++i)
                tsu.AddUpload(new ExampleStruct { someData = i }, 4 * i);

            m_Uploader.EndAndCommit(tsu);

            tsu.ValidateBitExact(m_Buffer);

            Teardown();
        }

        [Test]
        public unsafe void StridedUploadBasic()
        {
            const int k_Count = 100;
            const int k_BufferSize = k_Count * 4;

            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var initialData = new int[k_BufferSize];
            for (int i = 0; i < initialData.Length; ++i)
                initialData[i] = 0;

            Setup(initialData);

            uint structSize = sizeof(int);
            int totalSize = (int)(structSize * k_Count);

            var tsu =
                new TestSparseUploader(m_Uploader.Begin(totalSize, totalSize, k_Count))
                    .WithInitialData(initialData);

            var src = new NativeArray<int>(k_Count, Allocator.Temp);
            for (int i = 0; i < src.Length; ++i)
                src[i] = i;

            tsu.AddStridedUpload(src.GetUnsafeReadOnlyPtr(),
                structSize, structSize, k_Count,
                0, (int)(structSize * 2));

            m_Uploader.EndAndCommit(tsu);

            tsu.ValidateBitExact(m_Buffer);

            Teardown();
        }

        [Test]
        public unsafe void StridedUploadFloat3()
        {
            const int k_Count = 2;
            const int k_BufferSize = k_Count * 4;

            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var initialData = new float[k_BufferSize];
            for (int i = 0; i < initialData.Length; ++i)
                initialData[i] = 0;

            Setup(initialData);

            uint structSize = (uint)sizeof(float3);
            int totalSize = (int)(structSize * k_Count);

            var tsu =
                new TestSparseUploader(m_Uploader.Begin(totalSize, totalSize, k_Count))
                    .WithInitialData(initialData);

            var src = new NativeArray<float3>(k_Count, Allocator.Temp);
            for (int i = 0; i < src.Length; ++i)
                src[i] = new float3(i, i * 2, i * 3);


            // Upload float3s as float4s
            tsu.AddStridedUpload(src.GetUnsafeReadOnlyPtr(),
                structSize, structSize, k_Count,
                0, sizeof(float4));

            m_Uploader.EndAndCommit(tsu);

            tsu.ValidateBitExact(m_Buffer);

            Teardown();
        }

        internal unsafe struct TestFloat5
        {
            public fixed float fs[5];

            public TestFloat5(float f)
            {
                fs[0] = f + 1;
                fs[1] = 2 * f + 1;
                fs[2] = 3 * f + 1;
                fs[3] = 4 * f + 1;
                fs[4] = 5 * f + 1;
            }
        }

        [Test]
        public unsafe void StridedUploadWeirdStrides()
        {
            const int k_Count = 100;
            const int k_BufferSize = k_Count * 16;

            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var initialData = new float[k_BufferSize];
            for (int i = 0; i < initialData.Length; ++i)
                initialData[i] = 0;

            Setup(initialData);

            uint structSize = (uint)UnsafeUtility.SizeOf<TestFloat5>();

            uint srcStride = structSize * 2;
            int totalSize = (int)(srcStride * k_Count);

            var tsu =
                new TestSparseUploader(m_Uploader.Begin(totalSize, totalSize, k_Count))
                    .WithInitialData(initialData);

            var src = new NativeArray<TestFloat5>(k_Count * 2, Allocator.Temp);
            for (int i = 0; i < src.Length; ++i)
                src[i] = new TestFloat5(i);

            uint dstOffset = k_BufferSize * sizeof(float) - 2 * structSize;
            int dstStride = -7 * 4;

            tsu.AddStridedUpload(src.GetUnsafeReadOnlyPtr(),
                structSize, srcStride, k_Count,
                dstOffset, dstStride);

            m_Uploader.EndAndCommit(tsu);

            tsu.ValidateBitExact(m_Buffer);

            Teardown();
        }

        [Test]
        public void Constructor_NullDestinationBuffer_ThrowsArgumentNullException()
        {
            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            Assert.Throws<ArgumentNullException>(() => new SparseUploader(null));
        }

        [Test]
        public void Constructor_DisposedDestinationBuffer_ThrowsArgumentException()
        {
            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, GraphicsBuffer.UsageFlags.None, 1, UnsafeUtility.SizeOf<ExampleStruct>());
            buffer.Dispose();

            Assert.Throws<ArgumentException>(() => new SparseUploader(buffer));
        }

        [Test]
        public void Constructor_NonRawDestinationBuffer_ThrowsArgumentException()
        {
            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, GraphicsBuffer.UsageFlags.None, 1, UnsafeUtility.SizeOf<ExampleStruct>());
            try
            {
                Assert.Throws<ArgumentException>(() => new SparseUploader(buffer));
            }
            finally
            {
                buffer.Dispose();
            }
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void Constructor_NonPositiveBufferChunkSize_ThrowsArgumentOutOfRangeException(int chunkSize)
        {
            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, GraphicsBuffer.UsageFlags.None, 1, UnsafeUtility.SizeOf<ExampleStruct>());
            try
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => new SparseUploader(buffer, chunkSize));
            }
            finally
            {
                buffer.Dispose();
            }
        }

        [Test]
        public void ReplaceBuffer_NullBuffer_ThrowsArgumentNullException()
        {
            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            Setup<ExampleStruct>(1);

            Assert.Throws<ArgumentNullException>(() => m_Uploader.ReplaceBuffer(null));

            Teardown();
        }

        [Test]
        public void ReplaceBuffer_DisposedBuffer_ThrowsArgumentException()
        {
            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            Setup<ExampleStruct>(1);

            var newBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, GraphicsBuffer.UsageFlags.None, 1, UnsafeUtility.SizeOf<ExampleStruct>());
            newBuffer.Dispose();

            Assert.Throws<ArgumentException>(() => m_Uploader.ReplaceBuffer(newBuffer));

            Teardown();
        }

        [Test]
        public void ReplaceBuffer_NonRawBuffer_ThrowsArgumentException()
        {
            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            Setup<ExampleStruct>(1);

            var newBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, GraphicsBuffer.UsageFlags.None, 1, UnsafeUtility.SizeOf<ExampleStruct>());
            try
            {
                Assert.Throws<ArgumentException>(() => m_Uploader.ReplaceBuffer(newBuffer));
            }
            finally
            {
                newBuffer.Dispose();
                Teardown();
            }
        }

        [Test]
        public void ReplaceBuffer_CopyFalse_PreservesNewBufferContents()
        {
            if (!EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem())
            {
                Assert.Ignore("Skipped due to platform/computer not supporting compute shaders");
                return;
            }

            var initialData = new ExampleStruct[64];
            for (int i = 0; i < initialData.Length; ++i)
                initialData[i] = new ExampleStruct { someData = i };

            Setup(initialData);

            var newData = new ExampleStruct[initialData.Length];
            for (int i = 0; i < newData.Length; ++i)
                newData[i] = new ExampleStruct { someData = -1 };

            var newBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, GraphicsBuffer.UsageFlags.None,
                newData.Length, UnsafeUtility.SizeOf<ExampleStruct>());
            newBuffer.SetData(newData);

            m_Uploader.ReplaceBuffer(newBuffer, false);
            m_Buffer.Dispose();
            m_Buffer = newBuffer;

            var resultingData = new ExampleStruct[initialData.Length];
            m_Buffer.GetData(resultingData);

            for (int i = 0; i < resultingData.Length; ++i)
                Assert.AreEqual(-1, resultingData[i].someData);

            Teardown();
        }
    }
}
