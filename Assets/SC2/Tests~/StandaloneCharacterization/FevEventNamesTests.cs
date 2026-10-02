using System.IO;
using System.Text;

public sealed class FevEventNamesTests
{
    private sealed class FevBuilder
    {
        private readonly MemoryStream _stream = new();
        private readonly BinaryWriter _writer;

        public FevBuilder() { _writer = new BinaryWriter(_stream, Encoding.ASCII); }

        public void U32(uint value) => _writer.Write(value);
        public void Bytes(int count, byte value = 0) { for (var i = 0; i < count; i++) _writer.Write(value); }

        public void Str(string text)
        {
            U32((uint)text.Length + 1);
            _writer.Write(Encoding.ASCII.GetBytes(text));
            _writer.Write((byte)0);
        }

        public void Category(string name, params string[] childNames)
        {
            Str(name);
            Bytes(16);
            U32((uint)childNames.Length);
            foreach (var child in childNames)
            {
                Str(child);
                Bytes(16);
                U32(0);
            }
        }

        public void Event(string name, string category)
        {
            U32(8); // value in front of the name
            Str(name);
            Bytes(16, 0x5A);       // GUID
            U32(1); Bytes(12, 0);  // looks like the end marker but is not followed by a category path
            U32(1); U32(7); _writer.Write(Encoding.ASCII.GetBytes("notacat")); // ditto
            Bytes(40, 0x11);
            U32(1);
            Str(category);
        }

        public byte[] ToArray() => _stream.ToArray();
    }

    private static byte[] Sample()
    {
        var b = new FevBuilder();
        // magic, version and two values
        foreach (var c in "FEV1") b.Bytes(1, (byte)c);
        b.U32(0x3d0000); b.U32(1); b.U32(2);
        b.Str("PRJ");
        b.U32(1);                 // one wave bank
        b.U32(0x80); b.U32(7); b.Bytes(8); b.Str("BankA");
        b.Category("master", "SoundFX");
        b.U32(1);                 // one top-level group
        b.Str("Top"); b.U32(0);   // name, no properties
        b.U32(1);                 // one sub group
        b.U32(1);                 // one event directly in Top
        b.Str("Sub"); b.U32(0);
        b.U32(0);                 // no sub groups
        b.U32(2);                 // two events
        b.Event("first", "SoundFX");
        b.Event("second", "master/SoundFX");
        b.Event("top_event", "SoundFX");
        return b.ToArray();
    }

    [Fact]
    public void Read_ReturnsFullPathsInTreeOrder()
    {
        var names = SC2FevEventNames.Read(Sample());
        Assert.Equal(new[] { "PRJ/Top/Sub/first", "PRJ/Top/Sub/second", "PRJ/Top/top_event" }, names);
    }

    [Fact]
    public void Read_RejectsAFileThatIsNotFev1()
    {
        Assert.Throws<InvalidDataException>(() => SC2FevEventNames.Read(new byte[64]));
    }

    [Fact]
    public void Read_ReportsAnEventWhoseEndCannotBeFound()
    {
        var data = Sample();
        // Cut the last event short so its end marker never appears.
        var cut = new byte[data.Length - 50];
        System.Array.Copy(data, cut, cut.Length);
        Assert.Throws<InvalidDataException>(() => SC2FevEventNames.Read(cut));
    }

    // Optional check against a real file: set SC2_FEV_PATH to the game's SC2.fev. Skipped (passes) when unset.
    [Fact]
    public void Read_RealSc2Fev_HasEveryEventTheFmodPlayerReports()
    {
        var path = System.Environment.GetEnvironmentVariable("SC2_FEV_PATH");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var names = SC2FevEventNames.Read(File.ReadAllBytes(path));
        Assert.Equal(2698, names.Count);
        Assert.Contains("SC2/SC2/Environments/SC2_MP_001/snd_SC2_MP_001_Generic_Ocean", names);
    }
}
