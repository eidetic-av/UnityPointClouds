using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine;
using static UnityEngine.Experimental.Rendering.GraphicsFormat;

namespace Eidetic.PointClouds
{
    [Serializable]
    public class PointCloud : ScriptableObject
    {
        public Texture2D PositionMap;
        public Texture2D ColorMap;

        public int PointCount => PositionMap == null ? 0 : (PositionMap.width * PositionMap.height);

        /// <summary>
        /// Manually set the backing positions texture of a PointCloud.
        /// <para> The texture should contain r:g:b pixels corresponding to x:y:z world positions,
        /// with an optional alpha channel containing point "alive" values. </para>
        /// </summary>
        public void SetPositionMap(Texture src, bool destroySrc = true)
        {
            if (PositionMap == null)
                PositionMap = new Texture2D(src.width, src.height, TextureFormat.RGBAFloat, false)
                {
                    name = "PositionMap"
                };
            SetMap(src, PositionMap, destroySrc);
        }

        /// <summary>
        /// Manually set the backing colour texture of a PointCloud.
        /// </summary>
        public void SetColorMap(Texture src, bool destroySrc = true)
        {
            if (ColorMap == null)
                ColorMap = new Texture2D(src.width, src.height, TextureFormat.RGBAFloat, false)
                {
                    name = "ColorMap"
                };
            SetMap(src, ColorMap, destroySrc);
        }

        void SetMap(Texture src, Texture2D map, bool destroySrc)
        {
            if (map.height != src.height)
            {
                map.Resize(src.width, src.height);
                map.Apply();
            }

            Texture copySrc;
            // make sure that source and destination textures are the same pixel format
            if (src.graphicsFormat == map.graphicsFormat)
            {
                copySrc = src;
            }
            else
            {
                // convert it to a RenderTexture
                RenderTexture rt = new RenderTexture(src.width, src.height, 24, R32G32B32A32_SFloat);
                var currentRt = RenderTexture.active;
                RenderTexture.active = rt;

                // this is the actual conversion, the vectors here flip the image vertically,
                // otherwise it prints the image upside down
                Graphics.Blit(src, rt, new Vector2(1.0f, -1.0f), new Vector2(0.0f, 1.0f));
                copySrc = rt;

                RenderTexture.active = currentRt;
                Destroy(src);
                src = null;
            }

            Graphics.CopyTexture(copySrc, map);

            if (destroySrc)
            {
                Destroy(copySrc);
                copySrc = null;
            }
        }

        ComputeShader setPointsShader;
        ComputeShader SetPointsShader => setPointsShader ??
            (setPointsShader = Resources.Load<ComputeShader>("PointCloudSetPointsShader"));

        int SetPointsHandle => SetPointsShader.FindKernel("SetPoints");

        /// <summary>
        /// Set the positions and colours in a PointCloud.
        /// </summary>
        public (Texture2D, Texture2D) SetPoints(IEnumerable<Vector3> positions, IEnumerable<Color> colors) =>
            SetPoints(positions.ToArray(), colors.ToArray());

        /// <summary>
        /// Set the positions and colours in a PointCloud.
        /// </summary>
        public (Texture2D, Texture2D) SetPoints(Vector3[] positions, Color[] colors)
        {
            var pointCount = positions.Length;

            // find a width and height smaller than MaxTextureSize
            int width = SystemInfo.maxTextureSize;

            // var tempHeight =  (float) pointCount / width;
            // while (tempHeight % 1f != 0)
            //     tempHeight = (float) pointCount / --width;
            // int height = (int) tempHeight;
            int height = pointCount / width;

            // TODO this needs to be reworked... it will cull points
            if (height > SystemInfo.maxTextureSize)
                height = SystemInfo.maxTextureSize;

            var pixelCount = width * height;

            var positionsBuffer = new ComputeBuffer(positions.Length, 12);
            positionsBuffer.SetData(positions);
            var colorsBuffer = new ComputeBuffer(positions.Length, 16);
            colorsBuffer.SetData(colors);


            var positionsRt = new RenderTexture(width, height, 24, R32G32B32A32_SFloat);
            positionsRt.enableRandomWrite = true;
            positionsRt.Create();
            var colorsRt = new RenderTexture(width, height, 24, R32G32B32A32_SFloat);
            colorsRt.enableRandomWrite = true;
            colorsRt.Create();

            SetPointsShader.SetBuffer(SetPointsHandle, "Positions", positionsBuffer);
            SetPointsShader.SetBuffer(SetPointsHandle, "Colors", colorsBuffer);
            SetPointsShader.SetInt("Width", width);
            SetPointsShader.SetInt("PointCount", pointCount);
            SetPointsShader.SetTexture(SetPointsHandle, "PositionMap", positionsRt);
            SetPointsShader.SetTexture(SetPointsHandle, "ColorMap", colorsRt);

            var threadGroupsX = Mathf.CeilToInt(width / 8f);
            var threadGroupsY = Mathf.CeilToInt(height / 8f);
            SetPointsShader.Dispatch(SetPointsHandle, threadGroupsX, threadGroupsY, 1);

            SetPositionMap(positionsRt);
            SetColorMap(colorsRt);

            Destroy(positionsRt);
            Destroy(colorsRt);
            positionsBuffer.Release();
            colorsBuffer.Release();

            return (PositionMap, ColorMap);
        }

