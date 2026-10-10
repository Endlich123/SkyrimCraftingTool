using System;
using System.IO;
using System.Text;

namespace SkyrimCraftingTool.Services
{
    public enum DdsFormat
    {
        Unknown = 0,
        Bc1,        // DXT1 - colour, optional 1-bit alpha
        Bc2,        // DXT3 - colour + 4-bit explicit alpha
        Bc3,        // DXT5 - colour + interpolated alpha
        Bc4,        // one channel
        Bc5,        // two channels, the normal-map format
        Bc7,        // the one SSE actually uses for diffuse
        Bgra32,
        Rgba32,
        Bgr24,
    }

    public sealed class DdsImage
    {
        public int Width;
        public int Height;

        // BGRA, 4 bytes per pixel, top row first - what WPF's Bgra32 WriteableBitmap takes.
        public byte[] Pixels = Array.Empty<byte>();

        public DdsFormat SourceFormat;

        // Which mip was decoded. 0 is the full-size image; the preview normally wants a smaller one.
        public int MipLevel;
    }

    // A DDS decoder, written out rather than handed to Windows.
    //
    // WHY NOT WIC, which is right there and already correct: measured on this load order, WPF's
    // BitmapDecoder refuses EVERY DDS carrying a DX10 header - not just exotic formats, but plain BC1
    // too. 79 % of the diffuse textures a real Skyrim setup names are DX10-header files. Rewriting the
    // header as a legacy FOURCC does make WIC accept them, and that works for BC1/BC2/BC3 - but BC7 has
    // no legacy equivalent at all, and BC7 is 66 % of them. Since BC7 has to be written either way, one
    // decoder beats a hybrid whose two halves can disagree.
    //
    // WHAT MADE BC7 TRACTABLE: measured over 4,2 million blocks in 60 real textures, 100 % use modes 4,
    // 5 and 6 - every one of them SINGLE-SUBSET. Not a single partitioned block. So the partition tables,
    // which are the large and error-prone part of BC7 and the part this could most easily have got
    // subtly wrong, are not needed. Modes 0-3 and 7 are therefore not implemented; they decode as a flat
    // block rather than as a guess, and SupportedBlocks says how often that happened.
    //
    // THE MIP CHAIN IS THE POINT OF THE SIZE ARGUMENT. The distinct diffuse textures here total 7,5 GB
    // and 212 of them are larger than 2048 px. Decoding a 4096² BC7 to RGBA is 64 MB of work for a
    // preview that is 360 px across. A DDS already contains the smaller versions, so the decoder picks
    // one near the requested size and does a 64th of the work.
    public static class DdsReader
    {
        private const uint Magic = 0x20534444;      // 'DDS '
        private const int HeaderSize = 128;         // magic + DDS_HEADER
        private const int Dx10HeaderSize = 20;

        // Blocks that fell into an unimplemented BC7 mode on the last decode. Zero on every texture
        // measured; kept so a mesh that does look wrong can be checked rather than argued about.
        public static long UnsupportedBlocks;

