using System.Buffers.Binary;
using System.IO.Compression;

namespace Conversor.Core.Tiff;

/// <summary>Uma página de TIFF já decodificada.</summary>
/// <param name="Pixels">Cores em ARGB (0xAARRGGBB, sem pré-multiplicação), linha a linha, o formato do Android.</param>
/// <param name="HasAlpha">A página tem transparência.</param>
public sealed record TiffPage(int Width, int Height, int[] Pixels, bool HasAlpha);

/// <summary>
/// Lê arquivos TIFF sem bibliotecas nativas (o Android não abre TIFF sozinho). Cobre o que scanners, câmeras e programas
/// de imagem gravam no dia a dia: sem compressão, LZW, Deflate e PackBits; faixas ou blocos (tiles); 1, 2, 4, 8 ou 16
/// bits; tons de cinza, RGB, paleta e CMYK; transparência; várias páginas.
/// Não cobre: JPEG dentro do TIFF, CCITT (fax), amostras em ponto flutuante, planos separados e BigTIFF.
/// </summary>
public static class TiffDecoder
{
    /// <summary>Maior imagem aceita (pixels): acima disso a memória de um celular não dá conta.</summary>
    public const long MaxPixels = 40_000_000;

    private const int MaxPages = 2000;

    /// <summary>Quantas páginas o TIFF tem.</summary>
    public static int PageCount(byte[] file) => ReadDirectories(file).Count;

    /// <summary>Decodifica a página <paramref name="pageIndex"/> (a partir de 0).</summary>
    /// <exception cref="NotSupportedException">Variante de TIFF que o app não lê (a mensagem explica qual).</exception>
    /// <exception cref="InvalidDataException">Arquivo corrompido.</exception>
    public static TiffPage Decode(byte[] file, int pageIndex = 0)
    {
        var directories = ReadDirectories(file);
        if (pageIndex < 0 || pageIndex >= directories.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        return new PageReader(file, directories[pageIndex]).Read();
    }

    // ───────────────────────── Diretórios (IFDs) ─────────────────────────

    private sealed class Ifd(bool bigEndian)
    {
        public bool BigEndian { get; } = bigEndian;
        public Dictionary<ushort, long[]> Values { get; } = [];

        public long[]? Get(ushort tag) => Values.TryGetValue(tag, out var v) ? v : null;

        public long One(ushort tag, long fallback) => Values.TryGetValue(tag, out var v) && v.Length > 0 ? v[0] : fallback;
    }

    private static List<Ifd> ReadDirectories(byte[] file)
    {
        if (file.Length < 8)
            throw new InvalidDataException("Arquivo TIFF incompleto.");
        var bigEndian = file[0] == 'M' && file[1] == 'M';
        if (!bigEndian && !(file[0] == 'I' && file[1] == 'I'))
            throw new InvalidDataException("Não é um arquivo TIFF.");
        var magic = U16(file, 2, bigEndian);
        if (magic == 43)
            throw new NotSupportedException("TIFF no formato BigTIFF ainda não é suportado.");
        if (magic != 42)
            throw new InvalidDataException("Não é um arquivo TIFF.");

        var list = new List<Ifd>();
        var seen = new HashSet<long>();
        long offset = U32(file, 4, bigEndian);
        while (offset != 0 && list.Count < MaxPages && seen.Add(offset))
        {
            if (offset + 2 > file.Length)
                break;
            var count = U16(file, (int)offset, bigEndian);
            var entries = (int)offset + 2;
            if (entries + count * 12L + 4 > file.Length)
                break;
            var directory = new Ifd(bigEndian);
            for (var i = 0; i < count; i++)
            {
                var at = entries + i * 12;
                var tag = U16(file, at, bigEndian);
                var type = U16(file, at + 2, bigEndian);
                var n = U32(file, at + 4, bigEndian);
                if (ReadValues(file, at + 8, type, n, bigEndian) is { } values)
                    directory.Values[tag] = values;
            }
            list.Add(directory);
            offset = U32(file, entries + count * 12, bigEndian);
        }
        if (list.Count == 0)
            throw new InvalidDataException("O TIFF não tem nenhuma página.");
        return list;
    }

    /// <summary>Valores inteiros de uma entrada (BYTE, SHORT, LONG e os com sinal); null para os outros tipos.</summary>
    private static long[]? ReadValues(byte[] file, int valueField, ushort type, uint count, bool bigEndian)
    {
        var size = type switch
        {
            1 or 6 or 7 => 1,
            3 or 8 => 2,
            4 or 9 or 13 => 4,
            _ => 0,
        };
        if (size == 0 || count == 0 || count > 1 << 24)
            return null;
        long total = size * count;
        long start = total <= 4 ? valueField : U32(file, valueField, bigEndian);
        if (start < 0 || start + total > file.Length)
            return null;
        var values = new long[count];
        for (var i = 0; i < count; i++)
        {
            var at = (int)(start + i * size);
            values[i] = type switch
            {
                1 or 7 => file[at],
                6 => (sbyte)file[at],
                3 => U16(file, at, bigEndian),
                8 => (short)U16(file, at, bigEndian),
                9 => (int)U32(file, at, bigEndian),
                _ => U32(file, at, bigEndian),
            };
        }
        return values;
    }

    private static ushort U16(byte[] data, int at, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at, 2));

