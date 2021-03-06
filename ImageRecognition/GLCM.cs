﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Xml;
using ImageProcessing;

namespace ImageRecognition
{
    public sealed class GLCMFeature
    {
        internal double[] source;
        private double[] feature;

        internal GLCMFeature(double[] data)
        {
            source = data;
            feature = new double[8];
            for (int i = 0; i < 4; ++i)
            {
                feature[i * 2] = MathHelpers.GetAverage(source, i * 4, 4);
                feature[i * 2 + 1] = Math.Sqrt(MathHelpers.GetVariance(source, feature[i * 2], i * 4, 4));
            }
        }

        internal GLCMFeature()
        {
            source = new double[16];
            feature = new double[8];
        }

        internal void SaveKnowledges(XmlElement element, XmlDocument parent)
        {
            var data = parent.CreateElement("GLCM");

            var sourceData = parent.CreateElement("source");
            for (int i = 0; i < 16; ++i)
            {
                var attribute = parent.CreateAttribute("s" + i.ToString());
                attribute.Value = source[i].ToString();
                sourceData.Attributes.Append(attribute);
            }
            data.AppendChild(sourceData);

            var featureData = parent.CreateElement("feature");
            for (int i = 0; i < 8; ++i)
            {
                var attribute = parent.CreateAttribute("f" + i.ToString());
                attribute.Value = feature[i].ToString();
                featureData.Attributes.Append(attribute);
            
            }
            data.AppendChild(featureData);

            element.AppendChild(data);
        }

        internal void LoadKnowledges(XmlNode node)
        {
            var sourceData = node.ChildNodes[0];
            for (int i = 0; i < 16; ++i)
            {
                double value;
                double.TryParse(sourceData.Attributes["s" + i.ToString()].Value, out value);
                source[i] = value;
            }

            var featureData = node.ChildNodes[1];
            for (int i = 0; i < 8; ++i)
            {
                double value;
                double.TryParse(featureData.Attributes["f" + i.ToString()].Value, out value);
                feature[i] = value;
            }
        }

        public double this[int index]
        {
            get
            {
                if ((index < 0) || (index >= 8))
                {
                    return 0;
                }
                return feature[index];
            }
        }

        public double GetDistance(GLCMFeature other)
        {
            double result = 0;
            var dWeight = RecognitionParameters.GLCMDeviationWeight;
            for (int i = 0; i < 8; ++i)
            {
                var weight = ((i + 1) % 2 == 2) ? (dWeight) : (1.0);
                result += weight * MathHelpers.Sqr(feature[i] - other[i]);
            }
            return Math.Sqrt(result);
        }

        public static GLCMFeature BuildStandart(List<GLCMFeature> list)
        {
            if (list.Count == 0)
            {
                return null;
            }

            if (list.Count == 1)
            {
                return list[0];
            }

            var data = new double[16];
            int count = 0;
            foreach (var item in list)
            {
                var glcm = item as GLCMFeature;
                if (glcm == null)
                {
                    continue;
                }

                for (int j = 0; j < 16; ++j)
                {
                    data[j] += glcm.source[j];
                }
                ++count; 
            }

            for (int j = 0; j < 16; ++j)
            {
                data[j] /= count;
            }
            return new GLCMFeature(data);
        }
    }

    internal class GLCMThreadParameter
    {
        public GLCMThreadParameter(int from, int to, List<Fragment> fragments, ManualResetEvent resetEvent)
        {
            ResetEvent = resetEvent;
            From = from;
            To = to;
            Fragments = fragments;
            Result = new List<GLCMFeature>();
        }

        public int From
        {
            get;
            set;
        }

        public int To
        {
            get;
            set;
        }

        public List<Fragment> Fragments
        {
            get;
            set;

        }

        public List<GLCMFeature> Result
        {
            get;
            set;
        }

        public ManualResetEvent ResetEvent
        {
            get;
            set;
        }
    }

    public class GLCMCreator
    {
        private byte[,] data;
        private GLCMFeature feature;
        private int width, height;
        private int fragmentSize;
        private bool isTeaching;
        private bool isDivideToFragments;
        private bool isMultithread;