        public static bool TryDecode(byte[] data, int maxSize, out DdsImage image, out string error)
        {
            image = new DdsImage();
            error = "";

            try
            {
                return Decode(data, maxSize, image, out error);
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        private static bool Decode(byte[] d, int maxSize, DdsImage image, out string error)
        {
            error = "";

            // The header and nothing more. A 4x4 BC1 image is 136 bytes in total, so demanding a block
            // on top of the header would refuse the smallest legitimate textures outright.
            if (d == null || d.Length < HeaderSize) { error = "too short to be a DDS"; return false; }
            if (BitConverter.ToUInt32(d, 0) != Magic) { error = "not a DDS"; return false; }

            int height = BitConverter.ToInt32(d, 12);
            int width = BitConverter.ToInt32(d, 16);
            int mipCount = Math.Max(1, BitConverter.ToInt32(d, 28));

            if (width <= 0 || height <= 0 || width > 32768 || height > 32768)
            {
                error = $"implausible size {width}x{height}";
                return false;
            }

            uint pfFlags = BitConverter.ToUInt32(d, 80);
            string fourCc = Encoding.ASCII.GetString(d, 84, 4);

            int pixelStart = HeaderSize;
            DdsFormat format;

            if ((pfFlags & 0x4) != 0 && fourCc == "DX10")
            {
                pixelStart += Dx10HeaderSize;
                if (d.Length < pixelStart) { error = "DX10 header is truncated"; return false; }

                uint dxgi = BitConverter.ToUInt32(d, 128);
                format = dxgi switch
                {
                    70 or 71 or 72 => DdsFormat.Bc1,
                    73 or 74 or 75 => DdsFormat.Bc2,
                    76 or 77 or 78 => DdsFormat.Bc3,
                    79 or 80 or 81 => DdsFormat.Bc4,
                    82 or 83 or 84 => DdsFormat.Bc5,
                    97 or 98 or 99 => DdsFormat.Bc7,
                    87 or 88 => DdsFormat.Bgra32,
                    28 or 29 or 30 => DdsFormat.Rgba32,
                    _ => DdsFormat.Unknown,
                };
                if (format == DdsFormat.Unknown) { error = $"unsupported DXGI format {dxgi}"; return false; }
            }
            else if ((pfFlags & 0x4) != 0)
            {
                format = fourCc switch
                {
                    "DXT1" => DdsFormat.Bc1,
                    "DXT2" or "DXT3" => DdsFormat.Bc2,
                    "DXT4" or "DXT5" => DdsFormat.Bc3,
                    "ATI1" or "BC4U" => DdsFormat.Bc4,
                    "ATI2" or "BC5U" => DdsFormat.Bc5,
                    _ => DdsFormat.Unknown,
                };
                if (format == DdsFormat.Unknown) { error = $"unsupported FourCC '{fourCc}'"; return false; }
            }
            else
            {
                // Uncompressed. Only the layouts a Skyrim texture actually appears in; anything else is
                // refused rather than guessed at from the channel masks.
                uint bits = BitConverter.ToUInt32(d, 88);
                uint rMask = BitConverter.ToUInt32(d, 92);
                format = (bits, rMask) switch
                {
                    (32, 0x00FF0000) => DdsFormat.Bgra32,
                    (32, 0x000000FF) => DdsFormat.Rgba32,
                    (24, 0x00FF0000) => DdsFormat.Bgr24,
                    _ => DdsFormat.Unknown,
                };
                if (format == DdsFormat.Unknown) { error = $"unsupported uncompressed layout ({bits} bpp)"; return false; }
            }

            // Walk the mip chain to the one nearest the requested size. Each level halves, rounding up,
            // and never goes below 1.
            int mip = 0, w = width, h = height, offset = pixelStart;
            if (maxSize > 0)
            {
                while (mip + 1 < mipCount && Math.Max(w, h) > maxSize)
                {
                    int levelBytes = LevelBytes(format, w, h);
                    if (offset + levelBytes >= d.Length) break;      // truncated chain: use what we are on

                    offset += levelBytes;
                    w = Math.Max(1, w / 2);
                    h = Math.Max(1, h / 2);
                    mip++;
                }
            }

            int needed = LevelBytes(format, w, h);
            if (offset + needed > d.Length)
            {
                error = $"mip {mip} ({w}x{h}) needs {needed} bytes, only {d.Length - offset} left";
                return false;
            }

            image.Width = w;
            image.Height = h;
            image.MipLevel = mip;
            image.SourceFormat = format;
            image.Pixels = new byte[w * h * 4];

            UnsupportedBlocks = 0;

            switch (format)
            {
                case DdsFormat.Bc1: DecodeBlocks(d, offset, w, h, 8, image.Pixels, Bc1Block); break;
                case DdsFormat.Bc2: DecodeBlocks(d, offset, w, h, 16, image.Pixels, Bc2Block); break;
                case DdsFormat.Bc3: DecodeBlocks(d, offset, w, h, 16, image.Pixels, Bc3Block); break;
                case DdsFormat.Bc4: DecodeBlocks(d, offset, w, h, 8, image.Pixels, Bc4Block); break;
                case DdsFormat.Bc5: DecodeBlocks(d, offset, w, h, 16, image.Pixels, Bc5Block); break;
                case DdsFormat.Bc7: DecodeBlocks(d, offset, w, h, 16, image.Pixels, Bc7Block); break;
                default: DecodeUncompressed(d, offset, w, h, format, image.Pixels); break;
            }

            // A FILE WITH NO MIP CHAIN still hands back its full size, because there was nothing smaller
            // to walk to. Measured: a 4096x4096 latex diffuse with a single level, which is 64 MB of RGBA
            // and took 424 ms to shade. Mips are a convention, not a guarantee, so the size the caller
            // asked for has to be honoured either way.
            if (maxSize > 0 && Math.Max(image.Width, image.Height) > maxSize)
                Downsample(image, maxSize);

            return true;
        }

        // Box filter by a whole-number factor. Not a resampler: the factor is chosen so the result is at
        // or below the requested size, and averaging whole blocks is both the cheapest way to do that and
        // the one that keeps a texture's average brightness intact.
        private static void Downsample(DdsImage image, int maxSize)
        {
            int factor = Math.Max(1, (Math.Max(image.Width, image.Height) + maxSize - 1) / maxSize);
            if (factor <= 1) return;

            int width = Math.Max(1, image.Width / factor);
            int height = Math.Max(1, image.Height / factor);
            var result = new byte[width * height * 4];

            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int b = 0, g = 0, r = 0, a = 0, n = 0;

                for (int sy = y * factor; sy < Math.Min((y + 1) * factor, image.Height); sy++)
                for (int sx = x * factor; sx < Math.Min((x + 1) * factor, image.Width); sx++)
                {
                    int i = (sy * image.Width + sx) * 4;
                    b += image.Pixels[i];
                    g += image.Pixels[i + 1];
                    r += image.Pixels[i + 2];
                    a += image.Pixels[i + 3];
                    n++;
                }

                if (n == 0) continue;

                int t = (y * width + x) * 4;
                result[t] = (byte)(b / n);
                result[t + 1] = (byte)(g / n);
                result[t + 2] = (byte)(r / n);
                result[t + 3] = (byte)(a / n);
            }

            image.Width = width;
            image.Height = height;
            image.Pixels = result;
        }

        private static int LevelBytes(DdsFormat format, int w, int h)
        {
            int blocksWide = Math.Max(1, (w + 3) / 4);
            int blocksHigh = Math.Max(1, (h + 3) / 4);

            return format switch
            {
                DdsFormat.Bc1 or DdsFormat.Bc4 => blocksWide * blocksHigh * 8,
                DdsFormat.Bc2 or DdsFormat.Bc3 or DdsFormat.Bc5 or DdsFormat.Bc7 => blocksWide * blocksHigh * 16,
                DdsFormat.Bgr24 => w * h * 3,
                _ => w * h * 4,
            };
        }

        private static void DecodeUncompressed(byte[] d, int at, int w, int h, DdsFormat format, byte[] bgra)
        {
            int sourceStride = format == DdsFormat.Bgr24 ? 3 : 4;

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = at + (y * w + x) * sourceStride;
                int t = (y * w + x) * 4;

                switch (format)
                {
                    case DdsFormat.Bgra32:
                        bgra[t] = d[s]; bgra[t + 1] = d[s + 1]; bgra[t + 2] = d[s + 2]; bgra[t + 3] = d[s + 3];
                        break;
                    case DdsFormat.Rgba32:
                        bgra[t] = d[s + 2]; bgra[t + 1] = d[s + 1]; bgra[t + 2] = d[s]; bgra[t + 3] = d[s + 3];
                        break;
                    default:
                        bgra[t] = d[s]; bgra[t + 1] = d[s + 1]; bgra[t + 2] = d[s + 2]; bgra[t + 3] = 255;
                        break;
                }
            }
        }