    private static uint U32(byte[] data, int at, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at, 4));

    // ───────────────────────── Uma página ─────────────────────────

    private sealed class PageReader
    {
        private readonly byte[] _file;
        private readonly Ifd _ifd;
        private readonly int _width, _height, _bits, _samples, _photometric, _colorChannels, _alphaSample;
        private readonly bool _associatedAlpha;
        private readonly long _compression, _predictor;
        private readonly long[]? _colorMap;

        public PageReader(byte[] file, Ifd ifd)
        {
            _file = file;
            _ifd = ifd;
            _width = (int)ifd.One(256, 0);
            _height = (int)ifd.One(257, 0);
            if (_width <= 0 || _height <= 0)
                throw new InvalidDataException("O TIFF não informa o tamanho da imagem.");
            if ((long)_width * _height > MaxPixels)
                throw new NotSupportedException($"Imagem grande demais para o celular ({_width} × {_height} pixels).");

            _samples = (int)ifd.One(277, 1);
            var bits = ifd.Get(258) ?? [1];
            _bits = (int)bits[0];
            if (bits.Any(b => b != _bits) || _bits is not (1 or 2 or 4 or 8 or 16))
                throw new NotSupportedException($"TIFF com {string.Join("/", bits)} bits por amostra ainda não é suportado.");
            if (ifd.One(339, 1) is not 1 and not 2) // SampleFormat: só inteiros
                throw new NotSupportedException("TIFF com amostras em ponto flutuante ainda não é suportado.");
            if (ifd.One(284, 1) == 2 && _samples > 1)
                throw new NotSupportedException("TIFF com planos de cor separados ainda não é suportado.");

            _compression = ifd.One(259, 1);
            if (_compression is not (1 or 5 or 8 or 32946 or 32773))
                throw new NotSupportedException(_compression switch
                {
                    2 or 3 or 4 => "TIFF com compressão de fax (CCITT) ainda não é suportado.",
                    6 or 7 => "TIFF com compressão JPEG ainda não é suportado.",
                    _ => $"TIFF com compressão tipo {_compression} ainda não é suportado.",
                });
            _predictor = ifd.One(317, 1);
            if (_predictor is not (1 or 2))
                throw new NotSupportedException("TIFF com preditor de ponto flutuante ainda não é suportado.");

            _photometric = (int)ifd.One(262, _samples >= 3 ? 2 : 1);
            _colorChannels = _photometric switch
            {
                0 or 1 or 3 => 1,
                2 => 3,
                5 => 4,
                _ => throw new NotSupportedException(_photometric == 6
                    ? "TIFF em YCbCr ainda não é suportado."
                    : $"TIFF com espaço de cor tipo {_photometric} ainda não é suportado."),
            };
            if (_samples < _colorChannels)
                throw new InvalidDataException("O TIFF tem menos canais que o espaço de cor pede.");
            if (_photometric == 3)
            {
                _colorMap = ifd.Get(320);
                if (_colorMap is null || _colorMap.Length < 3 << _bits)
                    throw new InvalidDataException("O TIFF de paleta não tem a tabela de cores.");
            }

            // Canal de transparência: a primeira amostra extra marcada como alfa (1 = pré-multiplicado, 2 = não).
            // Sem a marcação (ExtraSamples ausente) e com exatamente uma amostra a mais, também vale como alfa.
            _alphaSample = -1;
            if (_samples > _colorChannels)
            {
                var extra = ifd.Get(338);
                var kind = extra is { Length: > 0 } ? extra[0] : (_samples == _colorChannels + 1 ? 2 : 0);
                if (kind is 1 or 2)
                {
                    _alphaSample = _colorChannels;
                    _associatedAlpha = kind == 1;
                }
            }
        }

        public TiffPage Read()
        {
            var pixels = new int[(long)_width * _height];
            if (_ifd.Get(322) is { } tileWidth && _ifd.Get(323) is { } tileHeight)
                ReadTiles((int)tileWidth[0], (int)tileHeight[0], pixels);
            else
                ReadStrips(pixels);
            return new TiffPage(_width, _height, pixels, _alphaSample >= 0);
        }

        private int RowBytes(int width) => (int)(((long)width * _samples * _bits + 7) / 8);

        private void ReadStrips(int[] pixels)
        {
            var offsets = _ifd.Get(273) ?? throw new InvalidDataException("O TIFF não diz onde estão os dados.");
            var counts = _ifd.Get(279);
            var rowsPerStrip = (int)Math.Clamp(_ifd.One(278, _height), 1, _height);
            var rowBytes = RowBytes(_width);
            var buffer = new byte[(long)rowsPerStrip * rowBytes];
            for (var s = 0; s < offsets.Length; s++)
            {
                var firstRow = s * rowsPerStrip;
                if (firstRow >= _height)
                    break;
                var rows = Math.Min(rowsPerStrip, _height - firstRow);
                var length = rows * rowBytes;
                Decompress(offsets[s], counts is not null && s < counts.Length ? counts[s] : -1, buffer.AsSpan(0, length));
                for (var r = 0; r < rows; r++)
                {
                    var row = buffer.AsSpan(r * rowBytes, rowBytes);
                    Unpredict(row, _width);
                    ConvertRow(row, _width, pixels, (long)(firstRow + r) * _width);
                }
            }
        }

        private void ReadTiles(int tileWidth, int tileHeight, int[] pixels)
        {
            if (tileWidth <= 0 || tileHeight <= 0)
                throw new InvalidDataException("Tamanho de bloco inválido no TIFF.");
            var offsets = _ifd.Get(324) ?? _ifd.Get(273) ?? throw new InvalidDataException("O TIFF não diz onde estão os dados.");
            var counts = _ifd.Get(325) ?? _ifd.Get(279);
            var across = (_width + tileWidth - 1) / tileWidth;
            var down = (_height + tileHeight - 1) / tileHeight;
            var rowBytes = RowBytes(tileWidth);
            var buffer = new byte[(long)tileHeight * rowBytes];
            var line = new int[tileWidth];
            for (var ty = 0; ty < down; ty++)
            {
                for (var tx = 0; tx < across; tx++)
                {
                    var index = ty * across + tx;
                    if (index >= offsets.Length)
                        return;
                    Decompress(offsets[index], counts is not null && index < counts.Length ? counts[index] : -1, buffer);
                    var visibleWidth = Math.Min(tileWidth, _width - tx * tileWidth);
                    var visibleRows = Math.Min(tileHeight, _height - ty * tileHeight);
                    for (var r = 0; r < visibleRows; r++)
                    {
                        var row = buffer.AsSpan(r * rowBytes, rowBytes);
                        Unpredict(row, tileWidth);
                        ConvertRow(row, tileWidth, line, 0);
                        var y = ty * tileHeight + r;
                        Array.Copy(line, 0, pixels, (long)y * _width + tx * tileWidth, visibleWidth);
                    }
                }
            }
        }

        /// <summary>Descomprime um bloco de dados em <paramref name="target"/>; o que faltar fica zerado.</summary>
        private void Decompress(long offset, long count, Span<byte> target)
        {
            target.Clear();
            if (offset <= 0 || offset >= _file.Length)
                return;
            var available = _file.Length - offset;
            var length = (int)(count < 0 || count > available ? available : count);
            var source = _file.AsSpan((int)offset, length);
            switch (_compression)
            {
                case 1:
                    source[..Math.Min(source.Length, target.Length)].CopyTo(target);
                    break;
                case 5:
                    TiffLzw.Decode(source, target);
                    break;
                case 8 or 32946:
                    Inflate(source, target);
                    break;
                case 32773:
                    UnpackBits(source, target);
                    break;
            }
        }

        private static void Inflate(ReadOnlySpan<byte> source, Span<byte> target)
        {
            using var input = new MemoryStream(source.ToArray());
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            var done = 0;
            try
            {
                while (done < target.Length)
                {
                    var read = zlib.Read(target[done..]);
                    if (read == 0)
                        break;
                    done += read;
                }
            }
            catch (InvalidDataException)
            {
                // Fim truncado: fica o que já saiu, como os outros leitores fazem.
            }
        }

        private static void UnpackBits(ReadOnlySpan<byte> source, Span<byte> target)
        {
            var i = 0;
            var o = 0;
            while (i < source.Length && o < target.Length)
            {
                var n = (sbyte)source[i++];
                if (n >= 0)
                {
                    var count = Math.Min(n + 1, Math.Min(source.Length - i, target.Length - o));
                    source.Slice(i, count).CopyTo(target[o..]);
                    i += n + 1;
                    o += count;
                }
                else if (n != -128 && i < source.Length)
                {
                    var count = Math.Min(1 - n, target.Length - o);
                    target.Slice(o, count).Fill(source[i++]);
                    o += count;
                }
            }
        }

        /// <summary>Desfaz o preditor horizontal: cada amostra estava gravada como diferença para a da esquerda.</summary>
        private void Unpredict(Span<byte> row, int width)
        {
            if (_predictor != 2)
                return;
            if (_bits == 8)
            {
                for (var i = _samples; i < width * _samples; i++)
                    row[i] += row[i - _samples];
            }
            else if (_bits == 16)
            {
                for (var i = _samples; i < width * _samples; i++)
                {
                    var value = (ushort)(Read16(row, i) + Read16(row, i - _samples));
                    if (_ifd.BigEndian)
                        BinaryPrimitives.WriteUInt16BigEndian(row.Slice(i * 2, 2), value);
                    else
                        BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(i * 2, 2), value);
                }
            }
        }

        private ushort Read16(ReadOnlySpan<byte> row, int sample) => _ifd.BigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(row.Slice(sample * 2, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(sample * 2, 2));

        /// <summary>Valor bruto de uma amostra (sem escala).</summary>
        private int Raw(ReadOnlySpan<byte> row, int sample) => _bits switch
        {
            8 => row[sample],
            16 => Read16(row, sample),
            _ => (row[sample * _bits >> 3] >> (8 - _bits - (sample * _bits & 7))) & ((1 << _bits) - 1),
        };

        /// <summary>Amostra levada a 0–255.</summary>
        private int Scaled(ReadOnlySpan<byte> row, int sample) => _bits switch
        {
            8 => row[sample],
            16 => Read16(row, sample) >> 8,
            _ => Raw(row, sample) * 255 / ((1 << _bits) - 1),
        };

        private void ConvertRow(ReadOnlySpan<byte> row, int width, int[] pixels, long start)
        {
            for (var x = 0; x < width; x++)
            {
                var s = x * _samples;
                int r, g, b;
                switch (_photometric)
                {
                    case 0:
                        r = g = b = 255 - Scaled(row, s);
                        break;
                    case 1:
                        r = g = b = Scaled(row, s);
                        break;
                    case 3:
                        var index = Raw(row, s);
                        var entries = 1 << _bits;
                        r = (int)(_colorMap![index] >> 8);
                        g = (int)(_colorMap[entries + index] >> 8);
                        b = (int)(_colorMap[2 * entries + index] >> 8);
                        break;
                    case 5:
                        var k = Scaled(row, s + 3);
                        r = (255 - Scaled(row, s)) * (255 - k) / 255;
                        g = (255 - Scaled(row, s + 1)) * (255 - k) / 255;
                        b = (255 - Scaled(row, s + 2)) * (255 - k) / 255;
                        break;
                    default:
                        r = Scaled(row, s);
                        g = Scaled(row, s + 1);
                        b = Scaled(row, s + 2);
                        break;
                }

                var a = 255;
                if (_alphaSample >= 0)
                {
                    a = Scaled(row, s + _alphaSample);
                    if (_associatedAlpha && a is > 0 and < 255)
                    {
                        r = Math.Min(255, r * 255 / a);
                        g = Math.Min(255, g * 255 / a);
                        b = Math.Min(255, b * 255 / a);
                    }
                }
                pixels[start + x] = a << 24 | r << 16 | g << 8 | b;
            }
        }
    }
}
