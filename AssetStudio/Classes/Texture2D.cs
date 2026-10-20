using System;

namespace AssetStudio
{
    public class StreamingInfo
    {
        public long offset; //ulong
        public uint size;
        public string path;

        public StreamingInfo(ObjectReader reader)
        {
            var version = reader.version;

            if (version[0] >= 2020) //2020.1 and up
            {
                offset = reader.ReadInt64();
            }
            else
            {
                offset = reader.ReadUInt32();
            }
            size = reader.ReadUInt32();
            path = reader.ReadAlignedString();
        }
    }

    public class GLTextureSettings
    {
        public int m_FilterMode;
        public int m_Aniso;
        public float m_MipBias;
        public int m_WrapMode;

        public GLTextureSettings(ObjectReader reader)
        {
            var version = reader.version;

            m_FilterMode = reader.ReadInt32();
            m_Aniso = reader.ReadInt32();
            m_MipBias = reader.ReadSingle();
            if (version[0] >= 2017)//2017.x and up
            {
                m_WrapMode = reader.ReadInt32(); //m_WrapU
                int m_WrapV = reader.ReadInt32();
                int m_WrapW = reader.ReadInt32();
            }
            else
            {
                m_WrapMode = reader.ReadInt32();
            }
        }
    }

    public sealed class Texture2D : Texture
    {
        public int m_Width;
        public int m_Height;
        public TextureFormat m_TextureFormat;
        public bool m_MipMap;
        public int m_MipCount;
        public GLTextureSettings m_TextureSettings;
        public ResourceReader image_data;
        public StreamingInfo m_StreamData;

        public Texture2D(ObjectReader reader) : base(reader)
        {
            m_Width = reader.ReadInt32();
            m_Height = reader.ReadInt32();
            var m_CompleteImageSize = reader.ReadInt32();
            if (version[0] >= 2020) //2020.1 and up
            {
                var m_MipsStripped = reader.ReadInt32();
            }
            m_TextureFormat = (TextureFormat)reader.ReadInt32();
            if (version[0] < 5 || (version[0] == 5 && version[1] < 2)) //5.2 down
            {
                m_MipMap = reader.ReadBoolean();
            }
            else
            {
                m_MipCount = reader.ReadInt32();
            }
            if (version[0] > 2 || (version[0] == 2 && version[1] >= 6)) //2.6.0 and up
            {
                var m_IsReadable = reader.ReadBoolean();
            }
            if (version[0] >= 2020) //2020.1 and up
            {
                var m_IsPreProcessed = reader.ReadBoolean();
            }
            if (version[0] > 2019 || (version[0] == 2019 && version[1] >= 3)) //2019.3 and up
            {
                var m_IgnoreMasterTextureLimit = reader.ReadBoolean();
            }
            if (version[0] >= 3) //3.0.0 - 5.4
            {
                if (version[0] < 5 || (version[0] == 5 && version[1] <= 4))
                {
                    var m_ReadAllowed = reader.ReadBoolean();
                }
            }
            if (version[0] > 2018 || (version[0] == 2018 && version[1] >= 2)) //2018.2 and up
            {
                var m_StreamingMipmaps = reader.ReadBoolean();
            }
            // Standard Unity aligns after the boolean fields here.
            // CoD Mobile's modified Unity 5.6 engine has extra fields after alignment
            // that don't exist in standard Unity. Detect and skip them.
            var preAlignPos = reader.Position;
            reader.AlignStream();
            if (version[0] > 2018 || (version[0] == 2018 && version[1] >= 2)) //2018.2 and up
            {
                var m_StreamingMipmapsPriority = reader.ReadInt32();
            }

            // Peek ahead to detect CoD Mobile's extra fields.
            // In standard Unity, the next two Int32s are m_ImageCount and m_TextureDimension.
            // m_ImageCount should be 1 (occasionally up to 6 for cubemaps).
            // m_TextureDimension should be 0-6.
            // If they don't look right, CoD Mobile may have extra fields we need to skip.
            var peekPos = reader.Position;
            var peekVal1 = reader.ReadInt32();
            var peekVal2 = reader.ReadInt32();
            reader.Position = peekPos; // rewind

            bool looksValid = (peekVal1 >= 1 && peekVal1 <= 6) && (peekVal2 >= -1 && peekVal2 <= 6);
            if (!looksValid)
            {
                // Try skipping 8 bytes (2 extra Int32 fields in CoD Mobile's modified engine)
                var skipPos = peekPos + 8;
                if (skipPos + 8 <= reader.byteStart + reader.byteSize)
                {
                    reader.Position = skipPos;
                    var skipVal1 = reader.ReadInt32();
                    var skipVal2 = reader.ReadInt32();
                    reader.Position = skipPos; // rewind to start of these fields

                    bool skipLooksValid = (skipVal1 >= 1 && skipVal1 <= 6) && (skipVal2 >= -1 && skipVal2 <= 6);
                    if (skipLooksValid)
                    {
                        CodmDiagnostics.Log($"[Tex2D] Skipping 8 extra bytes for '{m_Name}' (CoD Mobile variant: original peek={peekVal1},{peekVal2}; after skip={skipVal1},{skipVal2})");
                    }
                    else
                    {
                        // Neither works — try without alignment + skip 8
                        reader.Position = preAlignPos + 8;
                        if (reader.Position + 8 <= reader.byteStart + reader.byteSize)
                        {
                            var noAlignSkipVal1 = reader.ReadInt32();
                            var noAlignSkipVal2 = reader.ReadInt32();
                            bool noAlignSkipValid = (noAlignSkipVal1 >= 1 && noAlignSkipVal1 <= 6) && (noAlignSkipVal2 >= -1 && noAlignSkipVal2 <= 6);
                            if (noAlignSkipValid)
                            {
                                reader.Position = preAlignPos + 8;
                                CodmDiagnostics.Log($"[Tex2D] No-align + skip 8 for '{m_Name}' (CoD Mobile variant: vals={noAlignSkipVal1},{noAlignSkipVal2})");
                            }
                            else
                            {
                                // Fall back to standard position
                                reader.Position = peekPos;
                            }
                        }
                        else
                        {
                            reader.Position = peekPos;
                        }
                    }
                }
                else
                {
                    reader.Position = peekPos;
                }
            }
            var m_ImageCount = reader.ReadInt32();
            var m_TextureDimension = reader.ReadInt32();
            m_TextureSettings = new GLTextureSettings(reader);
            if (version[0] >= 3) //3.0 and up
            {
                var m_LightmapFormat = reader.ReadInt32();
            }
            if (version[0] > 3 || (version[0] == 3 && version[1] >= 5)) //3.5.0 and up
            {
                var m_ColorSpace = reader.ReadInt32();
            }
            if (version[0] > 2020 || (version[0] == 2020 && version[1] >= 2)) //2020.2 and up
            {
                var m_PlatformBlob = reader.ReadUInt8Array();
                reader.AlignStream();
            }
            var image_data_size = reader.ReadInt32();
            if (image_data_size == 0 && ((version[0] == 5 && version[1] >= 3) || version[0] > 5))//5.3.0 and up
            {
                m_StreamData = new StreamingInfo(reader);
            }

            // Calculate remaining bytes in this object
            var currentPos = reader.Position;
            var objectEnd = reader.byteStart + reader.byteSize;
            var remaining = objectEnd - currentPos;

            CodmDiagnostics.Log($"[Tex2D] '{m_Name}' {m_Width}x{m_Height} fmt={m_TextureFormat} completeSize={m_CompleteImageSize} inlineSize={image_data_size} streamPath='{m_StreamData?.path}' streamOffset={m_StreamData?.offset} streamSize={m_StreamData?.size} remaining={remaining} objectSize={reader.byteSize} version=[{string.Join(",", version)}]");

            // If data size is 0 and stream path is empty, dump the entire object to diagnose
            if (image_data_size == 0 && string.IsNullOrEmpty(m_StreamData?.path) && m_CompleteImageSize > 0)
            {
                var savedPos = reader.Position;
                reader.Position = reader.byteStart;
                var allBytes = reader.ReadBytes((int)System.Math.Min(reader.byteSize, 256));
                reader.Position = savedPos;
                Logger.Warning($"[Tex2D] MISSING DATA for '{m_Name}': Object bytes (first {allBytes.Length} of {reader.byteSize}): {System.BitConverter.ToString(allBytes).Replace("-", " ")}");
            }

            ResourceReader resourceReader;
            if (!string.IsNullOrEmpty(m_StreamData?.path))
            {
                resourceReader = new ResourceReader(m_StreamData.path, assetsFile, m_StreamData.offset, m_StreamData.size);
            }
            else
            {
                resourceReader = new ResourceReader(reader, reader.BaseStream.Position, image_data_size);
            }
            image_data = resourceReader;
        }
    }

