using System.Text;
using Localify.Server.Library;
using Xunit;

namespace Localify.Server.Tests;

public class OggRepairTests
{
    [Fact]
    public void Tags_whose_size_is_a_multiple_of_255_are_unreadable_after_TagLib_and_readable_after_repair()
    {
        var dir = Path.Combine(Path.GetTempPath(), "localify-ogg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "source.opus");
            File.WriteAllBytes(source, MinimalOpus());

            // TagLib écrit le paquet de tags ; on cherche une longueur de commentaire qui le rend multiple de 255.
            string? broken = null;
            for (var pad = 0; pad < 600 && broken is null; pad++)
            {
                var path = Path.Combine(dir, $"t{pad}.opus");
                File.Copy(source, path);
                using (var file = TagLib.File.Create(path))
                {
                    file.Tag.Title = "Titre";
                    file.Tag.Comment = new string('x', pad);
                    file.Save();
                }
                if (OggRepair.Repair(File.ReadAllBytes(path)) is not null) broken = path;
                else File.Delete(path);
            }

            Assert.NotNull(broken);
            Assert.ThrowsAny<Exception>(() => TagLib.File.Create(broken).Dispose());
            Assert.True(OggRepair.Repair(broken));
            using (var repaired = TagLib.File.Create(broken)) Assert.Equal("Titre", repaired.Tag.Title);
            Assert.False(OggRepair.Repair(broken));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Healthy_or_foreign_files_are_left_alone()
    {
        Assert.Null(OggRepair.Repair(MinimalOpus()));
        Assert.Null(OggRepair.Repair(Encoding.ASCII.GetBytes("RIFF0000WAVEfmt ")));
        Assert.Null(OggRepair.Repair([]));
    }

    /// <summary>Flux Ogg Opus mono minimal : en-tête, tags vides, quelques trames de silence.</summary>
    private static byte[] MinimalOpus()
    {
        var head = new List<byte>(Encoding.ASCII.GetBytes("OpusHead")) { 1, 1 };
        head.AddRange(BitConverter.GetBytes((ushort)312));
        head.AddRange(BitConverter.GetBytes(48000u));
        head.AddRange(BitConverter.GetBytes((short)0));
        head.Add(0);
        var vendor = Encoding.ASCII.GetBytes("test");
        var tags = new List<byte>(Encoding.ASCII.GetBytes("OpusTags"));
        tags.AddRange(BitConverter.GetBytes((uint)vendor.Length));
        tags.AddRange(vendor);
        tags.AddRange(BitConverter.GetBytes(0u));
        // Trame CELT de 20 ms silencieuse.
        byte[] silence = [0xF8, 0xFF, 0xFE];

        using var ms = new MemoryStream();
        ms.Write(Page(0x02, 0, 0, [head.ToArray()]));
        ms.Write(Page(0x00, 0, 1, [tags.ToArray()]));
        ms.Write(Page(0x04, 50 * 960, 2, Enumerable.Repeat(silence, 50).ToArray()));
        return ms.ToArray();
    }

    private static byte[] Page(byte flags, long granule, uint sequence, byte[][] packets)
    {
        var lacing = new List<byte>();
        foreach (var p in packets)
        {
            for (var n = p.Length; ; n -= 255)
            {
                lacing.Add((byte)Math.Min(n, 255));
                if (n < 255) break;
            }
        }
        var page = new List<byte>(Encoding.ASCII.GetBytes("OggS")) { 0, flags };
        page.AddRange(BitConverter.GetBytes(granule));
        page.AddRange(BitConverter.GetBytes(0x1234u));
        page.AddRange(BitConverter.GetBytes(sequence));
        page.AddRange(new byte[4]);
        page.Add((byte)lacing.Count);
        page.AddRange(lacing);
        foreach (var p in packets) page.AddRange(p);
        var bytes = page.ToArray();
        uint crc = 0;
        foreach (var b in bytes)
        {
            crc ^= (uint)b << 24;
            for (var i = 0; i < 8; i++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        BitConverter.TryWriteBytes(bytes.AsSpan(22, 4), crc);
        return bytes;
    }
}
