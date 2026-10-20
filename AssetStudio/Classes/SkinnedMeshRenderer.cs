using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AssetStudio
{
    public sealed class SkinnedMeshRenderer : Renderer
    {
        public PPtr<Mesh> m_Mesh;
        public PPtr<Transform>[] m_Bones;
        public float[] m_BlendShapeWeights;

        public SkinnedMeshRenderer(ObjectReader reader) : base(reader)
        {
            Logger.Info($"[SMR] After Renderer base: position=0x{reader.Position - reader.byteStart:X}, remaining={reader.byteStart + reader.byteSize - reader.Position}");
            if (version[0] < 5 || (version[0] == 5 && version[1] < 4)) //5.4 down
            {
                int m_Quality = reader.ReadInt32();
            }
            var m_UpdateWhenOffscreen = reader.ReadBoolean();
            Logger.Info($"[SMR] m_UpdateWhenOffscreen={m_UpdateWhenOffscreen}, position=0x{reader.Position - reader.byteStart:X}");
            if (version[0] < 3 || (version[0] == 3 && version[1] <= 1)) //3.1.0 and below
            {
                var m_SkinNormals = reader.ReadBoolean();
            }
            reader.AlignStream();

            if (version[0] < 2 || (version[0] == 2 && version[1] < 6)) //2.6 down
            {
                var m_DisableAnimationWhenOffscreen = new PPtr<Animation>(reader);
            }

            Logger.Info($"[SMR] Before m_Mesh: position=0x{reader.Position - reader.byteStart:X}");
            m_Mesh = new PPtr<Mesh>(reader);
            Logger.Info($"[SMR] m_Mesh: FileID={m_Mesh.m_FileID}, PathID={m_Mesh.m_PathID}, position=0x{reader.Position - reader.byteStart:X}");

            m_Bones = new PPtr<Transform>[reader.ReadInt32()];
            Logger.Info($"[SMR] m_Bones.Length={m_Bones.Length}, position=0x{reader.Position - reader.byteStart:X}");
            for (int b = 0; b < m_Bones.Length; b++)
            {
                m_Bones[b] = new PPtr<Transform>(reader);
            }

            if (version[0] > 4 || (version[0] == 4 && version[1] >= 3)) //4.3 and up
            {
                m_BlendShapeWeights = reader.ReadSingleArray();
            }
            Logger.Info($"[SMR] End: position=0x{reader.Position - reader.byteStart:X}, objectSize={reader.byteSize}");
        }
    }
}