    public enum TextureFormat
    {
        Alpha8 = 1,
        ARGB4444,
        RGB24,
        RGBA32,
        ARGB32,
        ARGBFloat,
        RGB565,
        BGR24,
        R16,
        DXT1,
        DXT3,
        DXT5,
        RGBA4444,
        BGRA32,
        RHalf,
        RGHalf,
        RGBAHalf,
        RFloat,
        RGFloat,
        RGBAFloat,
        YUY2,
        RGB9e5Float,
        RGBFloat,
        BC6H,
        BC7,
        BC4,
        BC5,
        DXT1Crunched,
        DXT5Crunched,
        PVRTC_RGB2,
        PVRTC_RGBA2,
        PVRTC_RGB4,
        PVRTC_RGBA4,
        ETC_RGB4,
        ATC_RGB4,
        ATC_RGBA8,
        EAC_R = 41,
        EAC_R_SIGNED,
        EAC_RG,
        EAC_RG_SIGNED,
        ETC2_RGB,
        ETC2_RGBA1,
        ETC2_RGBA8,
        ASTC_RGB_4x4,
        ASTC_RGB_5x5,
        ASTC_RGB_6x6,
        ASTC_RGB_8x8,
        ASTC_RGB_10x10,
        ASTC_RGB_12x12,
        ASTC_RGBA_4x4,
        ASTC_RGBA_5x5,
        ASTC_RGBA_6x6,
        ASTC_RGBA_8x8,
        ASTC_RGBA_10x10,
        ASTC_RGBA_12x12,
        ETC_RGB4_3DS,
        ETC_RGBA8_3DS,
        RG16,
        R8,
        ETC_RGB4Crunched,
        ETC2_RGBA8Crunched,
        ASTC_HDR_4x4,
        ASTC_HDR_5x5,
        ASTC_HDR_6x6,
        ASTC_HDR_8x8,
        ASTC_HDR_10x10,
        ASTC_HDR_12x12,
        RG32,
        RGB48,
        RGBA64
    }
}