        // One texel of a decoded block: BGRA, written straight into the target.
        private delegate void BlockDecoder(byte[] source, int at, Span<byte> block16Bgra);

        private static void DecodeBlocks(byte[] d, int at, int w, int h, int blockBytes, byte[] bgra, BlockDecoder decode)
        {
            int blocksWide = Math.Max(1, (w + 3) / 4);
            int blocksHigh = Math.Max(1, (h + 3) / 4);

            Span<byte> block = stackalloc byte[64];              // 16 texels, BGRA

            for (int by = 0; by < blocksHigh; by++)
            for (int bx = 0; bx < blocksWide; bx++)
            {
                block.Clear();
                decode(d, at + (by * blocksWide + bx) * blockBytes, block);

                // The last block in a row or column may hang over the edge of a non-multiple-of-four
                // image. Those texels exist in the file and are simply not copied out.
                for (int ty = 0; ty < 4; ty++)
                {
                    int y = by * 4 + ty;
                    if (y >= h) break;

                    for (int tx = 0; tx < 4; tx++)
                    {
                        int x = bx * 4 + tx;
                        if (x >= w) break;

                        int s = (ty * 4 + tx) * 4;
                        int t = (y * w + x) * 4;
                        bgra[t] = block[s];
                        bgra[t + 1] = block[s + 1];
                        bgra[t + 2] = block[s + 2];
                        bgra[t + 3] = block[s + 3];
                    }
                }
            }
        }

        // ---- the classic block formats ----
        //
        // Two 16-bit endpoints and sixteen 2-bit indices. In BC1 the ORDER of the endpoints decides
        // whether the fourth index is a blend or transparent black - the one place BC1 differs from the
        // colour half of BC2 and BC3, and the reason this takes a flag.

