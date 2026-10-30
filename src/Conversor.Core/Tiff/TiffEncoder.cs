using System.Buffers.Binary;

namespace Conversor.Core.Tiff;

/// <summary>
/// Grava um TIFF de base (TIFF 6.0): RGB ou RGBA com 8 bits por canal, em faixas (strips), comprimido com LZW e o
/// preditor horizontal, o mesmo que Photoshop e o libtiff usam por padrão para "TIFF LZW". Abre no Windows, macOS,
/// Photoshop, GIMP e em qualquer serviço de impressão.
/// </summary>
public static class TiffEncoder
{
    /// <summary>Bytes não comprimidos por faixa, aproximadamente.</summary>
    private const int StripBytes = 128 * 1024;

    private const ushort TypeAscii = 2, TypeShort = 3, TypeLong = 4, TypeRational = 5;

    /// <summary>Grava a imagem em <paramref name="output"/>.</summary>
    /// <param name="readRow">
    /// Preenche o array com a linha <c>y</c> da imagem em ARGB (o formato de cor do Android: 0xAARRGGBB, sem
    /// pré-multiplicação). O array tem <paramref name="width"/> posições.
    /// </param>
    /// <param name="alpha">Grava o canal de transparência (RGBA); senão, só RGB.</param>
    /// <param name="dpi">Resolução gravada no arquivo (só informativa).</param>
    public static void Write(Stream output, int width, int height, bool alpha, Action<int, int[]> readRow, int dpi = 72,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (!output.CanSeek)
        {
            using var buffer = new MemoryStream();
            Write(buffer, width, height, alpha, readRow, dpi, cancellationToken);
            buffer.Position = 0;
            buffer.CopyTo(output);
            return;
        }

        var samples = alpha ? 4 : 3;
        var rowBytes = (long)width * samples;
        var rowsPerStrip = (int)Math.Clamp(StripBytes / rowBytes, 1, height);
        var stripCount = (height + rowsPerStrip - 1) / rowsPerStrip;
        var offsets = new uint[stripCount];
        var counts = new uint[stripCount];

        var start = output.Position;
        var file = new TiffWriterStream(output, start);
        file.Bytes("II"u8);
        file.UInt16(42);
        file.UInt32(0); // posição do IFD, preenchida no fim

        var argb = new int[width];
        var strip = new byte[rowsPerStrip * rowBytes];
        for (var s = 0; s < stripCount; s++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var firstRow = s * rowsPerStrip;
            var rows = Math.Min(rowsPerStrip, height - firstRow);
            for (var r = 0; r < rows; r++)
            {
                readRow(firstRow + r, argb);
                var row = strip.AsSpan((int)(r * rowBytes), (int)rowBytes);
                for (var x = 0; x < width; x++)
                {
                    var color = argb[x];
                    var i = x * samples;
                    row[i] = (byte)(color >> 16);
                    row[i + 1] = (byte)(color >> 8);
                    row[i + 2] = (byte)color;
                    if (alpha)
                        row[i + 3] = (byte)(color >>> 24);
                }
                // Preditor horizontal: cada amostra vira a diferença para a do pixel à esquerda (comprime bem melhor).
                for (var i = row.Length - 1; i >= samples; i--)
                    row[i] -= row[i - samples];
            }

            var compressed = TiffLzw.Encode(strip.AsSpan(0, (int)(rows * rowBytes)));
            file.Align();
            offsets[s] = file.Offset;
            counts[s] = (uint)compressed.Length;
            file.Bytes(compressed);
        }

        // Valores que não cabem nos 4 bytes da entrada do IFD vão antes dele.
        file.Align();
        var bitsOffset = file.Offset;
        for (var i = 0; i < samples; i++)
            file.UInt16(8);
        var resolutionOffset = file.Offset;
        file.UInt32((uint)Math.Max(1, dpi));
        file.UInt32(1);
        var software = "Conversor (Android)\0"u8;
        var softwareOffset = file.Offset;
        file.Bytes(software);
        file.Align();
        uint stripOffsetsAt = offsets[0], stripCountsAt = counts[0];
        if (stripCount > 1)
        {
            stripOffsetsAt = file.Offset;
            foreach (var o in offsets)
                file.UInt32(o);
            stripCountsAt = file.Offset;
            foreach (var c in counts)
                file.UInt32(c);
        }

        file.Align();
        var ifdOffset = file.Offset;
        var entries = new List<(ushort Tag, ushort Type, uint Count, uint Value)>
        {
            (256, TypeLong, 1, (uint)width),           // ImageWidth
            (257, TypeLong, 1, (uint)height),          // ImageLength
            (258, TypeShort, (uint)samples, bitsOffset), // BitsPerSample: 8,8,8(,8)
            (259, TypeShort, 1, 5),                    // Compression: LZW
            (262, TypeShort, 1, 2),                    // PhotometricInterpretation: RGB
            (273, TypeLong, (uint)stripCount, stripOffsetsAt), // StripOffsets
            (277, TypeShort, 1, (uint)samples),        // SamplesPerPixel
            (278, TypeLong, 1, (uint)rowsPerStrip),    // RowsPerStrip
            (279, TypeLong, (uint)stripCount, stripCountsAt), // StripByteCounts
            (282, TypeRational, 1, resolutionOffset),  // XResolution
            (283, TypeRational, 1, resolutionOffset),  // YResolution
            (284, TypeShort, 1, 1),                    // PlanarConfiguration: intercalado
            (296, TypeShort, 1, 2),                    // ResolutionUnit: polegada
            (305, TypeAscii, (uint)software.Length, softwareOffset), // Software
            (317, TypeShort, 1, 2),                    // Predictor: horizontal
        };
        if (alpha)
            entries.Add((338, TypeShort, 1, 2));       // ExtraSamples: transparência não pré-multiplicada

        file.UInt16((ushort)entries.Count);
        foreach (var (tag, type, count, value) in entries)
        {
            file.UInt16(tag);
            file.UInt16(type);
            file.UInt32(count);
            file.UInt32(value); // SHORT de um valor só: os 2 primeiros bytes (little-endian), como manda a norma
        }
        file.UInt32(0); // não há próxima página

        var end = output.Position;
        output.Position = start + 4;
        Span<byte> pointer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(pointer, ifdOffset);
        output.Write(pointer);
        output.Position = end;
    }

    /// <summary>Escreve valores little-endian e sabe a posição relativa ao começo do TIFF.</summary>
    private sealed class TiffWriterStream(Stream stream, long start)
    {
        private readonly byte[] _scratch = new byte[4];

        public uint Offset => checked((uint)(stream.Position - start));

        public void Bytes(ReadOnlySpan<byte> data) => stream.Write(data);

        public void UInt16(ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_scratch, value);
            stream.Write(_scratch, 0, 2);
        }

        public void UInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_scratch, value);
            stream.Write(_scratch, 0, 4);
        }

        /// <summary>Os dados do TIFF começam em posições pares.</summary>
        public void Align()
        {
            if ((Offset & 1) != 0)
                stream.WriteByte(0);
        }
    }
}