        /// <summary>
        /// Set the positions and colours in a PointCloud given a float[] of 
        /// x, y, z positions and a byte[] of r:g:b values packed in order.
        /// </summary>
        public (Texture2D, Texture2D) SetPoints(float[] positions, byte[] colors)
        {
            // TODO process this in parrallel
            var pointCount = positions.Length / 3;
            var positionVectors = new Vector3[pointCount];
            var packedColors = new Color[pointCount];
            for (int i = 0; i < pointCount; i++)
            {
                var index = i * 3;
                var x = positions[index] * -1;
                var y = positions[index + 1] * -1;
                var z = positions[index + 2];
                positionVectors[i] = new Vector3(x, y, z);
                var r = colors[index] / 256f;
                var g = colors[index + 1] / 256f;
                var b = colors[index + 2] / 256f;
                packedColors[i] = new Color(r, g, b);
            }
            return SetPoints(positionVectors, packedColors);
        }

        /// <summary>
        /// Set the positions and colours in a PointCloud given a raw byte array 
        /// containing first a Vector3[] of positions and then a byte[] of colours.
        /// </summary>
        public (Texture2D, Texture2D) SetPoints(byte[] frameBuffer, int pointCount)
        {
            // find a width and height smaller than maxTextureSize
            int width = SystemInfo.maxTextureSize;

            var tempHeight = (float)pointCount / width;
            while (tempHeight % 1f != 0)
                tempHeight = (float)pointCount / --width;
            int height = (int)tempHeight;

            var pixelCount = width * height;

            var positionsBuffer = new ComputeBuffer(pointCount / 2, 12);
            positionsBuffer.SetData(frameBuffer, 0, 0, pointCount);
            var colorsBuffer = new ComputeBuffer(pointCount / 2, 12);
            colorsBuffer.SetData(frameBuffer, pointCount, 0, pointCount);

            var positionsRt = new RenderTexture(width, height, 24, R32G32B32A32_SFloat);
            positionsRt.enableRandomWrite = true;
            positionsRt.Create();
            var colorsRt = new RenderTexture(width, height, 24, R32G32B32A32_SFloat);
            colorsRt.enableRandomWrite = true;
            colorsRt.Create();

            SetPointsShader.SetBuffer(SetPointsHandle, "Positions", positionsBuffer);
            SetPointsShader.SetBuffer(SetPointsHandle, "Colors", colorsBuffer);
            SetPointsShader.SetInt("Width", width);
            SetPointsShader.SetInt("PointCount", pointCount);
            SetPointsShader.SetTexture(SetPointsHandle, "PositionMap", positionsRt);
            SetPointsShader.SetTexture(SetPointsHandle, "ColorMap", colorsRt);

            var threadGroupsX = Mathf.CeilToInt(width / 8f);
            var threadGroupsY = Mathf.CeilToInt(height / 8f);
            SetPointsShader.Dispatch(SetPointsHandle, threadGroupsX, threadGroupsY, 1);

            SetPositionMap(positionsRt);
            SetColorMap(colorsRt);

            Destroy(positionsRt);
            Destroy(colorsRt);
            positionsBuffer.Release();
            colorsBuffer.Release();

            return (PositionMap, ColorMap);
        }

        // for convenience:
        public static PointCloud CreateInstance() => CreateInstance<PointCloud>();
    }
}