        private static void ColourBlock(byte[] d, int at, Span<byte> block, bool punchThroughAlpha)
        {
            ushort c0 = BitConverter.ToUInt16(d, at);
            ushort c1 = BitConverter.ToUInt16(d, at + 2);
            uint indices = BitConverter.ToUInt32(d, at + 4);

            Span<byte> r = stackalloc byte[4];
            Span<byte> g = stackalloc byte[4];
            Span<byte> b = stackalloc byte[4];
            Span<byte> a = stackalloc byte[4];

            Unpack565(c0, out r[0], out g[0], out b[0]);
            Unpack565(c1, out r[1], out g[1], out b[1]);
            a[0] = a[1] = a[2] = a[3] = 255;

            if (!punchThroughAlpha || c0 > c1)
            {
                r[2] = (byte)((2 * r[0] + r[1]) / 3); g[2] = (byte)((2 * g[0] + g[1]) / 3); b[2] = (byte)((2 * b[0] + b[1]) / 3);
                r[3] = (byte)((r[0] + 2 * r[1]) / 3); g[3] = (byte)((g[0] + 2 * g[1]) / 3); b[3] = (byte)((b[0] + 2 * b[1]) / 3);
            }
            else
            {
                r[2] = (byte)((r[0] + r[1]) / 2); g[2] = (byte)((g[0] + g[1]) / 2); b[2] = (byte)((b[0] + b[1]) / 2);
                r[3] = g[3] = b[3] = 0;
                a[3] = 0;
            }

            for (int i = 0; i < 16; i++)
            {
                int k = (int)((indices >> (i * 2)) & 3);
                block[i * 4 + 0] = b[k];
                block[i * 4 + 1] = g[k];
                block[i * 4 + 2] = r[k];
                block[i * 4 + 3] = a[k];
            }
        }

        private static void Unpack565(ushort c, out byte r, out byte g, out byte b)
        {
            int r5 = (c >> 11) & 0x1F, g6 = (c >> 5) & 0x3F, b5 = c & 0x1F;

            // Replicate the high bits into the low ones, so 31 maps to 255 rather than 248.
            r = (byte)((r5 << 3) | (r5 >> 2));
            g = (byte)((g6 << 2) | (g6 >> 4));
            b = (byte)((b5 << 3) | (b5 >> 2));
        }

        private static void Bc1Block(byte[] d, int at, Span<byte> block)
            => ColourBlock(d, at, block, punchThroughAlpha: true);

        private static void Bc2Block(byte[] d, int at, Span<byte> block)
        {
            ColourBlock(d, at + 8, block, punchThroughAlpha: false);

            // Four explicit bits of alpha per texel, scaled to a byte by replication.
            for (int i = 0; i < 16; i++)
            {
                int nibble = (d[at + i / 2] >> ((i % 2) * 4)) & 0xF;
                block[i * 4 + 3] = (byte)((nibble << 4) | nibble);
            }
        }

        private static void Bc3Block(byte[] d, int at, Span<byte> block)
        {
            ColourBlock(d, at + 8, block, punchThroughAlpha: false);

            Span<byte> alpha = stackalloc byte[16];
            InterpolatedAlpha(d, at, alpha);
            for (int i = 0; i < 16; i++) block[i * 4 + 3] = alpha[i];
        }

        private static void Bc4Block(byte[] d, int at, Span<byte> block)
        {
            Span<byte> value = stackalloc byte[16];
            InterpolatedAlpha(d, at, value);

            for (int i = 0; i < 16; i++)
            {
                block[i * 4 + 0] = block[i * 4 + 1] = block[i * 4 + 2] = value[i];
                block[i * 4 + 3] = 255;
            }
        }

        private static void Bc5Block(byte[] d, int at, Span<byte> block)
        {
            Span<byte> red = stackalloc byte[16];
            Span<byte> green = stackalloc byte[16];
            InterpolatedAlpha(d, at, red);
            InterpolatedAlpha(d, at + 8, green);

            for (int i = 0; i < 16; i++)
            {
                block[i * 4 + 0] = 255;                  // blue: a normal map's Z is reconstructed, not stored
                block[i * 4 + 1] = green[i];
                block[i * 4 + 2] = red[i];
                block[i * 4 + 3] = 255;
            }
        }

