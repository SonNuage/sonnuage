namespace SonNuage.Server.Library;

/// <summary>
/// Répare un défaut d'écriture de TagLib# dans les fichiers Ogg (Opus, Vorbis) : quand le paquet de tags fait
/// exactement un multiple de 255 octets, la page qui le termine n'a pas la valeur de laçage 0 qui le clôt.
/// Le paquet paraît alors se poursuivre dans la page suivante (qui n'est pas une suite) : le son reste lisible,
/// mais les tags ne le sont plus, ni pour TagLib ni pour d'autres lecteurs. Environ un fichier sur 255 est touché.
/// La réparation ajoute la valeur manquante et recalcule la somme de contrôle de la page.
/// </summary>
public static class OggRepair
{
    private const int HeaderSize = 27;
    private const byte Continued = 0x01;
    private static readonly uint[] Crc = BuildCrcTable();

    public static bool IsOgg(string path) => Path.GetExtension(path).ToLowerInvariant() is ".opus" or ".ogg" or ".oga";

    /// <summary>Vrai si le fichier a été corrigé (il est réécrit en place).</summary>
    public static bool Repair(string path)
    {
        var data = File.ReadAllBytes(path);
        var fixedData = Repair(data);
        if (fixedData is null) return false;
        var temp = path + ".repair";
        File.WriteAllBytes(temp, fixedData);
        File.Move(temp, path, overwrite: true);
        return true;
    }

    /// <summary>Contenu corrigé, ou null s'il n'y a rien à corriger (ou si ce n'est pas un flux Ogg lisible).</summary>
    public static byte[]? Repair(byte[] data)
    {
        var pages = new List<(int Offset, int Length)>();
        for (var i = 0; i + HeaderSize <= data.Length;)
        {
            if (data[i] != 'O' || data[i + 1] != 'g' || data[i + 2] != 'g' || data[i + 3] != 'S') return null;
            var segments = data[i + 26];
            if (i + HeaderSize + segments > data.Length) return null;
            var body = 0;
            for (var s = 0; s < segments; s++) body += data[i + HeaderSize + s];
            var length = HeaderSize + segments + body;
            if (i + length > data.Length) return null;
            pages.Add((i, length));
            i += length;
        }

        // Pages dont le dernier paquet reste ouvert alors que la page suivante du même flux ne le continue pas.
        var broken = new HashSet<int>();
        for (var p = 0; p + 1 < pages.Count; p++)
        {
            var (offset, _) = pages[p];
            var segments = data[offset + 26];
            if (segments is 0 or 255 || data[offset + HeaderSize + segments - 1] != 255) continue;
            var next = pages[p + 1].Offset;
            if (Serial(data, next) == Serial(data, offset) && (data[next + 5] & Continued) == 0) broken.Add(p);
        }
        if (broken.Count == 0) return null;

        using var output = new MemoryStream(data.Length + broken.Count);
        for (var p = 0; p < pages.Count; p++)
        {
            var (offset, length) = pages[p];
            if (!broken.Contains(p))
            {
                output.Write(data, offset, length);
                continue;
            }
            var segments = data[offset + 26];
            var page = new byte[length + 1];
            Array.Copy(data, offset, page, 0, HeaderSize + segments);
            page[26] = (byte)(segments + 1);
            page[HeaderSize + segments] = 0;
            Array.Copy(data, offset + HeaderSize + segments, page, HeaderSize + segments + 1, length - HeaderSize - segments);
            WriteCrc(page);
            output.Write(page);
        }
        return output.ToArray();
    }

    private static uint Serial(byte[] data, int offset) => BitConverter.ToUInt32(data, offset + 14);

    private static void WriteCrc(byte[] page)
    {
        Array.Clear(page, 22, 4);
        uint crc = 0;
        foreach (var b in page) crc = (crc << 8) ^ Crc[((crc >> 24) & 0xFF) ^ b];
        BitConverter.TryWriteBytes(page.AsSpan(22, 4), crc);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var r = i << 24;
            for (var j = 0; j < 8; j++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            table[i] = r;
        }
        return table;
    }
}