        public GLCMCreator(ImageGrayData imageData, bool isTeaching, bool isDivideToFragments, bool isMultithread = true)
        {
            var needSize = RecognitionParameters.RecognitionFragmentSize;
            if (isTeaching)
            {
                needSize = RecognitionParameters.FragmentsSize;
            }

            if ((imageData.Width < needSize) || 
                (imageData.Height < needSize))
            {
                throw new ArgumentException("Размер изображения меньше размера фрагмента для разбиения. \n" +
                    "Невозможно создать матрицу взаимной встречаемости");
            }

            fragmentSize = needSize;
            this.isTeaching = isTeaching;
            this.isDivideToFragments = isDivideToFragments;
            this.isMultithread = isMultithread;
            PrepareData(imageData);
            ConstructFeature();
        }

        private void PrepareData(ImageGrayData imageData)
        {
            width = imageData.Width;
            height = imageData.Height;
            data = new byte[width, height];
            var grayData = imageData.Data;
            int quantizers = 256 / RecognitionParameters.GLCMSize;
            for (int y = 0; y < height; ++y)
            {
                for (int x = 0; x < width; ++x)
                {
                    data[x, y] = (byte)(grayData[x, y] / quantizers);
                }
            }
        }

        private void ConstructFeature()
        {
            if (isDivideToFragments)
            {
                var list = GetSubfeatures();
                feature = GLCMFeature.BuildStandart(list);
            }
            else
            {
                feature = GetGLCMFeature(new Fragment(0, 0, width, height));
            }
        }

        private List<GLCMFeature> GetSubfeatures()
        {
            var fragments = GetFragments();
            int threadCount = Math.Min(fragments.Count, RecognitionParameters.FragmentProcessThreadCount);
            threadCount = (isMultithread) ? (threadCount) : (1);
            int threadPart = fragments.Count / threadCount;
            var resetEvents = new ManualResetEvent[threadCount];
            var threadParameters = new GLCMThreadParameter[threadCount];
            for (int i = 0; i < threadCount; ++i)
            {
                resetEvents[i] = new ManualResetEvent(false);
                threadParameters[i] = new GLCMThreadParameter(
                    threadPart * i, 
                    ((i + 1) < threadCount) ? (threadPart * (i + 1)) : (fragments.Count), 
                    fragments, resetEvents[i]);

                if (threadCount > 1)
                {
                    ThreadPool.QueueUserWorkItem(new WaitCallback(CalculateGLCM), threadParameters[i]);
                }
                else
                {
                    CalculateGLCM(threadParameters[i]);
                }
            }
            WaitHandle.WaitAll(resetEvents);

            var list = new List<GLCMFeature>();
            for (int i = 0; i < threadCount; ++i)
            {
                list.AddRange(threadParameters[i].Result);
            }
            return list;
        }

        private List<Fragment> GetFragments()
        {
            var list = new List<Fragment>();
            int XCount = width / fragmentSize;
            int YCount = height / fragmentSize;

            for (int x = 0; x < XCount; ++x)
            {
                for (int y = 0; y < YCount; ++y)
                {
                    Fragment fragment = new Fragment(x * fragmentSize, y * fragmentSize,
                        fragmentSize, fragmentSize);
                    list.Add(fragment);
                }
            }

            if (XCount * fragmentSize < width)
            {
                for (int y = 0; y < YCount; ++y)
                {
                    Fragment fragment = new Fragment(width - fragmentSize, y * fragmentSize,
                        fragmentSize, fragmentSize);
                    list.Add(fragment);
                }
            }

            if (YCount * fragmentSize < height)
            {
                for (int x = 0; x < XCount; ++x)
                {
                    Fragment fragment = new Fragment(x * fragmentSize, height - fragmentSize,
                        fragmentSize, fragmentSize);
                    list.Add(fragment);
                } 
            }

            if ((XCount * fragmentSize < width) || (YCount * fragmentSize < height))
            {
                Fragment fragment = new Fragment(width - fragmentSize, height - fragmentSize,
                    fragmentSize, fragmentSize);
                list.Add(fragment);
            }

            return list;
        }

