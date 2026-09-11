using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

/// <summary>Reader for Supreme Commander model files, including SupCom 2 SCM v7/v8 material ranges.</summary>
public static class SC2ScmModelReader
{
    private const int Version5HeaderSize = 48; // MODL + 11 uint32 values
    private const int Version7HeaderSize = 56; // MODL + 13 uint32 values
    private const int BoneSize = 96;
    private const int VertexSize = 68;
    private const int MaterialSize = 20;

    public static SC2ScmModel ReadFile(string path)
    {
        using (var stream = File.OpenRead(path)) return Read(stream, path);
    }

    public static SC2ScmModel Read(Stream stream, string sourcePath = null)
    {
        if (stream == null || !stream.CanSeek || !stream.CanRead) throw new ArgumentException("SCM stream must be readable and seekable.", nameof(stream));
        using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
        {
            Require(stream.Length >= Version5HeaderSize, "File is shorter than the SCM v5 header.");
            string magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
            Require(magic == "MODL", "Invalid SCM magic '" + magic + "'.");
            uint version = reader.ReadUInt32();
            Require(version == 5 || version == 7 || version == 8, "Unsupported SCM version " + version + ".");
            int headerSize = version == 5 ? Version5HeaderSize : Version7HeaderSize;
            Require(stream.Length >= headerSize, "File is shorter than the SCM v" + version + " header.");
            var header = new uint[version == 5 ? 10 : 12];
            for (int i = 0; i < header.Length; i++) header[i] = reader.ReadUInt32();
            var model = new SC2ScmModel(sourcePath, version)
            {
                HeaderSize = headerSize,
                BoneOffset = header[0], BoneCount = header[1], VertexOffset = header[2], ExtraVertexOffset = header[3], VertexCount = header[4],
                IndexOffset = header[5], IndexCount = header[6], InfoOffset = header[7], InfoLength = header[8], TotalBoneCount = header[9],
                MaterialOffset = version >= 7 ? header[10] : 0, MaterialCount = version >= 7 ? header[11] : 0
            };
            Validate(model, stream.Length);
            ReadBoneNames(reader, model);
            ReadBones(reader, model);
            ReadVertices(reader, model);
            ReadIndices(reader, model);
            ReadInfo(reader, model);
            ReadMaterials(reader, model);
            return model;
        }
    }

