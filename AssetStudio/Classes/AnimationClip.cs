using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AssetStudio
{
    public class Keyframe<T>
    {
        public float time;
        public T value;
        public T inSlope;
        public T outSlope;
        public int weightedMode;
        public T inWeight;
        public T outWeight;


        public Keyframe(ObjectReader reader, Func<T> readerFunc)
        {
            time = reader.ReadSingle();
            value = readerFunc();
            inSlope = readerFunc();
            outSlope = readerFunc();
            if (reader.version[0] >= 2018) //2018 and up
            {
                weightedMode = reader.ReadInt32();
                inWeight = readerFunc();
                outWeight = readerFunc();
            }
        }
    }

    public class AnimationCurve<T>
    {
        public Keyframe<T>[] m_Curve;
        public int m_PreInfinity;
        public int m_PostInfinity;
        public int m_RotationOrder;

        public AnimationCurve(ObjectReader reader, Func<T> readerFunc)
        {
            var version = reader.version;
            int numCurves = reader.ReadInt32();
            // Wrong Unity version → misaligned stream → absurd counts → OutOfMemoryException
            if (numCurves < 0 || numCurves > reader.Remaining)
            {
                throw new InvalidDataException($"Absurd AnimationCurve count {numCurves} (remaining={reader.Remaining}, version={string.Join(".", version)}). Likely wrong/obfuscated Unity version — set Options → Specify Unity version.");
            }
            m_Curve = new Keyframe<T>[numCurves];
            for (int i = 0; i < numCurves; i++)
            {
                m_Curve[i] = new Keyframe<T>(reader, readerFunc);
            }

            m_PreInfinity = reader.ReadInt32();
            m_PostInfinity = reader.ReadInt32();
            if (version[0] > 5 || (version[0] == 5 && version[1] >= 3))//5.3 and up
            {
                m_RotationOrder = reader.ReadInt32();
            }
        }
    }

    public class QuaternionCurve
    {
        public AnimationCurve<Quaternion> curve;
        public string path;

        public QuaternionCurve(ObjectReader reader)
        {
            curve = new AnimationCurve<Quaternion>(reader, reader.ReadQuaternion);
            path = reader.ReadAlignedString();
        }
    }

    public class PackedFloatVector
    {
        public uint m_NumItems;
        public float m_Range;
        public float m_Start;
        public byte[] m_Data;
        public byte m_BitSize;

        public PackedFloatVector(ObjectReader reader)
        {
            m_NumItems = reader.ReadUInt32();
            m_Range = reader.ReadSingle();
            m_Start = reader.ReadSingle();

            int numData = reader.ReadInt32();
            m_Data = reader.ReadBytes(numData);
            reader.AlignStream();

            m_BitSize = reader.ReadByte();
            reader.AlignStream();
        }

        public float[] UnpackFloats(int itemCountInChunk, int chunkStride, int start = 0, int numChunks = -1)
        {
            int bitPos = m_BitSize * start;
            int indexPos = bitPos / 8;
            bitPos %= 8;

            float scale = 1.0f / m_Range;
            if (numChunks == -1)
                numChunks = (int)m_NumItems / itemCountInChunk;
            var end = chunkStride * numChunks / 4;
            var data = new List<float>();
            for (var index = 0; index != end; index += chunkStride / 4)
            {
                for (int i = 0; i < itemCountInChunk; ++i)
                {
                    uint x = 0;

                    int bits = 0;
                    while (bits < m_BitSize)
                    {
                        x |= (uint)((m_Data[indexPos] >> bitPos) << bits);
                        int num = Math.Min(m_BitSize - bits, 8 - bitPos);
                        bitPos += num;
                        bits += num;
                        if (bitPos == 8)
                        {
                            indexPos++;
                            bitPos = 0;
                        }
                    }
                    x &= (uint)(1 << m_BitSize) - 1u;
                    data.Add(x / (scale * ((1 << m_BitSize) - 1)) + m_Start);
                }
            }

            return data.ToArray();
        }
    }

    public class PackedIntVector
    {
        public uint m_NumItems;
        public byte[] m_Data;
        public byte m_BitSize;

        public PackedIntVector(ObjectReader reader)
        {
            m_NumItems = reader.ReadUInt32();

            int numData = reader.ReadInt32();
            m_Data = reader.ReadBytes(numData);
            reader.AlignStream();

            m_BitSize = reader.ReadByte();
            reader.AlignStream();
        }

        public int[] UnpackInts()
        {
            var data = new int[m_NumItems];
            int indexPos = 0;
            int bitPos = 0;
            for (int i = 0; i < m_NumItems; i++)
            {
                int bits = 0;
                data[i] = 0;
                while (bits < m_BitSize)
                {
                    data[i] |= (m_Data[indexPos] >> bitPos) << bits;
                    int num = Math.Min(m_BitSize - bits, 8 - bitPos);
                    bitPos += num;
                    bits += num;
                    if (bitPos == 8)
                    {
                        indexPos++;
                        bitPos = 0;
                    }
                }
                data[i] &= (1 << m_BitSize) - 1;
            }
            return data;
        }
    }

    public class PackedQuatVector
    {
        public uint m_NumItems;
        public byte[] m_Data;

        public PackedQuatVector(ObjectReader reader)
        {
            m_NumItems = reader.ReadUInt32();

            int numData = reader.ReadInt32();
            m_Data = reader.ReadBytes(numData);

            reader.AlignStream();
        }

        public Quaternion[] UnpackQuats()
        {
            var data = new Quaternion[m_NumItems];
            int indexPos = 0;
            int bitPos = 0;

            for (int i = 0; i < m_NumItems; i++)
            {
                uint flags = 0;

                int bits = 0;
                while (bits < 3)
                {
                    flags |= (uint)((m_Data[indexPos] >> bitPos) << bits);
                    int num = Math.Min(3 - bits, 8 - bitPos);
                    bitPos += num;
                    bits += num;
                    if (bitPos == 8)
                    {
                        indexPos++;
                        bitPos = 0;
                    }
                }
                flags &= 7;


                var q = new Quaternion();
                float sum = 0;
                for (int j = 0; j < 4; j++)
                {
                    if ((flags & 3) != j)
                    {
                        int bitSize = ((flags & 3) + 1) % 4 == j ? 9 : 10;
                        uint x = 0;

                        bits = 0;
                        while (bits < bitSize)
                        {
                            x |= (uint)((m_Data[indexPos] >> bitPos) << bits);
                            int num = Math.Min(bitSize - bits, 8 - bitPos);
                            bitPos += num;
                            bits += num;
                            if (bitPos == 8)
                            {
                                indexPos++;
                                bitPos = 0;
                            }
                        }
                        x &= (uint)((1 << bitSize) - 1);
                        q[j] = x / (0.5f * ((1 << bitSize) - 1)) - 1;
                        sum += q[j] * q[j];
                    }
                }

                int lastComponent = (int)(flags & 3);
                q[lastComponent] = (float)Math.Sqrt(1 - sum);
                if ((flags & 4) != 0u)
                    q[lastComponent] = -q[lastComponent];
                data[i] = q;
            }

            return data;
        }
    }

    public class CompressedAnimationCurve
    {
        public string m_Path;
        public PackedIntVector m_Times;
        public PackedQuatVector m_Values;
        public PackedFloatVector m_Slopes;
        public int m_PreInfinity;
        public int m_PostInfinity;

        public CompressedAnimationCurve(ObjectReader reader)
        {
            m_Path = reader.ReadAlignedString();
            m_Times = new PackedIntVector(reader);
            m_Values = new PackedQuatVector(reader);
            m_Slopes = new PackedFloatVector(reader);
            m_PreInfinity = reader.ReadInt32();
            m_PostInfinity = reader.ReadInt32();
        }
    }

    public class Vector3Curve
    {
        public AnimationCurve<Vector3> curve;
        public string path;

        public Vector3Curve(ObjectReader reader)
        {
            curve = new AnimationCurve<Vector3>(reader, reader.ReadVector3);
            path = reader.ReadAlignedString();
        }
    }

    public class FloatCurve
    {
        public AnimationCurve<float> curve;
        public string attribute;
        public string path;
        public ClassIDType classID;
        public PPtr<MonoScript> script;


        public FloatCurve(ObjectReader reader)
        {
            curve = new AnimationCurve<float>(reader, reader.ReadSingle);
            attribute = reader.ReadAlignedString();
            path = reader.ReadAlignedString();
            classID = (ClassIDType)reader.ReadInt32();
            script = new PPtr<MonoScript>(reader);
        }
    }

    public class PPtrKeyframe
    {
        public float time;
        public PPtr<Object> value;


        public PPtrKeyframe(ObjectReader reader)
        {
            time = reader.ReadSingle();
            value = new PPtr<Object>(reader);
        }
    }

    public class PPtrCurve
    {
        public PPtrKeyframe[] curve;
        public string attribute;
        public string path;
        public int classID;
        public PPtr<MonoScript> script;


        public PPtrCurve(ObjectReader reader)
        {
            int numCurves = reader.ReadInt32();
            curve = new PPtrKeyframe[numCurves];
            for (int i = 0; i < numCurves; i++)
            {
                curve[i] = new PPtrKeyframe(reader);
            }

            attribute = reader.ReadAlignedString();
            path = reader.ReadAlignedString();
            classID = reader.ReadInt32();
            script = new PPtr<MonoScript>(reader);
        }
    }

    public class AABB
    {
        public Vector3 m_Center;
        public Vector3 m_Extent;

        public AABB(ObjectReader reader)
        {
            m_Center = reader.ReadVector3();
            m_Extent = reader.ReadVector3();
        }
    }

    public class xform
    {
        public Vector3 t;
        public Quaternion q;
        public Vector3 s;

        public xform(ObjectReader reader)
        {
            var version = reader.version;
            t = version[0] > 5 || (version[0] == 5 && version[1] >= 4) ? reader.ReadVector3() : (Vector3)reader.ReadVector4();//5.4 and up
            q = reader.ReadQuaternion();
            s = version[0] > 5 || (version[0] == 5 && version[1] >= 4) ? reader.ReadVector3() : (Vector3)reader.ReadVector4();//5.4 and up
        }
    }

    public class HandPose
    {
        public xform m_GrabX;
        public float[] m_DoFArray;
        public float m_Override;
        public float m_CloseOpen;
        public float m_InOut;
        public float m_Grab;

        public HandPose(ObjectReader reader)
        {
            m_GrabX = new xform(reader);
            m_DoFArray = reader.ReadSingleArray();
            m_Override = reader.ReadSingle();
            m_CloseOpen = reader.ReadSingle();
            m_InOut = reader.ReadSingle();
            m_Grab = reader.ReadSingle();
        }
    }

    public class HumanGoal
    {
        public xform m_X;
        public float m_WeightT;
        public float m_WeightR;
        public Vector3 m_HintT;
        public float m_HintWeightT;

        public HumanGoal(ObjectReader reader)
        {
            var version = reader.version;
            m_X = new xform(reader);
            m_WeightT = reader.ReadSingle();
            m_WeightR = reader.ReadSingle();
            if (version[0] >= 5)//5.0 and up
            {
                m_HintT = version[0] > 5 || (version[0] == 5 && version[1] >= 4) ? reader.ReadVector3() : (Vector3)reader.ReadVector4();//5.4 and up
                m_HintWeightT = reader.ReadSingle();
            }
        }
    }

    public class HumanPose
    {
        public xform m_RootX;
        public Vector3 m_LookAtPosition;
        public Vector4 m_LookAtWeight;
        public HumanGoal[] m_GoalArray;
        public HandPose m_LeftHandPose;
        public HandPose m_RightHandPose;
        public float[] m_DoFArray;
        public Vector3[] m_TDoFArray;

        public HumanPose(ObjectReader reader)
        {
            var version = reader.version;
            m_RootX = new xform(reader);
            m_LookAtPosition = version[0] > 5 || (version[0] == 5 && version[1] >= 4) ? reader.ReadVector3() : (Vector3)reader.ReadVector4();//5.4 and up
            m_LookAtWeight = reader.ReadVector4();

            int numGoals = reader.ReadInt32();
            if (numGoals < 0 || numGoals > 64)
            {
                throw new InvalidDataException($"Absurd HumanGoal count {numGoals}");
            }
            m_GoalArray = new HumanGoal[numGoals];
            for (int i = 0; i < numGoals; i++)
            {
                m_GoalArray[i] = new HumanGoal(reader);
            }

            m_LeftHandPose = new HandPose(reader);
            m_RightHandPose = new HandPose(reader);

            int numDof = reader.ReadInt32();
            if (numDof < 0 || numDof > reader.Remaining / 4)
            {
                throw new InvalidDataException($"Absurd HumanPose DoF count {numDof} (remaining={reader.Remaining})");
            }
            m_DoFArray = new float[numDof];
            for (int i = 0; i < numDof; i++)
            {
                m_DoFArray[i] = reader.ReadSingle();
            }

            if (version[0] > 5 || (version[0] == 5 && version[1] >= 2))//5.2 and up
            {
                int numTDof = reader.ReadInt32();
                if (numTDof < 0 || numTDof > 1024)
                {
                    throw new InvalidDataException($"Absurd HumanPose TDoF count {numTDof}");
                }
                m_TDoFArray = new Vector3[numTDof];
                for (int i = 0; i < numTDof; i++)
                {
                    m_TDoFArray[i] = version[0] > 5 || (version[0] == 5 && version[1] >= 4) ? reader.ReadVector3() : (Vector3)reader.ReadVector4();//5.4 and up
                }
            }
        }
    }

    public class StreamedClip
    {
        public uint[] data;
        public uint curveCount;

        public StreamedClip(ObjectReader reader)
        {
            int numData = reader.ReadInt32();
            if (numData < 0 || numData > reader.Remaining / 4)
            {
                throw new InvalidDataException($"Absurd StreamedClip data count {numData} (remaining={reader.Remaining})");
            }
            data = new uint[numData];
            for (int i = 0; i < numData; i++)
            {
                data[i] = reader.ReadUInt32();
            }
            curveCount = reader.ReadUInt32();
        }

        public class StreamedCurveKey
        {
            public int index;
            public float[] coeff;

            public float value;
            public float outSlope;
            public float inSlope;

            public StreamedCurveKey(BinaryReader reader)
            {
                index = reader.ReadInt32();
                coeff = reader.ReadSingleArray(4);

                outSlope = coeff[2];
                value = coeff[3];
            }

            public float CalculateNextInSlope(float dx, StreamedCurveKey rhs)
            {
                //Stepped
                if (coeff[0] == 0f && coeff[1] == 0f && coeff[2] == 0f)
                {
                    return float.PositiveInfinity;
                }

                dx = Math.Max(dx, 0.0001f);
                var dy = rhs.value - value;
                var length = 1.0f / (dx * dx);
                var d1 = outSlope * dx;
                var d2 = dy + dy + dy - d1 - d1 - coeff[1] / length;
                return d2 / dx;
            }
        }

        public class StreamedFrame
        {
            public float time;
            public StreamedCurveKey[] keyList;

            public StreamedFrame(BinaryReader reader)
            {
                time = reader.ReadSingle();

                int numKeys = reader.ReadInt32();
                var remaining = reader.BaseStream.Length - reader.BaseStream.Position;
                // StreamedCurveKey ≈ 4 + 16 bytes
                if (numKeys < 0 || numKeys > remaining / 16)
                {
                    throw new InvalidDataException($"Absurd StreamedFrame key count {numKeys} (remaining={remaining})");
                }
                keyList = new StreamedCurveKey[numKeys];
                for (int i = 0; i < numKeys; i++)
                {
                    keyList[i] = new StreamedCurveKey(reader);
                }
            }
        }

        public List<StreamedFrame> ReadData()
        {
            var frameList = new List<StreamedFrame>();
            var buffer = new byte[data.Length * 4];
            Buffer.BlockCopy(data, 0, buffer, 0, buffer.Length);
            using (var reader = new BinaryReader(new MemoryStream(buffer)))
            {
                while (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    frameList.Add(new StreamedFrame(reader));
                }
            }

            for (int frameIndex = 2; frameIndex < frameList.Count - 1; frameIndex++)
            {
                var frame = frameList[frameIndex];
                foreach (var curveKey in frame.keyList)
                {
                    for (int i = frameIndex - 1; i >= 0; i--)
                    {
                        var preFrame = frameList[i];
                        var preCurveKey = preFrame.keyList.FirstOrDefault(x => x.index == curveKey.index);
                        if (preCurveKey != null)
                        {
                            curveKey.inSlope = preCurveKey.CalculateNextInSlope(frame.time - preFrame.time, curveKey);
                            break;
                        }
                    }
                }
            }
            return frameList;
        }
    }

    public class DenseClip
    {
        public int m_FrameCount;
        public uint m_CurveCount;
        public float m_SampleRate;
        public float m_BeginTime;
        public float[] m_SampleArray;
        /// <summary>Unsanitized CODM quantized samples (set by CodmQuantizedDense); used to rewindow at export.</summary>
        public float[] m_CodmRawSampleArray;
        /// <summary>Bytes after last quant block (still undecoded). Diagnostics only.</summary>
        public byte[] m_CodmTrailer;
        /// <summary>Per-block channel counts from CODM quant decode (shared min/max within each block).</summary>
        public int[] m_CodmBlockChannelCounts;
        /// <summary>Per-block dequant min (parallel to channel counts).</summary>
        public float[] m_CodmBlockMins;
        /// <summary>Per-block dequant max.</summary>
        public float[] m_CodmBlockMaxs;
        /// <summary>Per-block packed size field (nb) from CODM quant header.</summary>
        public uint[] m_CodmBlockPackedSizes;
        /// <summary>Per-block flag field (expected 3).</summary>
        public uint[] m_CodmBlockFlags;
        /// <summary>
        /// Span multiplier applied to 3ch blocks at hydrate (1 = full header span, 0.5 = half).
        /// Used to re-apply / undo without reloading the pak.
        /// </summary>
        public float m_Codm3chSpanFactor = 1f;
        public int m_CodmPrefixLength;
        public int m_CodmPayloadOffset;
        public int m_CodmBlocksEnd;

        public DenseClip(ObjectReader reader)
        {
            m_FrameCount = reader.ReadInt32();
            m_CurveCount = reader.ReadUInt32();
            m_SampleRate = reader.ReadSingle();
            m_BeginTime = reader.ReadSingle();
            int numSamples = reader.ReadInt32();
            if (numSamples < 0 || numSamples > reader.Remaining / 4)
            {
                throw new InvalidDataException($"Absurd DenseClip sample count {numSamples} (remaining={reader.Remaining})");
            }
            m_SampleArray = new float[numSamples];
            for (int i = 0; i < numSamples; i++)
            {
                m_SampleArray[i] = reader.ReadSingle();
            }
        }
    }

    public class ConstantClip
    {
        public float[] data;

        public ConstantClip(ObjectReader reader)
        {
            int numData = reader.ReadInt32();
            if (numData < 0 || numData > reader.Remaining / 4)
            {
                throw new InvalidDataException($"Absurd ConstantClip data count {numData} (remaining={reader.Remaining})");
            }
            data = new float[numData];
            for (int i = 0; i < numData; i++)
            {
                data[i] = reader.ReadSingle();
            }
        }
    }

    public class ValueConstant
    {
        public uint m_ID;
        public uint m_TypeID;
        public uint m_Type;
        public uint m_Index;

        public ValueConstant(ObjectReader reader)
        {
            var version = reader.version;
            m_ID = reader.ReadUInt32();
            if (version[0] < 5 || (version[0] == 5 && version[1] < 5))//5.5 down
            {
                m_TypeID = reader.ReadUInt32();
            }
            m_Type = reader.ReadUInt32();
            m_Index = reader.ReadUInt32();
        }
    }

    public class ValueArrayConstant
    {
        public ValueConstant[] m_ValueArray;

        public ValueArrayConstant(ObjectReader reader)
        {
            int numVals = reader.ReadInt32();
            if (numVals < 0 || numVals > reader.Remaining / 12)
            {
                throw new InvalidDataException($"Absurd ValueArrayConstant count {numVals} (remaining={reader.Remaining})");
            }
            m_ValueArray = new ValueConstant[numVals];
            for (int i = 0; i < numVals; i++)
            {
                m_ValueArray[i] = new ValueConstant(reader);
            }
        }
    }

    public class Clip
    {
        public StreamedClip m_StreamedClip;
        public DenseClip m_DenseClip;
        public ConstantClip m_ConstantClip;
        public ValueArrayConstant m_Binding;

        public Clip(ObjectReader reader)
        {
            var version = reader.version;
            m_StreamedClip = new StreamedClip(reader);
            m_DenseClip = new DenseClip(reader);
            if (version[0] > 4 || (version[0] == 4 && version[1] >= 3)) //4.3 and up
            {
                m_ConstantClip = new ConstantClip(reader);
            }
            if (version[0] < 2018 || (version[0] == 2018 && version[1] < 3)) //2018.3 down
            {
                m_Binding = new ValueArrayConstant(reader);
            }
        }

        public AnimationClipBindingConstant ConvertValueArrayToGenericBinding()
        {
            var bindings = new AnimationClipBindingConstant();
            var genericBindings = new List<GenericBinding>();
            var values = m_Binding;
            if (values?.m_ValueArray == null || values.m_ValueArray.Length == 0)
            {
                bindings.genericBindings = Array.Empty<GenericBinding>();
                bindings.pptrCurveMapping = Array.Empty<PPtr<Object>>();
                return bindings;
            }
            for (int i = 0; i < values.m_ValueArray.Length;)
            {
                var curveID = values.m_ValueArray[i].m_ID;
                var curveTypeID = values.m_ValueArray[i].m_TypeID;
                var binding = new GenericBinding();
                genericBindings.Add(binding);
                if (curveTypeID == 4174552735) //CRC(PositionX))
                {
                    binding.path = curveID;
                    binding.attribute = 1; //kBindTransformPosition
                    binding.typeID = ClassIDType.Transform;
                    i += 3;
                }
                else if (curveTypeID == 2211994246) //CRC(QuaternionX))
                {
                    binding.path = curveID;
                    binding.attribute = 2; //kBindTransformRotation
                    binding.typeID = ClassIDType.Transform;
                    i += 4;
                }
                else if (curveTypeID == 1512518241) //CRC(ScaleX))
                {
                    binding.path = curveID;
                    binding.attribute = 3; //kBindTransformScale
                    binding.typeID = ClassIDType.Transform;
                    i += 3;
                }
                else
                {
                    binding.typeID = ClassIDType.Animator;
                    binding.path = 0;
                    binding.attribute = curveID;
                    i++;
                }
            }
            bindings.genericBindings = genericBindings.ToArray();
            bindings.pptrCurveMapping = Array.Empty<PPtr<Object>>();
            return bindings;
        }
    }

    public class ValueDelta
    {
        public float m_Start;
        public float m_Stop;

        public ValueDelta(ObjectReader reader)
        {
            m_Start = reader.ReadSingle();
            m_Stop = reader.ReadSingle();
        }
    }

    public class ClipMuscleConstant
    {
        public HumanPose m_DeltaPose;
        public xform m_StartX;
        public xform m_StopX;
        public xform m_LeftFootStartX;
        public xform m_RightFootStartX;
        public xform m_MotionStartX;
        public xform m_MotionStopX;
        public Vector3 m_AverageSpeed;
        public Clip m_Clip;
        public float m_StartTime;
        public float m_StopTime;
        public float m_OrientationOffsetY;
        public float m_Level;
        public float m_CycleOffset;
        public float m_AverageAngularSpeed;
        public int[] m_IndexArray;
        public ValueDelta[] m_ValueArrayDelta;
        public float[] m_ValueArrayReferencePose;
        public bool m_Mirror;
        public bool m_LoopTime;
        public bool m_LoopBlend;
        public bool m_LoopBlendOrientation;
        public bool m_LoopBlendPositionY;
        public bool m_LoopBlendPositionXZ;
        public bool m_StartAtOrigin;
        public bool m_KeepOriginalOrientation;
        public bool m_KeepOriginalPositionY;
        public bool m_KeepOriginalPositionXZ;
        public bool m_HeightFromFeet;

        public ClipMuscleConstant(ObjectReader reader)
        {
            var version = reader.version;
            // CoD Mobile's modified Unity 5.6 replaces the full HumanPose + foot xforms
            // with two xforms (delta root + start), then average speed + Clip.
            // Stock HumanPose reads look-at/goal arrays and OOMs on this data.
            if (IsCodmMuscleLayout(version))
            {
                Logger.Info("[AnimClip] Using CoD Mobile ClipMuscleConstant layout (2 xforms, no HumanPose)");
                m_StartX = new xform(reader); // delta/root pose (not a full HumanPose)
                m_StopX = new xform(reader);  // start X (reusing field)
                m_AverageSpeed = reader.ReadVector3();
            }
            else
            {
                m_DeltaPose = new HumanPose(reader);
                m_StartX = new xform(reader);
                if (version[0] > 5 || (version[0] == 5 && version[1] >= 5))//5.5 and up
                {
                    m_StopX = new xform(reader);
                }
                m_LeftFootStartX = new xform(reader);
                m_RightFootStartX = new xform(reader);
                if (version[0] < 5)//5.0 down
                {
                    m_MotionStartX = new xform(reader);
                    m_MotionStopX = new xform(reader);
                }
                m_AverageSpeed = version[0] > 5 || (version[0] == 5 && version[1] >= 4) ? reader.ReadVector3() : (Vector3)reader.ReadVector4();//5.4 and up
            }
            m_Clip = new Clip(reader);
            m_StartTime = reader.ReadSingle();
            m_StopTime = reader.ReadSingle();
            m_OrientationOffsetY = reader.ReadSingle();
            m_Level = reader.ReadSingle();
            m_CycleOffset = reader.ReadSingle();
            m_AverageAngularSpeed = reader.ReadSingle();

            int numIndices = reader.ReadInt32();
            if (numIndices < 0 || numIndices > reader.Remaining / 4)
            {
                throw new InvalidDataException($"Absurd IndexArray count {numIndices} (remaining={reader.Remaining})");
            }
            m_IndexArray = new int[numIndices];
            for (int i = 0; i < numIndices; i++)
            {
                m_IndexArray[i] = reader.ReadInt32();
            }
            if (version[0] < 4 || (version[0] == 4 && version[1] < 3)) //4.3 down
            {
                var m_AdditionalCurveIndexArray = reader.ReadInt32Array();
            }
            int numDeltas = reader.ReadInt32();
            if (numDeltas < 0 || numDeltas > reader.Remaining / 8)
            {
                throw new InvalidDataException($"Absurd ValueArrayDelta count {numDeltas} (remaining={reader.Remaining})");
            }
            m_ValueArrayDelta = new ValueDelta[numDeltas];
            for (int i = 0; i < numDeltas; i++)
            {
                m_ValueArrayDelta[i] = new ValueDelta(reader);
            }
            if (version[0] > 5 || (version[0] == 5 && version[1] >= 3))//5.3 and up
            {
                m_ValueArrayReferencePose = ReadBoundedSingleArray(reader);
            }

            m_Mirror = reader.ReadBoolean();
            if (version[0] > 4 || (version[0] == 4 && version[1] >= 3)) //4.3 and up
            {
                m_LoopTime = reader.ReadBoolean();
            }
            m_LoopBlend = reader.ReadBoolean();
            m_LoopBlendOrientation = reader.ReadBoolean();
            m_LoopBlendPositionY = reader.ReadBoolean();
            m_LoopBlendPositionXZ = reader.ReadBoolean();
            if (version[0] > 5 || (version[0] == 5 && version[1] >= 5))//5.5 and up
            {
                m_StartAtOrigin = reader.ReadBoolean();
            }
            m_KeepOriginalOrientation = reader.ReadBoolean();
            m_KeepOriginalPositionY = reader.ReadBoolean();
            m_KeepOriginalPositionXZ = reader.ReadBoolean();
            m_HeightFromFeet = reader.ReadBoolean();
            reader.AlignStream();

            // CODM trailing times often read as 0; derive a usable stop time from dense clip.
            if (IsCodmMuscleLayout(version) && m_StopTime <= 0f && m_Clip?.m_DenseClip != null
                && m_Clip.m_DenseClip.m_FrameCount > 0 && m_Clip.m_DenseClip.m_SampleRate > 0f)
            {
                m_StopTime = m_Clip.m_DenseClip.m_FrameCount / m_Clip.m_DenseClip.m_SampleRate;
            }
        }

        private static bool IsCodmMuscleLayout(int[] version)
        {
            // This fork targets CoD Mobile's modified Unity 5.6 engine.
            return version != null && version.Length > 1 && version[0] == 5 && version[1] >= 6;
        }

        private static float[] ReadBoundedSingleArray(ObjectReader reader)
        {
            int length = reader.ReadInt32();
            if (length < 0 || length > reader.Remaining / 4)
            {
                throw new InvalidDataException($"Absurd float array length {length} (remaining={reader.Remaining})");
            }
            var result = new float[length];
            for (int i = 0; i < length; i++)
            {
                result[i] = reader.ReadSingle();
            }
            return result;
        }
    }

    public class GenericBinding
    {
        public uint path;
        public uint attribute;
        public PPtr<Object> script;
        public ClassIDType typeID;
        public byte customType;
        public byte isPPtrCurve;
        public byte isIntCurve;

        public GenericBinding() { }

        public GenericBinding(ObjectReader reader)
        {
            var version = reader.version;
            path = reader.ReadUInt32();
            attribute = reader.ReadUInt32();
            script = new PPtr<Object>(reader);
            if (version[0] > 5 || (version[0] == 5 && version[1] >= 6)) //5.6 and up
            {
                typeID = (ClassIDType)reader.ReadInt32();
            }
            else
            {
                typeID = (ClassIDType)reader.ReadUInt16();
            }
            customType = reader.ReadByte();
            isPPtrCurve = reader.ReadByte();
            if (version[0] > 2022 || (version[0] == 2022 && version[1] >= 1)) //2022.1 and up
            {
                isIntCurve = reader.ReadByte();
            }
            reader.AlignStream();
        }
    }

    public class AnimationClipBindingConstant
    {
        public GenericBinding[] genericBindings;
        public PPtr<Object>[] pptrCurveMapping;

        public AnimationClipBindingConstant() { }

        public AnimationClipBindingConstant(ObjectReader reader)
        {
            int numBindings = reader.ReadInt32();
            if (numBindings < 0 || numBindings > 4096)
            {
                throw new InvalidDataException($"Absurd GenericBinding count {numBindings}");
            }
            genericBindings = new GenericBinding[numBindings];
            for (int i = 0; i < numBindings; i++)
            {
                genericBindings[i] = new GenericBinding(reader);
            }

            int numMappings = reader.ReadInt32();
            if (numMappings < 0 || numMappings > 4096 || numMappings > reader.Remaining / 8)
            {
                throw new InvalidDataException($"Absurd pptrCurveMapping count {numMappings} (remaining={reader.Remaining})");
            }
            pptrCurveMapping = new PPtr<Object>[numMappings];
            for (int i = 0; i < numMappings; i++)
            {
                pptrCurveMapping[i] = new PPtr<Object>(reader);
            }
        }

        public GenericBinding FindBinding(int index)
        {
            int curves = 0;
            foreach (var b in genericBindings)
            {
                if (b.typeID == ClassIDType.Transform)
                {
                    switch (b.attribute)
                    {
                        case 1: //kBindTransformPosition
                        case 3: //kBindTransformScale
                        case 4: //kBindTransformEuler
                            curves += 3;
                            break;
                        case 2: //kBindTransformRotation
                            curves += 4;
                            break;
                        default:
                            curves += 1;
                            break;
                    }
                }
                else
                {
                    curves += 1;
                }
                if (curves > index)
                {
                    return b;
                }
            }

            return null;
        }
    }

    public class AnimationEvent
    {
        public float time;
        public string functionName;
        public string data;
        public PPtr<Object> objectReferenceParameter;
        public float floatParameter;
        public int intParameter;
        public int messageOptions;

        public AnimationEvent(ObjectReader reader)
        {
            var version = reader.version;

            time = reader.ReadSingle();
            functionName = reader.ReadAlignedString();
            data = reader.ReadAlignedString();
            objectReferenceParameter = new PPtr<Object>(reader);
            floatParameter = reader.ReadSingle();
            if (version[0] >= 3) //3 and up
            {
                intParameter = reader.ReadInt32();
            }
            messageOptions = reader.ReadInt32();
        }
    }

    public enum AnimationType
    {
        Legacy = 1,
        Generic = 2,
        Humanoid = 3
    };

    public sealed class AnimationClip : NamedObject
    {
        public AnimationType m_AnimationType;
        public bool m_Legacy;
        public bool m_Compressed;
        public bool m_UseHighQualityCurve;
        public QuaternionCurve[] m_RotationCurves;
        public CompressedAnimationCurve[] m_CompressedRotationCurves;
        public Vector3Curve[] m_EulerCurves;
        public Vector3Curve[] m_PositionCurves;
        public Vector3Curve[] m_ScaleCurves;
        public FloatCurve[] m_FloatCurves;
        public PPtrCurve[] m_PPtrCurves;
        public float m_SampleRate;
        public int m_WrapMode;
        public AABB m_Bounds;
        public uint m_MuscleClipSize;
        public ClipMuscleConstant m_MuscleClip;
        public AnimationClipBindingConstant m_ClipBindingConstant;
        public AnimationEvent[] m_Events;


        public AnimationClip(ObjectReader reader) : base(reader)
        {
            if (version[0] >= 5)//5.0 and up
            {
                m_Legacy = reader.ReadBoolean();
            }
            else if (version[0] >= 4)//4.0 and up
            {
                m_AnimationType = (AnimationType)reader.ReadInt32();
                if (m_AnimationType == AnimationType.Legacy)
                    m_Legacy = true;
            }
            else
            {
                m_Legacy = true;
            }
            m_Compressed = reader.ReadBoolean();
            if (version[0] > 4 || (version[0] == 4 && version[1] >= 3))//4.3 and up
            {
                m_UseHighQualityCurve = reader.ReadBoolean();
            }
            reader.AlignStream();
            int numRCurves = reader.ReadInt32();
            m_RotationCurves = new QuaternionCurve[numRCurves];
            for (int i = 0; i < numRCurves; i++)
            {
                m_RotationCurves[i] = new QuaternionCurve(reader);
            }

            int numCRCurves = reader.ReadInt32();
            m_CompressedRotationCurves = new CompressedAnimationCurve[numCRCurves];
            for (int i = 0; i < numCRCurves; i++)
            {
                m_CompressedRotationCurves[i] = new CompressedAnimationCurve(reader);
            }

            if (version[0] > 5 || (version[0] == 5 && version[1] >= 3))//5.3 and up
            {
                int numEulerCurves = reader.ReadInt32();
                m_EulerCurves = new Vector3Curve[numEulerCurves];
                for (int i = 0; i < numEulerCurves; i++)
                {
                    m_EulerCurves[i] = new Vector3Curve(reader);
                }
            }

            int numPCurves = reader.ReadInt32();
            m_PositionCurves = new Vector3Curve[numPCurves];
            for (int i = 0; i < numPCurves; i++)
            {
                m_PositionCurves[i] = new Vector3Curve(reader);
            }

            int numSCurves = reader.ReadInt32();
            m_ScaleCurves = new Vector3Curve[numSCurves];
            for (int i = 0; i < numSCurves; i++)
            {
                m_ScaleCurves[i] = new Vector3Curve(reader);
            }

            int numFCurves = reader.ReadInt32();
            m_FloatCurves = new FloatCurve[numFCurves];
            for (int i = 0; i < numFCurves; i++)
            {
                m_FloatCurves[i] = new FloatCurve(reader);
            }

            if (version[0] > 4 || (version[0] == 4 && version[1] >= 3)) //4.3 and up
            {
                int numPtrCurves = reader.ReadInt32();
                m_PPtrCurves = new PPtrCurve[numPtrCurves];
                for (int i = 0; i < numPtrCurves; i++)
                {
                    m_PPtrCurves[i] = new PPtrCurve(reader);
                }
            }

            m_SampleRate = reader.ReadSingle();
            m_WrapMode = reader.ReadInt32();
            if (version[0] > 3 || (version[0] == 3 && version[1] >= 4)) //3.4 and up
            {
                m_Bounds = new AABB(reader);
            }
            if (version[0] >= 4)//4.0 and up
            {
                m_MuscleClipSize = reader.ReadUInt32();
                var muscleDataStart = reader.Position;
                m_MuscleClip = new ClipMuscleConstant(reader);
                var afterMuscleStructural = reader.Position;

                if (version[0] > 4 || (version[0] == 4 && version[1] >= 3)) //4.3 and up
                {
                    if (version[0] == 5 && version[1] >= 6)
                    {
                        var muscleHasCurves = MuscleHasCurveData(m_MuscleClip);
                        var bySizePos = muscleDataStart + m_MuscleClipSize;
                        var objectEnd = reader.byteStart + reader.byteSize;
                        var synced = false;
                        if (m_MuscleClipSize > 0 && bySizePos >= afterMuscleStructural && bySizePos + 8 <= objectEnd)
                        {
                            reader.Position = bySizePos;
                            if (TryPeekBindingList(reader, out _))
                            {
                                Logger.Info($"[AnimClip] CODM binding sync via muscleClipSize at 0x{bySizePos - reader.byteStart:X}");
                                synced = true;
                            }
                        }
                        if (!synced)
                        {
                            reader.Position = afterMuscleStructural;
                            if (!TrySeekToAnimationClipBindingConstant(reader, allowEmpty: !muscleHasCurves))
                            {
                                Logger.Warning(
                                    $"[AnimClip] CODM binding sync missed (PathID={reader.m_PathID}, muscleCurves={muscleHasCurves}); " +
                                    "rebuilding ClipBindingConstant from muscle ValueArray if present");
                                m_ClipBindingConstant = RebuildBindingsOrEmpty(m_MuscleClip);
                                TrySeekToAnimationEvents(reader);
                                goto AfterBindings;
                            }
                        }
                    }
                    var bindingPos = reader.Position;
                    m_ClipBindingConstant = new AnimationClipBindingConstant(reader);
                    if (m_ClipBindingConstant.genericBindings == null || m_ClipBindingConstant.genericBindings.Length == 0)
                    {
                        var rebuilt = RebuildBindingsOrEmpty(m_MuscleClip);
                        if (rebuilt.genericBindings != null && rebuilt.genericBindings.Length > 0)
                        {
                            Logger.Warning(
                                $"[AnimClip] ClipBindingConstant empty after sync (PathID={reader.m_PathID}); " +
                                $"using {rebuilt.genericBindings.Length} bindings from muscle ValueArray");
                            m_ClipBindingConstant = rebuilt;
                        }
                        else if (MuscleHasCurveData(m_MuscleClip))
                        {
                            Logger.Warning(
                                $"[AnimClip] PathID={reader.m_PathID}: muscle has curve data but 0 bindings " +
                                $"(streamed={m_MuscleClip.m_Clip?.m_StreamedClip?.curveCount}, " +
                                $"dense={m_MuscleClip.m_Clip?.m_DenseClip?.m_CurveCount}, " +
                                $"constant={m_MuscleClip.m_Clip?.m_ConstantClip?.data?.Length}) — export will look static/empty");
                        }
                    }
                    if (version[0] == 5 && version[1] >= 6)
                    {
                        TryApplyCodmQuantizedDense(reader, muscleDataStart, bindingPos, m_MuscleClip, m_ClipBindingConstant);
                    }
                    WarnIfConstantOnlyWithUnreadMuscle(reader.m_PathID, m_MuscleClipSize, afterMuscleStructural, bindingPos, m_MuscleClip);
                }
            }
            else if (version[0] > 4 || (version[0] == 4 && version[1] >= 3)) //4.3 and up, no muscle block
            {
                m_ClipBindingConstant = new AnimationClipBindingConstant(reader);
            }
        AfterBindings:
            if (version[0] > 2018 || (version[0] == 2018 && version[1] >= 3)) //2018.3 and up
            {
                var m_HasGenericRootTransform = reader.ReadBoolean();
                var m_HasMotionFloatCurves = reader.ReadBoolean();
                reader.AlignStream();
            }
            int numEvents = reader.ReadInt32();
            if (numEvents < 0 || numEvents > 4096 || numEvents > reader.Remaining / 8)
            {
                Logger.Warning($"[AnimClip] Absurd event count {numEvents} (PathID={reader.m_PathID}); treating as no events");
                m_Events = Array.Empty<AnimationEvent>();
            }
            else
            {
                m_Events = new AnimationEvent[numEvents];
                for (int i = 0; i < numEvents; i++)
                {
                    m_Events[i] = new AnimationEvent(reader);
                }
            }
            if (version[0] >= 2017) //2017 and up
            {
                reader.AlignStream();
            }
        }

        private static AnimationClipBindingConstant RebuildBindingsOrEmpty(ClipMuscleConstant muscle)
        {
            var rebuilt = muscle?.m_Clip?.ConvertValueArrayToGenericBinding();
            if (rebuilt?.genericBindings != null && rebuilt.genericBindings.Length > 0)
                return rebuilt;
            return new AnimationClipBindingConstant
            {
                genericBindings = Array.Empty<GenericBinding>(),
                pptrCurveMapping = Array.Empty<PPtr<Object>>()
            };
        }

        private static bool MuscleHasCurveData(ClipMuscleConstant muscle)
        {
            var clip = muscle?.m_Clip;
            if (clip == null) return false;
            if ((clip.m_StreamedClip?.curveCount ?? 0) > 0) return true;
            if ((clip.m_DenseClip?.m_CurveCount ?? 0) > 0) return true;
            if ((clip.m_ConstantClip?.data?.Length ?? 0) > 0) return true;
            return false;
        }

        /// <summary>
        /// CODM often stores a large undecoded blob between ClipMuscleConstant and
        /// ClipBindingConstant. When the in-Clip StreamedClip is empty, export collapses
        /// to ConstantClip (static pose) even though muscleClipSize implies more data.
        /// </summary>
        private static int _codmExtWarnCount;

        private static void TryApplyCodmQuantizedDense(
            ObjectReader reader, long muscleDataStart, long bindingPos,
            ClipMuscleConstant muscle, AnimationClipBindingConstant bindings)
        {
            if (bindingPos <= muscleDataStart || bindingPos - muscleDataStart > reader.byteSize)
                return;
            var len = (int)(bindingPos - muscleDataStart);
            if (len < 64)
                return;
            var saved = reader.Position;
            try
            {
                reader.Position = muscleDataStart;
                var region = reader.ReadBytes(len);
                CodmQuantizedDense.TryApply(region, muscle, bindings);
            }
            catch (Exception e)
            {
                Logger.Warning($"[AnimClip] CODM quant dense decode failed (PathID={reader.m_PathID}): {e.Message}");
            }
            finally
            {
                reader.Position = saved;
            }
        }

        private static void WarnIfConstantOnlyWithUnreadMuscle(
            long pathId, uint muscleClipSize, long afterMuscleStructural, long bindingPos, ClipMuscleConstant muscle)
        {
            var streamed = muscle?.m_Clip?.m_StreamedClip;
            var dense = muscle?.m_Clip?.m_DenseClip;
            var constantLen = muscle?.m_Clip?.m_ConstantClip?.data?.Length ?? 0;
            var streamedCurves = streamed?.curveCount ?? 0;
            var streamedData = streamed?.data?.Length ?? 0;
            // Successfully hydrated dense samples from the CODM extension.
            if (dense != null && dense.m_CurveCount > 0 && dense.m_SampleArray != null && dense.m_SampleArray.Length > 0)
                return;
            if (streamedCurves > 0 || streamedData > 2)
                return;
            if (constantLen <= 0)
                return;
            var gap = bindingPos - afterMuscleStructural;
            if (gap < 512 && muscleClipSize < 2048)
                return;

            _codmExtWarnCount++;
            if (_codmExtWarnCount <= 3)
            {
                Logger.Warning(
                    $"[AnimClip] PathID={pathId}: StreamedClip empty (data={streamedData},curves={streamedCurves}) but " +
                    $"~{gap} bytes sit between muscle struct and bindings (muscleSize={muscleClipSize}, " +
                    $"denseFrames={dense?.m_FrameCount}, constant={constantLen}). " +
                    "CODM extension animation data is not decoded yet — export will look static (constant pose only).");
            }
            else if (_codmExtWarnCount == 4)
            {
                Logger.Warning(
                    "[AnimClip] Further CODM-extension static-clip warnings suppressed this session. " +
                    "Clips with large StreamedClip (e.g. FireLeft/FireJump) are unaffected.");
            }
        }

        private static bool TryPeekBindingList(ObjectReader reader, out int count)
        {
            count = 0;
            var objectEnd = reader.byteStart + reader.byteSize;
            if (reader.Position + 8 > objectEnd) return false;
            var pos = reader.Position;
            try
            {
                count = reader.ReadInt32();
                if (count < 1 || count > 512 || reader.Position + 28 > objectEnd)
                    return false;
                reader.ReadUInt32();
                uint attr = reader.ReadUInt32();
                _ = new PPtr<Object>(reader);
                int typeId = reader.ReadInt32();
                return IsPlausibleGenericBinding(typeId, attr);
            }
            catch
            {
                return false;
            }
            finally
            {
                reader.Position = pos;
            }
        }

        /// <summary>
        /// CoD Mobile: after ClipMuscleConstant there may be extra unknown data before
        /// AnimationClipBindingConstant. Prefer a non-empty plausible binding list.
        /// Empty lists are only accepted when <paramref name="allowEmpty"/> is true
        /// (no muscle curve data) — otherwise we get static first/last-frame exports.
        /// </summary>
        private static bool TrySeekToAnimationClipBindingConstant(ObjectReader reader, bool allowEmpty = true)
        {
            var objectEnd = reader.byteStart + reader.byteSize;
            var seekStart = reader.Position;
            long? emptyTailPos = null;

            reader.Position = seekStart;
            while (reader.Position + 8 <= objectEnd)
            {
                var pos = reader.Position;
                int count = reader.ReadInt32();

                if (count >= 1 && count <= 512 && reader.Position + 28 <= objectEnd)
                {
                    var peekPos = reader.Position;
                    try
                    {
                        reader.ReadUInt32(); // path
                        uint attr = reader.ReadUInt32();
                        _ = new PPtr<Object>(reader);
                        int typeId = reader.ReadInt32();
                        if (IsPlausibleGenericBinding(typeId, attr))
                        {
                            reader.Position = pos;
                            Logger.Info($"[AnimClip] CODM binding sync at 0x{pos - reader.byteStart:X} (count={count}, typeID={typeId}, attr={attr})");
                            return true;
                        }
                    }
                    catch
                    {
                        // peek failed — keep scanning
                    }
                    reader.Position = peekPos;
                }

                if (allowEmpty && count == 0 && reader.Position + 8 <= objectEnd)
                {
                    int mapCount = reader.ReadInt32();
                    if (mapCount == 0)
                    {
                        int eventCount = reader.ReadInt32();
                        long bytesLeft = objectEnd - pos;
                        if (eventCount == 0 && bytesLeft <= 16)
                            emptyTailPos = pos;
                        else if (eventCount >= 0 && eventCount <= 64 && bytesLeft <= 2048)
                            emptyTailPos = pos;
                    }
                }

                reader.Position = pos + 4;
            }

            if (allowEmpty && emptyTailPos.HasValue)
            {
                reader.Position = emptyTailPos.Value;
                Logger.Warning(
                    $"[AnimClip] CODM binding sync at 0x{emptyTailPos.Value - reader.byteStart:X} " +
                    "(empty bindings at tail — muscle ValueArray may still hold curve maps)");
                return true;
            }

            reader.Position = seekStart;
            return false;
        }

        private static bool IsPlausibleGenericBinding(int typeId, uint attr)
        {
            // Transform TRS channels
            if (typeId == (int)ClassIDType.Transform && attr >= 1 && attr <= 4)
                return true;
            // Common non-Transform curve targets (attribute is usually a CRC hash)
            if (typeId == (int)ClassIDType.Animator ||
                typeId == (int)ClassIDType.SkinnedMeshRenderer ||
                typeId == (int)ClassIDType.GameObject ||
                typeId == (int)ClassIDType.RectTransform ||
                typeId == (int)ClassIDType.MonoBehaviour)
                return attr != 0 || typeId == (int)ClassIDType.Animator;
            return false;
        }

        /// <summary>
        /// After giving up on bindings, slide forward to a plausible AnimationEvent[] header.
        /// </summary>
        private static void TrySeekToAnimationEvents(ObjectReader reader)
        {
            var objectEnd = reader.byteStart + reader.byteSize;
            var start = reader.Position;
            while (reader.Position + 4 <= objectEnd)
            {
                var pos = reader.Position;
                int count = reader.ReadInt32();
                if (count == 0)
                {
                    // empty events at end of object is common
                    if (reader.Position >= objectEnd - 4)
                    {
                        reader.Position = pos;
                        return;
                    }
                }
                if (count >= 1 && count <= 256)
                {
                    // Peek: first event starts with float time (0..600s typical) then aligned string
                    var peek = reader.Position;
                    try
                    {
                        float time = reader.ReadSingle();
                        if (time >= -0.01f && time < 600f)
                        {
                            var name = reader.ReadAlignedString();
                            if (name != null && name.Length < 256)
                            {
                                reader.Position = pos;
                                Logger.Info($"[AnimClip] CODM event sync at 0x{pos - reader.byteStart:X} (count={count})");
                                return;
                            }
                        }
                    }
                    catch
                    {
                        // continue
                    }
                    reader.Position = peek;
                }
                reader.Position = pos + 4;
            }
            reader.Position = start;
        }
    }
}