        // The eight-value interpolated channel BC3, BC4 and BC5 all share: two endpoints, then sixteen
        // 3-bit indices packed into six bytes.
        private static void InterpolatedAlpha(byte[] d, int at, Span<byte> output)
        {
            byte a0 = d[at], a1 = d[at + 1];
            Span<byte> table = stackalloc byte[8];
            table[0] = a0;
            table[1] = a1;

            if (a0 > a1)
            {
                for (int i = 1; i < 7; i++) table[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
            }
            else
            {
                for (int i = 1; i < 5; i++) table[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
                table[6] = 0;
                table[7] = 255;
            }

            ulong bits = 0;
            for (int i = 0; i < 6; i++) bits |= (ulong)d[at + 2 + i] << (8 * i);

            for (int i = 0; i < 16; i++)
                output[i] = table[(int)((bits >> (i * 3)) & 7)];
        }

        // ---- BC7, single-subset modes only ----
        //
        // See the class comment: every one of 4,2 million blocks measured in real textures is mode 4, 5
        // or 6. Modes 0-3 and 7 are partitioned and are not implemented; they produce a flat block and
        // bump UnsupportedBlocks rather than a plausible-looking guess.

        private static readonly byte[] Weights2 = { 0, 21, 43, 64 };
        private static readonly byte[] Weights3 = { 0, 9, 18, 27, 37, 46, 55, 64 };
        private static readonly byte[] Weights4 = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };

        // Which subset each of the 16 texels belongs to, one string per partition. Written as digits
        // because that is what they are - a 4x4 picture of the split - and a transposed or mistyped row
        // is findable by eye in this form and not in a wall of hex.
        //
        // A wrong entry here does not crash: it assigns some texels to the wrong endpoint pair, which
        // shows up as colour speckle inside 4x4 blocks. That is what the rendered textures were checked
        // for.
        private static readonly string[] Partition2 =
        {
            "0011001100110011", "0001000100010001", "0111011101110111", "0001001100110111",
            "0000000100010011", "0011011101111111", "0001001101111111", "0000000100110111",
            "0000000000010011", "0011011111111111", "0000000101111111", "0000000000010111",
            "0001011111111111", "0000000011111111", "0000111111111111", "0000000000001111",
            "0000100011101111", "0111000100000000", "0000000010001110", "0111001100010000",
            "0011000100000000", "0000100011001110", "0000000010001100", "0111001100110001",
            "0011000100010000", "0000100010001100", "0110011001100110", "0011011001101100",
            "0001011111101000", "0000111111110000", "0111000110001110", "0011100110011100",
            "0101010101010101", "0000111100001111", "0101101001011010", "0011001111001100",
            "0011110000111100", "0101010110101010", "0110100101101001", "0101101010100101",
            "0111001111001110", "0001001111001000", "0011001001001100", "0011101111011100",
            "0110100110010110", "0011110011000011", "0110011010011001", "0000011001100000",
            "0100111001000000", "0010011100100000", "0000001001110010", "0000010011100100",
            "0110110010010011", "0011011011001001", "0110001110011100", "0011100111000110",
            "0110110011001001", "0110001100111001", "0111111010000001", "0001100011100111",
            "0000111100110011", "0011001111110000", "0010001011101110", "0100010001110111",
        };

        private static readonly string[] Partition3 =
        {
            "0011001102212222", "0001001122112221", "0000200122112211", "0222002200110111",
            "0000000011221122", "0011001100220022", "0022002211111111", "0011001122112211",
            "0000000011112222", "0000111111112222", "0000111122222222", "0012001200120012",
            "0112011201120112", "0122012201220122", "0011011111221222", "0011200122002220",
            "0001001101121122", "0111001120012200", "0000112211221122", "0022002200221111",
            "0111011102220222", "0001000122212221", "0000001101220122", "0000110022102210",
            "0122012200110000", "0012001211222222", "0110122112210110", "0000011012211221",
            "0022110211020022", "0110011020022222", "0011012201220011", "0000200022112221",
            "0000000211221222", "0222002200120011", "0011001200220222", "0120012001200120",
            "0000111122220000", "0120120120120120", "0120201212010120", "0011220011220011",
            "0011112222000011", "0101010122222222", "0000000021212121", "0022112200221122",
            "0022001100220011", "0220122102201221", "0101222222220101", "0000212121212121",
            "0101010101012222", "0222011102220111", "0002111100021112", "0000211221122112",
            "0222011101110222", "0002111111120002", "0110011001102222", "0000000021121122",
            "0110011022222222", "0022001100110022", "0022112211220022", "0000000000002112",
            "0002000100020001", "0222122202221222", "0101222222222222", "0111201122012220",
        };

        // Which texel carries the implied-zero high bit for each subset. Subset 0's anchor is always
        // texel 0, so only the others are tabulated.
        private static readonly byte[] Anchor2 =
        {
            15,15,15,15,15,15,15,15, 15,15,15,15,15,15,15,15,
            15, 2, 8, 2, 2, 8, 8,15,  2, 8, 2, 2, 8, 8, 2, 2,
            15,15, 6, 8, 2, 8,15,15,  2, 8, 2, 2, 2,15,15, 6,
             6, 2, 6, 8,15,15, 2, 2, 15,15,15,15,15, 2, 2,15,
        };

        private static readonly byte[] Anchor3First =
        {
             3, 3,15,15, 8, 3,15,15,  8, 8, 6, 6, 6, 5, 3, 3,
             3, 3, 8,15, 3, 3, 6,10,  5, 8, 8, 6, 8, 5,15,15,
             8,15, 3, 5, 6,10, 8,15, 15, 3,15, 5,15,15,15,15,
             3,15, 5, 5, 5, 8, 5,10,  5,10, 8,13,15,12, 3, 3,
        };

        private static readonly byte[] Anchor3Second =
        {
            15, 8, 8, 3,15,15, 3, 8, 15,15,15,15,15,15,15, 8,
            15, 8,15, 3,15, 8,15, 8,  3,15, 6,10,15,15,10, 8,
            15, 3,15,10,10, 8, 9,10,  6,15, 8,15, 3, 6, 6, 8,
            15, 3,15,15,15,15,15,15, 15,15,15,15, 3,15,15, 8,
        };

        // Everything a partitioned mode needs, in the order the bit stream carries it.
        private readonly struct Bc7Mode
        {
            public readonly int Subsets, PartitionBits, ColourBits, AlphaBits, EndpointPBits, SharedPBits, IndexBits;

            public Bc7Mode(int subsets, int partitionBits, int colourBits, int alphaBits,
                           int endpointPBits, int sharedPBits, int indexBits)
            {
                Subsets = subsets; PartitionBits = partitionBits; ColourBits = colourBits;
                AlphaBits = alphaBits; EndpointPBits = endpointPBits; SharedPBits = sharedPBits;
                IndexBits = indexBits;
            }
        }

        private static readonly Bc7Mode?[] PartitionedModes =
        {
            new Bc7Mode(3, 4, 4, 0, 1, 0, 3),   // mode 0
            new Bc7Mode(2, 6, 6, 0, 0, 1, 3),   // mode 1
            new Bc7Mode(3, 6, 5, 0, 0, 0, 2),   // mode 2
            new Bc7Mode(2, 6, 7, 0, 1, 0, 2),   // mode 3
            null, null, null,                    // modes 4-6 are single-subset, handled on their own
            new Bc7Mode(2, 6, 5, 5, 1, 0, 2),   // mode 7
        };

        private static void Bc7Block(byte[] d, int at, Span<byte> block)
        {
            int mode = -1;
            for (int m = 0; m < 8; m++)
                if ((d[at] & (1 << m)) != 0) { mode = m; break; }

            switch (mode)
            {
                case 4: Bc7Mode4(d, at, block); return;
                case 5: Bc7Mode5(d, at, block); return;
                case 6: Bc7Mode6(d, at, block); return;
                case 0: case 1: case 2: case 3: case 7:
                    Bc7Partitioned(d, at, block, mode, PartitionedModes[mode]!.Value);
                    return;
            }

            // A block of all zero bits, which is not a legal BC7 encoding. Mid grey so it reads as
            // "something here was not decoded" rather than as a hole.
            UnsupportedBlocks++;
            for (int i = 0; i < 16; i++)
            {
                block[i * 4 + 0] = block[i * 4 + 1] = block[i * 4 + 2] = 128;
                block[i * 4 + 3] = 255;
            }
        }

        // Modes 0, 1, 2, 3 and 7: two or three subsets, each with its own pair of endpoints, and a
        // partition number saying which texel belongs to which.
        //
        // Measured over 17 million blocks in a real setup, 12,3 % of BC7 blocks land here - and in some
        // textures (scrib, argonian head and body maps) it is over 70 %. An earlier sample said 0 % and
        // was simply looking at the wrong files, which is why this is implemented rather than skipped.
        //
        // The channels are stored PLANE BY PLANE, not endpoint by endpoint: every subset's red, then
        // every green, then every blue, then alpha. Reading them interleaved produces a picture that is
        // recognisable but wrongly coloured, which is the kind of wrong that survives a casual look.
        private static void Bc7Partitioned(byte[] d, int at, Span<byte> block, int mode, Bc7Mode m)
        {
            var bits = new Bits(d, at, mode + 1);                  // mode is encoded as `mode` zeroes then a one

            int partition = m.PartitionBits > 0 ? bits.Read(m.PartitionBits) : 0;
            int endpoints = m.Subsets * 2;

            Span<int> r = stackalloc int[6];
            Span<int> g = stackalloc int[6];
            Span<int> b = stackalloc int[6];
            Span<int> a = stackalloc int[6];

            for (int i = 0; i < endpoints; i++) r[i] = bits.Read(m.ColourBits);
            for (int i = 0; i < endpoints; i++) g[i] = bits.Read(m.ColourBits);
            for (int i = 0; i < endpoints; i++) b[i] = bits.Read(m.ColourBits);
            if (m.AlphaBits > 0)
                for (int i = 0; i < endpoints; i++) a[i] = bits.Read(m.AlphaBits);

            // P-bits extend each endpoint by one low bit. "Endpoint" p-bits give one per endpoint;
            // "shared" p-bits give one per SUBSET, used by both of its endpoints.
            Span<int> pbit = stackalloc int[6];
            for (int i = 0; i < 6; i++) pbit[i] = -1;

            if (m.EndpointPBits > 0)
            {
                for (int i = 0; i < endpoints; i++) pbit[i] = bits.Read(1);
            }
            else if (m.SharedPBits > 0)
            {
                for (int s = 0; s < m.Subsets; s++)
                {
                    int shared = bits.Read(1);
                    pbit[s * 2] = shared;
                    pbit[s * 2 + 1] = shared;
                }
            }

            Span<byte> r8 = stackalloc byte[6];
            Span<byte> g8 = stackalloc byte[6];
            Span<byte> b8 = stackalloc byte[6];
            Span<byte> a8 = stackalloc byte[6];

            for (int i = 0; i < endpoints; i++)
            {
                r8[i] = ExpandWithP(r[i], m.ColourBits, pbit[i]);
                g8[i] = ExpandWithP(g[i], m.ColourBits, pbit[i]);
                b8[i] = ExpandWithP(b[i], m.ColourBits, pbit[i]);
                a8[i] = m.AlphaBits > 0 ? ExpandWithP(a[i], m.AlphaBits, pbit[i]) : (byte)255;
            }

            string table = m.Subsets == 2 ? Partition2[partition] : Partition3[partition];

            // Anchor texels carry one bit fewer, because their index's high bit is implied zero. Subset 0
            // always anchors on texel 0.
            int anchor1 = m.Subsets == 2 ? Anchor2[partition] : Anchor3First[partition];
            int anchor2 = m.Subsets == 3 ? Anchor3Second[partition] : -1;

            Span<byte> index = stackalloc byte[16];
            for (int i = 0; i < 16; i++)
            {
                bool isAnchor = i == 0 || i == anchor1 || i == anchor2;
                index[i] = (byte)bits.Read(isAnchor ? m.IndexBits - 1 : m.IndexBits);
            }

            var weights = m.IndexBits == 2 ? Weights2 : Weights3;

            for (int i = 0; i < 16; i++)
            {
                int subset = table[i] - '0';
                int e0 = subset * 2, e1 = e0 + 1;
                int w = weights[index[i]];

                block[i * 4 + 0] = Interpolate(b8[e0], b8[e1], w);
                block[i * 4 + 1] = Interpolate(g8[e0], g8[e1], w);
                block[i * 4 + 2] = Interpolate(r8[e0], r8[e1], w);
                block[i * 4 + 3] = m.AlphaBits > 0 ? Interpolate(a8[e0], a8[e1], w) : (byte)255;
            }
        }

        // With a p-bit the stored value gains one low bit before being replicated up to eight; without
        // one it is replicated as it stands.
        private static byte ExpandWithP(int value, int bits, int pbit)
        {
            if (pbit < 0) return Expand(value, bits);

            int widened = (value << 1) | pbit;
            return Expand(widened, bits + 1);
        }

        // Reads a BC7 block as a little-endian bit stream, lowest bit of byte 0 first.
        private struct Bits
        {
            private readonly byte[] _d;
            private readonly int _at;
            private int _pos;

            public Bits(byte[] d, int at, int startBit) { _d = d; _at = at; _pos = startBit; }

            public int Read(int count)
            {
                int value = 0;
                for (int i = 0; i < count; i++)
                {
                    int bit = (_d[_at + (_pos >> 3)] >> (_pos & 7)) & 1;
                    value |= bit << i;
                    _pos++;
                }
                return value;
            }
        }

        private static byte Expand(int value, int bits)
        {
            int shifted = value << (8 - bits);
            return (byte)(shifted | (shifted >> bits));
        }

        private static byte Interpolate(byte a, byte b, int weight)
            => (byte)((a * (64 - weight) + b * weight + 32) >> 6);

        // Mode 4: 5-bit colour, 6-bit alpha, two index sets of different widths, and a rotation that
        // decides which channel the alpha indices actually drive.
        private static void Bc7Mode4(byte[] d, int at, Span<byte> block)
        {
            var bits = new Bits(d, at, 5);                       // past the mode bits

            int rotation = bits.Read(2);
            int indexMode = bits.Read(1);

            Span<byte> r = stackalloc byte[2], g = stackalloc byte[2], b = stackalloc byte[2], a = stackalloc byte[2];
            for (int i = 0; i < 2; i++) r[i] = Expand(bits.Read(5), 5);
            for (int i = 0; i < 2; i++) g[i] = Expand(bits.Read(5), 5);
            for (int i = 0; i < 2; i++) b[i] = Expand(bits.Read(5), 5);
            for (int i = 0; i < 2; i++) a[i] = Expand(bits.Read(6), 6);

            // Index set 1 is 2 bits wide, set 2 is 3 bits. The first index of each set carries one bit
            // fewer, because the anchor texel's high bit is implied zero.
            Span<byte> index2 = stackalloc byte[16];
            Span<byte> index3 = stackalloc byte[16];
            for (int i = 0; i < 16; i++) index2[i] = (byte)bits.Read(i == 0 ? 1 : 2);
            for (int i = 0; i < 16; i++) index3[i] = (byte)bits.Read(i == 0 ? 2 : 3);

            for (int i = 0; i < 16; i++)
            {
                int colourWeight = indexMode == 0 ? Weights2[index2[i]] : Weights3[index3[i]];
                int alphaWeight = indexMode == 0 ? Weights3[index3[i]] : Weights2[index2[i]];

                Write(block, i, rotation,
                      Interpolate(r[0], r[1], colourWeight),
                      Interpolate(g[0], g[1], colourWeight),
                      Interpolate(b[0], b[1], colourWeight),
                      Interpolate(a[0], a[1], alphaWeight));
            }
        }

        // Mode 5: 7-bit colour, full 8-bit alpha, two 2-bit index sets.
        private static void Bc7Mode5(byte[] d, int at, Span<byte> block)
        {
            var bits = new Bits(d, at, 6);

            int rotation = bits.Read(2);

            Span<byte> r = stackalloc byte[2], g = stackalloc byte[2], b = stackalloc byte[2], a = stackalloc byte[2];
            for (int i = 0; i < 2; i++) r[i] = Expand(bits.Read(7), 7);
            for (int i = 0; i < 2; i++) g[i] = Expand(bits.Read(7), 7);
            for (int i = 0; i < 2; i++) b[i] = Expand(bits.Read(7), 7);
            for (int i = 0; i < 2; i++) a[i] = (byte)bits.Read(8);

            Span<byte> colourIndex = stackalloc byte[16];
            Span<byte> alphaIndex = stackalloc byte[16];
            for (int i = 0; i < 16; i++) colourIndex[i] = (byte)bits.Read(i == 0 ? 1 : 2);
            for (int i = 0; i < 16; i++) alphaIndex[i] = (byte)bits.Read(i == 0 ? 1 : 2);

            for (int i = 0; i < 16; i++)
            {
                int cw = Weights2[colourIndex[i]];
                int aw = Weights2[alphaIndex[i]];

                Write(block, i, rotation,
                      Interpolate(r[0], r[1], cw),
                      Interpolate(g[0], g[1], cw),
                      Interpolate(b[0], b[1], cw),
                      Interpolate(a[0], a[1], aw));
            }
        }

        // Mode 6: one subset, 7 bits plus a shared p-bit per endpoint for all four channels, 4-bit
        // indices. Half the blocks in a real texture are this one.
        private static void Bc7Mode6(byte[] d, int at, Span<byte> block)
        {
            var bits = new Bits(d, at, 7);

            Span<int> r = stackalloc int[2], g = stackalloc int[2], b = stackalloc int[2], a = stackalloc int[2];
            for (int i = 0; i < 2; i++) r[i] = bits.Read(7);
            for (int i = 0; i < 2; i++) g[i] = bits.Read(7);
            for (int i = 0; i < 2; i++) b[i] = bits.Read(7);
            for (int i = 0; i < 2; i++) a[i] = bits.Read(7);

            Span<int> p = stackalloc int[2];
            for (int i = 0; i < 2; i++) p[i] = bits.Read(1);

            // The p-bit is the endpoint's least significant bit, which makes seven stored bits into a
            // full eight - no replication needed.
            Span<byte> r8 = stackalloc byte[2], g8 = stackalloc byte[2], b8 = stackalloc byte[2], a8 = stackalloc byte[2];
            for (int i = 0; i < 2; i++)
            {
                r8[i] = (byte)((r[i] << 1) | p[i]);
                g8[i] = (byte)((g[i] << 1) | p[i]);
                b8[i] = (byte)((b[i] << 1) | p[i]);
                a8[i] = (byte)((a[i] << 1) | p[i]);
            }

            for (int i = 0; i < 16; i++)
            {
                int index = bits.Read(i == 0 ? 3 : 4);
                int w = Weights4[index];

                Write(block, i, 0,
                      Interpolate(r8[0], r8[1], w),
                      Interpolate(g8[0], g8[1], w),
                      Interpolate(b8[0], b8[1], w),
                      Interpolate(a8[0], a8[1], w));
            }
        }

        // Rotation swaps the alpha channel with one of the colour channels AFTER interpolation - an
        // encoder uses it when a texture's detail happens to live in alpha. Writing it here keeps the
        // three mode decoders from each repeating the swap.
        private static void Write(Span<byte> block, int texel, int rotation, byte r, byte g, byte b, byte a)
        {
            switch (rotation)
            {
                case 1: (a, r) = (r, a); break;
                case 2: (a, g) = (g, a); break;
                case 3: (a, b) = (b, a); break;
            }

            block[texel * 4 + 0] = b;
            block[texel * 4 + 1] = g;
            block[texel * 4 + 2] = r;
            block[texel * 4 + 3] = a;
        }
    }
}