    private static void ReadBoneNames(BinaryReader reader, SC2ScmModel model)
    {
        if (model.BoneOffset == 0) return;
        long start = Align32(model.HeaderSize); long end = model.BoneOffset >= 4 ? model.BoneOffset - 4L : 0;
        if (end <= start) return;
        reader.BaseStream.Position = start;
        foreach (string name in ReadNullSeparated(reader.ReadBytes((int)(end - start)))) model.BoneNames.Add(name);
    }
    private static void ReadBones(BinaryReader reader, SC2ScmModel model)
    {
        if (model.BoneOffset == 0 || model.TotalBoneCount == 0) return;
        reader.BaseStream.Position = model.BoneOffset;
        for (uint i = 0; i < model.TotalBoneCount; i++)
        {
            var bone = new SC2ScmBone { Name = i < model.BoneNames.Count ? model.BoneNames[(int)i] : "bone_" + i };
            bone.InverseBindMatrix = ReadSingles(reader, 16); bone.Position = ReadSingles(reader, 3); bone.Rotation = ReadSingles(reader, 4); bone.ParentIndex = reader.ReadInt32();
            model.Bones.Add(bone);
        }
    }
    private static void ReadVertices(BinaryReader reader, SC2ScmModel model)
    {
        if (model.VertexOffset == 0 || model.VertexCount == 0) return;
        reader.BaseStream.Position = model.VertexOffset;
        for (uint i = 0; i < model.VertexCount; i++)
        {
            var vertex = new SC2ScmVertex { Position = ReadSingles(reader, 3), Tangent = ReadSingles(reader, 3), Normal = ReadSingles(reader, 3), Binormal = ReadSingles(reader, 3), Uv1 = ReadSingles(reader, 2), Uv2 = ReadSingles(reader, 2), BoneIndices = reader.ReadBytes(4) };
            model.Vertices.Add(vertex);
        }
    }
    private static void ReadIndices(BinaryReader reader, SC2ScmModel model)
    {
        if (model.IndexOffset == 0 || model.IndexCount == 0) return;
        reader.BaseStream.Position = model.IndexOffset;
        for (uint i = 0; i < model.IndexCount; i++) model.Indices.Add(reader.ReadUInt16());
    }
    private static void ReadInfo(BinaryReader reader, SC2ScmModel model)
    {
        if (model.InfoOffset == 0 || model.InfoLength == 0) return;
        reader.BaseStream.Position = model.InfoOffset;
        model.InfoStrings.AddRange(ReadNullSeparated(reader.ReadBytes((int)model.InfoLength)));
    }
    private static void ReadMaterials(BinaryReader reader, SC2ScmModel model)
    {
        if (model.Version < 7 || model.MaterialOffset == 0 || model.MaterialCount == 0) return;
        reader.BaseStream.Position = model.MaterialOffset;
        var records = new List<uint[]>();
        for (uint i = 0; i < model.MaterialCount; i++) records.Add(new[] { reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32() });
        foreach (uint[] record in records)
        {
            model.Materials.Add(new SC2ScmMaterialRange { Name = ReadCStringAt(reader, record[0]), FaceCount = record[1], FirstIndex = record[2], VertexStart = record[3], VertexCount = record[4] });
        }
    }
    private static string ReadCStringAt(BinaryReader reader, uint offset)
    {
        long original = reader.BaseStream.Position; reader.BaseStream.Position = offset; var bytes = new List<byte>(); byte b;
        while ((b = reader.ReadByte()) != 0) bytes.Add(b);
        reader.BaseStream.Position = original; return Encoding.ASCII.GetString(bytes.ToArray());
    }
    private static float[] ReadSingles(BinaryReader reader, int count) { var values = new float[count]; for (int i = 0; i < count; i++) values[i] = reader.ReadSingle(); return values; }
    private static IEnumerable<string> ReadNullSeparated(byte[] bytes) { foreach (string part in Encoding.UTF8.GetString(bytes).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries)) yield return part; }
    private static long Align32(long value) => (value + 31L) & ~31L;
    private static void Validate(SC2ScmModel m, long length)
    {
        Require(m.VertexCount <= 10000000 && m.IndexCount <= 30000000 && m.TotalBoneCount <= 100000, "SCM count is unreasonable.");
        CheckRange(m.BoneOffset, (long)m.TotalBoneCount * BoneSize, length, "bone"); CheckRange(m.VertexOffset, (long)m.VertexCount * VertexSize, length, "vertex"); CheckRange(m.IndexOffset, (long)m.IndexCount * 2, length, "index"); CheckRange(m.InfoOffset, m.InfoLength, length, "info"); if (m.Version >= 7) CheckRange(m.MaterialOffset, (long)m.MaterialCount * MaterialSize, length, "material");
    }
    private static void CheckRange(uint offset, long size, long length, string name) { if (offset == 0 && size == 0) return; Require(offset <= length && size <= length - offset, "SCM " + name + " range is outside the file."); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}

public sealed class SC2ScmModel
{
    public string SourcePath { get; private set; } public uint Version { get; private set; } public int HeaderSize { get; internal set; }
    public uint BoneOffset, BoneCount, VertexOffset, ExtraVertexOffset, VertexCount, IndexOffset, IndexCount, InfoOffset, InfoLength, TotalBoneCount, MaterialOffset, MaterialCount;
    public readonly List<string> BoneNames = new List<string>(); public readonly List<SC2ScmBone> Bones = new List<SC2ScmBone>(); public readonly List<SC2ScmVertex> Vertices = new List<SC2ScmVertex>(); public readonly List<ushort> Indices = new List<ushort>(); public readonly List<string> InfoStrings = new List<string>(); public readonly List<SC2ScmMaterialRange> Materials = new List<SC2ScmMaterialRange>();
    public int TriangleCount => Indices.Count / 3;
    internal SC2ScmModel(string sourcePath, uint version) { SourcePath = sourcePath; Version = version; }
}
public sealed class SC2ScmBone { public string Name; public float[] InverseBindMatrix; public float[] Position; public float[] Rotation; public int ParentIndex; }
public sealed class SC2ScmVertex { public float[] Position; public float[] Tangent; public float[] Normal; public float[] Binormal; public float[] Uv1; public float[] Uv2; public byte[] BoneIndices; }
public sealed class SC2ScmMaterialRange { public string Name; public uint FaceCount, FirstIndex, VertexStart, VertexCount; }
