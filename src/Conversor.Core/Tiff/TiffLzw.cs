namespace Conversor.Core.Tiff;

/// <summary>
/// A compressão LZW do TIFF (TIFF 6.0, seção 13): códigos de 9 a 12 bits, gravados do bit mais alto para o mais
/// baixo, com a "troca antecipada" de tamanho de código do TIFF. O codificador segue o libtiff passo a passo, para que
/// qualquer leitor (Windows, macOS, Photoshop, GIMP) abra o arquivo.
/// </summary>
internal static class TiffLzw
{
    private const int Clear = 256;
    private const int EndOfInformation = 257;
    private const int FirstCode = 258;
    private const int MinBits = 9;
    private const int MaxBits = 12;

    /// <summary>O maior código de 12 bits; o libtiff limpa a tabela quando o próximo código chegaria a CodeMax - 1.</summary>
    private const int CodeMax = (1 << MaxBits) - 1;

    /// <summary>Tabela de hash (prefixo, byte) → código, com sondagem linear. 8192 posições para no máximo 3836 códigos.</summary>
    private const int HashSize = 8192;

    public static byte[] Encode(ReadOnlySpan<byte> data)
    {
        var writer = new BitWriter(data.Length / 2 + 64);
        var keys = new int[HashSize]; // chave + 1; 0 = posição livre
        var codes = new ushort[HashSize];
        var bits = MinBits;
        var maxCode = (1 << bits) - 1;
        var next = FirstCode;

        writer.Write(Clear, bits);
        if (data.IsEmpty)
        {
            writer.Write(EndOfInformation, bits);
            return writer.ToArray();
        }

        int prefix = data[0];
        for (var i = 1; i < data.Length; i++)
        {
            var value = data[i];
            var key = (prefix << 8) | value;
            var slot = (int)((uint)(key * -1640531535) >> 19); // hash de Fibonacci em 13 bits
            var found = -1;
            while (keys[slot] != 0)
            {
                if (keys[slot] == key + 1)
                {
                    found = codes[slot];
                    break;
                }
                slot = (slot + 1) & (HashSize - 1);
            }
            if (found >= 0)
            {
                prefix = found;
                continue;
            }

            writer.Write(prefix, bits);
            prefix = value;
            keys[slot] = key + 1;
            codes[slot] = (ushort)next++;
            if (next == CodeMax - 1)
            {
                // Tabela cheia: código Clear (ainda com 12 bits) e recomeça com 9.
                writer.Write(Clear, bits);
                Array.Clear(keys);
                next = FirstCode;
                bits = MinBits;
                maxCode = (1 << bits) - 1;
            }
            else if (next > maxCode)
            {
                bits++;
                maxCode = (1 << bits) - 1;
            }
        }

        writer.Write(prefix, bits);
        // O leitor acrescenta uma entrada ao ler o último código: o tamanho do código EOI acompanha isso (libtiff).
        next++;
        if (next == CodeMax - 1)
        {
            writer.Write(Clear, bits);
            bits = MinBits;
        }
        else if (next > maxCode)
        {
            bits++;
        }
        writer.Write(EndOfInformation, bits);
        return writer.ToArray();
    }

    /// <summary>Descomprime em <paramref name="output"/>; para quando ele enche ou os dados acabam.</summary>
    /// <returns>Quantos bytes foram escritos.</returns>
    public static int Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var prefix = new short[1 << MaxBits];
        var suffix = new byte[1 << MaxBits];
        var first = new byte[1 << MaxBits];
        var length = new int[1 << MaxBits];
        for (var i = 0; i < 256; i++)
        {
            prefix[i] = -1;
            suffix[i] = (byte)i;
            first[i] = (byte)i;
            length[i] = 1;
        }

        var reader = new BitReader(input);
        var bits = MinBits;
        var next = FirstCode;
        var old = -1;
        var written = 0;

        while (written < output.Length)
        {
            var code = reader.Read(bits);
            if (code < 0 || code == EndOfInformation)
                break;
            if (code == Clear)
            {
                bits = MinBits;
                next = FirstCode;
                old = -1;
                continue;
            }
            if (old < 0)
            {
                if (code > 255)
                    break; // dados corrompidos: o primeiro código depois de Clear é sempre um byte
                output[written++] = (byte)code;
                old = code;
                continue;
            }

            byte head;
            if (code < next)
            {
                written += WriteString(code, prefix, suffix, length, output[written..]);
                head = first[code];
            }
            else if (code == next)
            {
                // O caso especial do LZW: o código que está sendo definido agora (string anterior + seu 1º byte).
                head = first[old];
                written += WriteString(old, prefix, suffix, length, output[written..]);
                if (written < output.Length)
                    output[written++] = head;
            }
            else
            {
                break; // código que ainda não existe: arquivo corrompido
            }

            if (next < (1 << MaxBits))
            {
                prefix[next] = (short)old;
                suffix[next] = head;
                first[next] = first[old];
                length[next] = length[old] + 1;
                next++;
            }
            old = code;
            if (next >= (1 << bits) - 1 && bits < MaxBits)
                bits++;
        }
        return written;
    }

    /// <summary>Escreve a sequência de bytes do código (de trás para frente, seguindo os prefixos).</summary>
    private static int WriteString(int code, short[] prefix, byte[] suffix, int[] length, Span<byte> output)
    {
        var count = length[code];
        var take = Math.Min(count, output.Length);
        // Pula o fim que não cabe e escreve o resto de trás para frente.
        for (var skip = count - take; skip > 0; skip--)
            code = prefix[code];
        for (var i = take - 1; i >= 0; i--)
        {
            output[i] = suffix[code];
            code = prefix[code];
        }
        return take;
    }

    private sealed class BitWriter(int capacity)
    {
        private byte[] _buffer = new byte[Math.Max(64, capacity)];
        private int _length;
        private ulong _bits;
        private int _count;

        public void Write(int code, int width)
        {
            _bits = (_bits << width) | (uint)code;
            _count += width;
            while (_count >= 8)
            {
                _count -= 8;
                Append((byte)(_bits >> _count));
            }
        }

        public byte[] ToArray()
        {
            if (_count > 0)
            {
                Append((byte)(_bits << (8 - _count)));
                _count = 0;
            }
            return _buffer.AsSpan(0, _length).ToArray();
        }

        private void Append(byte value)
        {
            if (_length == _buffer.Length)
                Array.Resize(ref _buffer, _buffer.Length * 2);
            _buffer[_length++] = value;
        }
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;
        private ulong _bits;
        private int _count;

        /// <summary>Lê um código, ou -1 quando os dados acabam.</summary>
        public int Read(int width)
        {
            while (_count < width)
            {
                if (_position >= _data.Length)
                    return -1;
                _bits = (_bits << 8) | _data[_position++];
                _count += 8;
            }
            _count -= width;
            return (int)((_bits >> _count) & ((1u << width) - 1));
        }
    }
}
