using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Pynelith.Island.Services.Siri
{
    /// <summary>
    /// Edge 朗读接口的 Sec-MS-GEC v2 令牌生成。
    ///
    /// 服务端从 2025 年起只认 "<16 位 CS64>-<64 位 SHA256>" 形式的新令牌，
    /// 旧版纯 SHA256 令牌一律返回 403。这里的算法与公开实现（edge-tts）保持一致：
    /// 以五分钟取整的 Windows 文件时间为盐，先跑一个改过常量与初始状态的 SHA256，
    /// 再用摘要里的两个 32 位字做一轮自定义混合得到 CS64 部分。
    /// </summary>
    internal static class EdgeTtsToken
    {
        private const uint Mask = 0xFFFFFFFFu;

        private static readonly uint[] RoundConstants =
        {
            0x428A2F98, 0x71374491, 0xB5C0FBCF, 0xE9B5DBA5, 0x3956C25B, 0x59F111F1, 0x923F82A4, 0xAB1C5ED5,
            0xD807AA98, 0x12835B01, 0x243185BE, 0x550C7DC3, 0x72BE5D74, 0x80DEB1FE, 0x9BDC06A7, 0xC19BF174,
            0xE49B69C1, 0xEFBE4786, 0x0FC19DC6, 0x240CA1CC, 0x2DE92C6F, 0x4A7484AA, 0x5CB0A9DC, 0x76F988DA,
            0x983E5152, 0xA831C66D, 0xB00327C8, 0xBF597FC7, 0xC6E00BF3, 0xD5A79147, 0x06CA6351, 0x14292967,
            0x27B70A85, 0x2E1B2138, 0x4D2C6DFC, 0x53380D13, 0x650A7354, 0x766A0ABB, 0x81C2C92E, 0x92722C85,
            0xA2BFE8A1, 0xA81A664B, 0xC24B8B70, 0xC76C51A3, 0xD192E819, 0xD6990624, 0xF40E3585, 0x106AA070,
            0x19A4C116, 0x1E376C08, 0x2748774C, 0x34B0BCB5, 0x391C0CB3, 0x4ED8AA4A, 0x5B9CCA4F, 0x682E6FF3,
            0x748F82EE, 0x78A5636F, 0x84C87814, 0x8CC70208, 0x90BEFFFA, 0xA4506CEB, 0xBEF9A3F7, 0xC67178F2,
        };

        /// <summary>第 14 轮常量被替换成一枚自定义常数后的版本。</summary>
        private static readonly uint[] BlockConstants = BuildBlockConstants();

        private static readonly uint[] InitialState =
        {
            0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19,
        };

        private static readonly uint[] SeedState =
        {
            0xFD9FBEA2, 0x91243CE7, 0xBE499094, 0xCD2E1E0A, 0x6C006F49, 0x04BD35AB, 0xFEB0A7E4, 0x03EFFBD2,
        };

        private static readonly uint[] SeedSchedule =
        {
            0xEB24BF9E, 0x1558691F, 0xA0C09EE2, 0x0CFA32BA, 0xA622EE8C, 0xD1DD9491,
            0xC0DD25A0, 0xF9E70AB6, 0xF0C31573, 0x7058CE4E, 0x7188E606, 0x00E5AB33,
            0xD258B060, 0x72766564,
        };

        private static readonly (uint Coefficient, uint Offset)[] Cs64Coefficients =
        {
            (0xE82E91B1, 0xB32700D0), (0x44DE24C6, 0x88DFF51D), (0x3BEB65BC, 0x29514038),
            (0xC89C5933, 0xB328CBDF), (0x8D1EC211, 0xD405B4D5), (0x314EF5D5, 0x26E55F3C),
            (0xE92491B1, 0x607801C6), (0xF0A5B9D5, 0x82B2C1FB), (0x06064CF3, 0xFA51A3DA),
            (0xDA79FA60, 0x207CC464), (0xBED20560, 0x3C3C74F3), (0x89F9C9F8, 0xD2D14B61),
            (0x35FA38C6, 0xBB04A2C1), (0xCB49E3C6, 0xC5626167),
        };

        /// <summary>
        /// 生成当前时刻的令牌。
        /// </summary>
        /// <param name="unixSeconds">可选：指定时间（用于自测对拍）。</param>
        public static string Generate(double? unixSeconds = null)
        {
            double ticks = unixSeconds
                ?? (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);

            ticks += 11644473600.0;
            ticks -= ticks % 300;
            ticks *= 10_000_000.0; // 换算成 100 纳秒单位

            string raw = ((long)ticks).ToString(CultureInfo.InvariantCulture) + SiriSpeaker.TrustedClientToken;
            byte[] data = Encoding.ASCII.GetBytes(raw);

            byte[] digest = Sha256Digest(data);
            (uint low, uint high) = Cs64Digest(data, digest);

            string cs64 = LittleEndianHex(low) + LittleEndianHex(high);
            string sha = Convert.ToHexString(digest);

            return cs64.ToUpperInvariant() + "-" + sha;
        }

        private static string LittleEndianHex(uint value)
            => (value & 0xFF).ToString("X2")
               + ((value >> 8) & 0xFF).ToString("X2")
               + ((value >> 16) & 0xFF).ToString("X2")
               + ((value >> 24) & 0xFF).ToString("X2");

        private static uint[] BuildBlockConstants()
        {
            var result = (uint[])RoundConstants.Clone();
            result[14] = 0xC9DC06A7;
            return result;
        }

        private static uint RotateLeft16(uint value) => (value << 16) | (value >> 16);

        private static uint Sigma0(uint value)
            => unchecked((value >> 7) ^ (value << 25) ^ (value >> 18) ^ (value << 14) ^ (value >> 3));

        private static uint Sigma1(uint value)
            => unchecked((value >> 17) ^ (value << 15) ^ (value >> 19) ^ (value << 13) ^ (value >> 10));

        private static uint RotateRight(uint value, int bits)
            => (value >> bits) | (value << (32 - bits));

        /// <summary>单个 64 字节块的消息扩展（与标准 SHA-256 相同）。</summary>
        private static void Expand(List<uint> words, int firstWord = 16)
        {
            for (int i = firstWord; i < 64; i++)
            {
                uint x = words[i - 2];
                uint y = words[i - 15];

                uint sigma1 = unchecked((x >> 17) ^ (x << 15) ^ (x >> 19) ^ (x << 13) ^ (x >> 10));
                uint sigma0 = unchecked((y >> 7) ^ (y << 25) ^ (y >> 18) ^ (y << 14) ^ (y >> 3));

                words.Add(unchecked(sigma1 + words[i - 7] + sigma0 + words[i - 16]));
            }
        }

        /// <summary>压缩函数；<paramref name="firstRound"/> = 14 时沿用"块前缀"常量表。</summary>
        private static uint[] Rounds(uint[] state, List<uint> words, int firstRound = 0)
        {
            uint[] constants = firstRound == 14 ? BlockConstants : RoundConstants;

            uint a = state[0], b = state[1], c = state[2], d = state[3];
            uint e = state[4], f = state[5], g = state[6], h = state[7];

            for (int i = firstRound; i < 64; i++)
            {
                uint sigma1 = RotateRight(e, 6) ^ RotateRight(e, 11) ^ RotateRight(e, 25);
                uint choose = g ^ (e & (f ^ g));
                uint temp1 = unchecked(h + sigma1 + choose + constants[i] + words[i]);

                uint sigma0 = RotateRight(a, 2) ^ RotateRight(a, 13) ^ RotateRight(a, 22);
                uint majority = (a & b) | (c & (a | b));
                uint temp2 = unchecked(sigma0 + majority);

                h = g;
                g = f;
                f = e;
                e = unchecked(d + temp1);
                d = c;
                c = b;
                b = a;
                a = unchecked(temp1 + temp2);
            }

            return new[] { a, b, c, d, e, f, g, h };
        }

        /// <summary>用被改写的 SHA-256 计算盐值的 32 字节摘要。</summary>
        private static byte[] Sha256Digest(byte[] data)
        {
            if (data.Length < 7)
            {
                throw new ArgumentException("盐值至少需要 7 字节", nameof(data));
            }

            int length = 57 + data.Length;
            var padded = new List<byte>(data.Length + 64);

            for (int i = 7; i < data.Length; i++)
            {
                padded.Add(data[i]);
            }

            padded.Add(0x80);
            int zeros = (55 - length) % 64;
            if (zeros < 0)
            {
                zeros += 64;
            }

            padded.AddRange(new byte[zeros]);

            ulong bitLength = (ulong)length * 8;
            for (int shift = 56; shift >= 0; shift -= 8)
            {
                padded.Add((byte)((bitLength >> shift) & 0xFF));
            }

            uint[] state = SeedStateForPrefix(data);

            for (int offset = 0; offset < padded.Count; offset += 64)
            {
                var words = new List<uint>(64);
                for (int i = 0; i < 16; i++)
                {
                    int p = offset + i * 4;
                    words.Add(((uint)padded[p] << 24) | ((uint)padded[p + 1] << 16)
                              | ((uint)padded[p + 2] << 8) | padded[p + 3]);
                }

                Expand(words);
                uint[] working = Rounds(state, words);

                for (int i = 0; i < 8; i++)
                {
                    state[i] = unchecked(state[i] + working[i]);
                }
            }

            var digest = new byte[32];
            for (int i = 0; i < 8; i++)
            {
                digest[i * 4] = (byte)(state[i] >> 24);
                digest[i * 4 + 1] = (byte)(state[i] >> 16);
                digest[i * 4 + 2] = (byte)(state[i] >> 8);
                digest[i * 4 + 3] = (byte)state[i];
            }

            return digest;
        }

        /// <summary>为前 7 个字节预生成压缩状态与消息字。</summary>
        private static uint[] SeedStateForPrefix(byte[] data)
        {
            var words = new List<uint>(64);
            for (int i = 0; i < 14; i++)
            {
                words.Add(0u);
            }

            words.Add(((uint)data[0] << 16) | ((uint)data[1] << 8) | data[2]);
            words.Add(((uint)data[3] << 24) | ((uint)data[4] << 16) | ((uint)data[5] << 8) | data[6]);

            for (int index = 16; index < 30; index++)
            {
                uint value = SeedSchedule[index - 16];

                if (index == 16)
                {
                    value = unchecked(value + (Sigma1(words[index - 2]) ^ 0x000B92C0));
                }
                else
                {
                    value = unchecked(value + Sigma1(words[index - 2]));
                }

                if (index - 7 >= 14)
                {
                    value = unchecked(value + words[index - 7]);
                }

                if (index - 15 >= 14)
                {
                    value = index == 29
                        ? unchecked(value + (Sigma0(words[index - 15]) ^ 0x059C0B80))
                        : unchecked(value + Sigma0(words[index - 15]));
                }

                words.Add(value & Mask);
            }

            words.Add(unchecked(Sigma1(words[28]) + words[23] + Sigma0(words[15]) + words[14]
                                + 0xC9DC06A7 - RoundConstants[14]) & Mask);

            Expand(words, 31);

            uint[] working = Rounds(SeedState, words, 14);
            var result = new uint[8];
            for (int i = 0; i < 8; i++)
            {
                result[i] = unchecked(InitialState[i] + working[i]);
            }

            return result;
        }

        /// <summary>把摘要与盐值混成两个 32 位校验字。</summary>
        private static (uint First, uint Second) Cs64Digest(byte[] data, byte[] digest)
        {
            uint keyA = BitConverter.ToUInt32(digest, 0) | 1u;
            uint keyB = BitConverter.ToUInt32(digest, 4) | 1u;

            uint factorA = unchecked(keyA * 0x82893933u);
            uint factorB = unchecked(keyB * 0x82893933u);
            uint[] factors = { factorA, factorB };

            var terms = new List<uint>();
            var offsets = new List<uint>();

            for (int i = 0; i < Cs64Coefficients.Length; i++)
            {
                terms.Add(unchecked(Cs64Coefficients[i].Coefficient * factors[i % 2]));
                offsets.Add(Cs64Coefficients[i].Offset);
            }

            var salt = new byte[data.Length + 1];
            Array.Copy(data, 0, salt, 1, data.Length);

            List<uint> saltWords = WordsLittleEndianEven(salt);
            for (int index = 0; index < saltWords.Count; index++)
            {
                uint word = saltWords[index];
                uint term = unchecked(word * (index % 2 == 0 ? keyA : keyB));
                uint offset = unchecked(RotateLeft16(word)
                                        * (index % 2 == 0 ? 0xEF0569FBu : 0xC31713DBu));

                if (index == 0)
                {
                    term = unchecked(term + 0xF2F90B1Au * factors[0]);
                    offset = unchecked(offset + 0x0B1A0000u);
                }

                terms.Add(term);
                offsets.Add(offset);
            }

            uint swapAcc = 0, swapLast = 0, state = 0, acc = 0;

            for (int index = 0; index < terms.Count; index += 2)
            {
                uint x = terms[index];
                uint y = terms[index + 1];

                uint t = RotateLeft16(unchecked(offsets[index] + x));
                uint q = unchecked(t * 0x76F50F34u);
                swapAcc = unchecked(swapAcc + unchecked(RotateLeft16(q) * 0xC3EFEA97u + q));

                t = RotateLeft16(unchecked(offsets[index + 1] + y));
                q = unchecked(t * 0x1881CE3Cu);
                swapLast = unchecked(RotateLeft16(q) * 0x35BD1EC9u + q);
                swapAcc = unchecked(swapAcc + swapLast);

                state = unchecked(state * keyA + x);
                state = unchecked(RotateLeft16(state) * 0xCF98B111u);
                state = unchecked(RotateLeft16(state) * 0x87085B9Fu);
                state = unchecked(RotateLeft16(state) * 0x12CEB96Du);
                state = unchecked(RotateLeft16(state) * 0x257E1D83u);
                acc = unchecked(acc + state);

                t = unchecked(state * keyB + y);
                t = unchecked(RotateLeft16(t) * 0xA27416F5u);
                t = unchecked(RotateLeft16(t) * 0xD38396FFu);
                state = unchecked(RotateLeft16(unchecked(RotateLeft16(t) * 0x7C932B89u)) * 0xBFA49F69u);
                acc = unchecked(acc + state);
            }

            return (swapLast ^ state, swapAcc ^ acc);
        }

        /// <summary>按小端序拆成 32 位字，末尾补零保证个数为偶数。</summary>
        private static List<uint> WordsLittleEndianEven(byte[] data)
        {
            int padding = (4 - (data.Length % 4)) % 4;
            int paddedLength = data.Length + padding;
            var padded = new byte[paddedLength];
            Array.Copy(data, padded, data.Length);

            var words = new List<uint>(paddedLength / 4);
            for (int i = 0; i < paddedLength; i += 4)
            {
                words.Add((uint)(padded[i] | (padded[i + 1] << 8) | (padded[i + 2] << 16) | (padded[i + 3] << 24)));
            }

            if (words.Count % 2 != 0)
            {
                words.Add(0u);
            }

            return words;
        }
    }
}