        private void CalculateGLCM(object parameter)
        {
            GLCMThreadParameter data = parameter as GLCMThreadParameter;
            if (data == null)
            {
                throw new ArgumentNullException("Неправильный параметр потока.");
            }

            for (int i = data.From; i < data.To; ++i)
            {
                var feature = GetGLCMFeature(data.Fragments[i]);
                data.Result.Add(feature);
            }
            data.ResetEvent.Set();
        }

        private GLCMFeature GetGLCMFeature(Fragment fragment)
        {
            var glcm0feature = GetBestScalarPropertyFeature(fragment, 1, 0);
            var glcm45feature = GetBestScalarPropertyFeature(fragment, 1, 1);
            var glcm90feature = GetBestScalarPropertyFeature(fragment, 0, 1);
            var glcm135feature = GetBestScalarPropertyFeature(fragment, -1, 1);
            var bigfeature = new double[16];
            for (int i = 0; i < 4; ++i)
            {
                bigfeature[i * 4] = glcm0feature[i];
                bigfeature[i * 4 + 1] = glcm45feature[i];
                bigfeature[i * 4 + 2] = glcm90feature[i];
                bigfeature[i * 4 + 3] = glcm135feature[i];
            }
            return new GLCMFeature(bigfeature);
        }

        private double[] GetBestScalarPropertyFeature(Fragment fragment, int dx, int dy)
        {
            var glcmList = GetGLCMList(fragment, dx, dy);
            var result = new double[4];
            for (int i = 0; i < glcmList.Count; ++i)
            {
                var scalar = GetScalarPropertyFeature(glcmList[i]);
                for (int j = 0; j < 4; ++j)
                {
                    if (scalar[j] > result[j])
                    {
                        result[j] = scalar[j];
                    }
                }
            }
            return result;
        }

        private List<double[,]> GetGLCMList(Fragment fragment, int dx, int dy)
        {
            var result = new List<double[,]>();
            int toX = fragment.fromX + fragment.width;
            int toY = fragment.fromY + fragment.height;
            for (int i = 1; i < RecognitionParameters.GLCMMaxDisplacementDistance; ++i)
            {
                var glcm = new double[RecognitionParameters.GLCMSize, RecognitionParameters.GLCMSize];
                for (int y = fragment.fromY; y < toY; ++y)
                {
                    for (int x = fragment.fromX; x < toX; ++x)
                    {
                        int nx = x + dx * i;
                        int ny = y + dy * i;
                        if (IsInFragment(nx, ny, fragment))
                        {
                            int from = data[x, y];
                            int to = data[nx, ny];
                            glcm[from, to] += 1;
                        }
                    }
                }
                NormalizeGLCM(glcm, dx * i, dy * i);
                result.Add(glcm);
            }
            return result;
        }

        private bool IsInFragment(int x, int y, Fragment fragment)
        {
            return (x >= fragment.fromX) && (x < fragment.fromX + fragment.width) &&
                (y >= fragment.fromY) && (y < fragment.fromY + fragment.height);
        }

        private void NormalizeGLCM(double[,] glcm, int dx, int dy)
        {
            double factor = (RecognitionParameters.FragmentsSize - dx) *
                (RecognitionParameters.FragmentsSize - dy);
            factor = 1 / factor;
            var size = RecognitionParameters.GLCMSize;
            for (int y = 0; y < size; ++y)
            {
                for (int x = 0; x < size; ++x)
                {
                    glcm[x, y] *= factor;
                }
            }
        }

        private double[] GetScalarPropertyFeature(double[,] glcm)
        {
            double homogeneity = 0;
            double contrast = 0;
            double energy = 0;
            double entropy = 0;
            var size = RecognitionParameters.GLCMSize;
            for (int y = 0; y < size; ++y)
            {
                for (int x = 0; x < size; ++x)
                {
                    homogeneity += glcm[x, y] / (1 + Math.Abs(x - y));
                    contrast += MathHelpers.Sqr(x - y) * glcm[x, y];
                    energy += MathHelpers.Sqr(glcm[x, y]);
                    if (glcm[x, y] > 0)
                    {
                        entropy -= glcm[x, y] * Math.Log(glcm[x, y], 2);
                    }
                }
            }
            return new double[] { homogeneity, contrast, energy, entropy };
        }

        public GLCMFeature Feature
        {
            get
            {
                return feature;
            }
        }    
    }
}
