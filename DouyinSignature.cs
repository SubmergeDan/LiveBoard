using System;
using System.Diagnostics;
using System.Text;

namespace LiveBoard
{
    // a_bogus request signing adapted from ihmily/DouyinLiveRecorder (MIT).
    internal static class DouyinSignature
    {
        private const string EnvironmentData = "1920|1080|1920|1040|0|30|0|0|1872|92|1920|1040|1857|92|1|24|Win32";
        private const string UaTable = "ckdp1h4ZKsUB80/Mfvw36XIgR25+WQAlEi7NLboqYTOPuzmFjJnryx9HVGDaStCe";
        private const string ResultTable = "Dkdpgh2ZmsQB80/MfvV36XI1R45-WUAlEixNLwoqYTOPuzKFjJnry79HbGcaStCe";

        static DouyinSignature()
        {
            Debug.Assert(ToHex(Sm3(Encoding.ASCII.GetBytes("abc"))) == "66c7f0f462eeedd9d1f2d46bdc10e4e24167c4875cf2f7a2297da02b8f4ba8e0");
        }

        internal static string Sign(string query, string userAgent)
        {
            var queryHash = Sm3(Sm3(Encoding.UTF8.GetBytes(query + "cus")));
            var suffixHash = Sm3(Sm3(Encoding.ASCII.GetBytes("cus")));
            var uaHash = Sm3(Encoding.ASCII.GetBytes(Encode(Rc4(Encoding.UTF8.GetBytes(userAgent), new byte[] { 0, 1, 14 }), UaTable)));
            var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var finished = started + 100;
            var environment = Encoding.ASCII.GetBytes(EnvironmentData);
            var startBytes = LowIntBytes(started);
            var endBytes = LowIntBytes(finished);
            var body = new byte[44 + environment.Length + 1];
            var pageId = IntBytes(110624);

            var checksum = (byte)(44 ^ startBytes[0] ^ 0 ^ 0 ^ queryHash[21] ^ suffixHash[21] ^ uaHash[23] ^
                startBytes[1] ^ 0 ^ 1 ^ 0 ^ queryHash[22] ^ suffixHash[22] ^ uaHash[24] ^
                startBytes[2] ^ 0 ^ 0 ^ 0 ^ startBytes[3] ^ 0 ^ 0 ^ 14 ^
                endBytes[0] ^ endBytes[1] ^ endBytes[2] ^ endBytes[3] ^ 3 ^
                (byte)(finished >> 32) ^ (byte)(finished >> 40) ^ (byte)(started >> 32) ^ (byte)(started >> 40) ^
                pageId[0] ^ pageId[1] ^ pageId[2] ^ pageId[3] ^ 0xEF ^ 0x18 ^ 0 ^ 0 ^
                (byte)environment.Length ^ (byte)(environment.Length >> 8));

            var fixedBody = new byte[]
            {
                44, startBytes[0], pageId[0], 0, 0, 0, 0x18, queryHash[21], suffixHash[21], pageId[1], uaHash[23],
                startBytes[1], 0, pageId[2], pageId[3], 1, 0, 0xEF, queryHash[22], suffixHash[22], uaHash[24],
                startBytes[2], 0, 0, 0, 0, startBytes[3], 0, 0, 14, endBytes[0], endBytes[1], 0, endBytes[2],
                endBytes[3], 3, (byte)(finished >> 32), (byte)(finished >> 40), (byte)(started >> 32),
                (byte)(started >> 40), (byte)environment.Length, (byte)(environment.Length >> 8), 0, 0
            };
            Buffer.BlockCopy(fixedBody, 0, body, 0, fixedBody.Length);
            Buffer.BlockCopy(environment, 0, body, fixedBody.Length, environment.Length);
            body[body.Length - 1] = checksum;

            var prefix = RandomPrefix();
            var encrypted = Rc4(body, new byte[] { 121 });
            var result = new byte[prefix.Length + encrypted.Length];
            Buffer.BlockCopy(prefix, 0, result, 0, prefix.Length);
            Buffer.BlockCopy(encrypted, 0, result, prefix.Length, encrypted.Length);
            return Encode(result, ResultTable) + "=";
        }

        private static byte[] RandomPrefix()
        {
            var values = new[] { 1234, 9876, 5555 };
            var options = new[] { new[] { 3, 45 }, new[] { 1, 0 }, new[] { 1, 5 } };
            var result = new byte[12];
            for (var index = 0; index < values.Length; index++)
            {
                var low = values[index] & 255;
                var high = values[index] >> 8 & 255;
                result[index * 4] = (byte)((low & 170) | (options[index][0] & 85));
                result[index * 4 + 1] = (byte)((low & 85) | (options[index][0] & 170));
                result[index * 4 + 2] = (byte)((high & 170) | (options[index][1] & 85));
                result[index * 4 + 3] = (byte)((high & 85) | (options[index][1] & 170));
            }
            return result;
        }

        private static string Encode(byte[] input, string table)
        {
            var totalCharacters = (input.Length * 4 + 2) / 3;
            var builder = new StringBuilder(totalCharacters);
            for (var offset = 0; offset < input.Length; offset += 3)
            {
                var value = input[offset] << 16;
                if (offset + 1 < input.Length) value |= input[offset + 1] << 8;
                if (offset + 2 < input.Length) value |= input[offset + 2];
                var indexes = new[] { value >> 18 & 63, value >> 12 & 63, value >> 6 & 63, value & 63 };
                for (var index = 0; index < indexes.Length && builder.Length < totalCharacters; index++)
                    builder.Append(table[indexes[index]]);
            }
            return builder.ToString();
        }

        private static byte[] Rc4(byte[] input, byte[] key)
        {
            var state = new byte[256];
            for (var index = 0; index < state.Length; index++) state[index] = (byte)index;
            var j = 0;
            for (var index = 0; index < state.Length; index++)
            {
                j = (j + state[index] + key[index % key.Length]) & 255;
                var swap = state[index]; state[index] = state[j]; state[j] = swap;
            }
            var result = new byte[input.Length];
            var i = 0;
            j = 0;
            for (var index = 0; index < input.Length; index++)
            {
                i = i + 1 & 255;
                j = (j + state[i]) & 255;
                var swap = state[i]; state[i] = state[j]; state[j] = swap;
                result[index] = (byte)(input[index] ^ state[(state[i] + state[j]) & 255]);
            }
            return result;
        }

        private static byte[] Sm3(byte[] input)
        {
            var size = (input.Length + 9 + 63) / 64 * 64;
            var padded = new byte[size];
            Buffer.BlockCopy(input, 0, padded, 0, input.Length);
            padded[input.Length] = 0x80;
            var bitLength = (ulong)input.Length * 8;
            for (var index = 0; index < 8; index++) padded[size - 1 - index] = (byte)(bitLength >> index * 8);

            var registers = new uint[] { 1937774191, 1226093241, 388252375, 3666478592, 2842636476, 372324522, 3817729613, 2969243214 };
            unchecked
            {
                for (var offset = 0; offset < padded.Length; offset += 64)
                {
                    var words = new uint[68];
                    var expanded = new uint[64];
                    for (var index = 0; index < 16; index++)
                    {
                        var position = offset + index * 4;
                        words[index] = (uint)(padded[position] << 24 | padded[position + 1] << 16 | padded[position + 2] << 8 | padded[position + 3]);
                    }
                    for (var index = 16; index < 68; index++)
                    {
                        var value = words[index - 16] ^ words[index - 9] ^ Rotate(words[index - 3], 15);
                        words[index] = value ^ Rotate(value, 15) ^ Rotate(value, 23) ^ Rotate(words[index - 13], 7) ^ words[index - 6];
                    }
                    for (var index = 0; index < 64; index++) expanded[index] = words[index] ^ words[index + 4];

                    var a = registers[0]; var b = registers[1]; var c = registers[2]; var d = registers[3];
                    var e = registers[4]; var f = registers[5]; var g = registers[6]; var h = registers[7];
                    for (var index = 0; index < 64; index++)
                    {
                        var constant = index < 16 ? 0x79CC4519u : 0x7A879D8Au;
                        var ss1 = Rotate(Rotate(a, 12) + e + Rotate(constant, index), 7);
                        var ss2 = ss1 ^ Rotate(a, 12);
                        var ff = index < 16 ? a ^ b ^ c : (a & b) | (a & c) | (b & c);
                        var gg = index < 16 ? e ^ f ^ g : (e & f) | (~e & g);
                        var tt1 = ff + d + ss2 + expanded[index];
                        var tt2 = gg + h + ss1 + words[index];
                        d = c; c = Rotate(b, 9); b = a; a = tt1;
                        h = g; g = Rotate(f, 19); f = e; e = tt2 ^ Rotate(tt2, 9) ^ Rotate(tt2, 17);
                    }
                    registers[0] ^= a; registers[1] ^= b; registers[2] ^= c; registers[3] ^= d;
                    registers[4] ^= e; registers[5] ^= f; registers[6] ^= g; registers[7] ^= h;
                }
            }
            var output = new byte[32];
            for (var index = 0; index < registers.Length; index++)
            {
                output[index * 4] = (byte)(registers[index] >> 24);
                output[index * 4 + 1] = (byte)(registers[index] >> 16);
                output[index * 4 + 2] = (byte)(registers[index] >> 8);
                output[index * 4 + 3] = (byte)registers[index];
            }
            return output;
        }

        private static uint Rotate(uint value, int bits)
        {
            bits &= 31;
            return value << bits | value >> (32 - bits & 31);
        }

        private static byte[] LowIntBytes(long value)
        {
            return IntBytes((int)value);
        }

        private static byte[] IntBytes(int value)
        {
            return new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes) builder.Append(value.ToString("x2"));
            return builder.ToString();
        }
    }
}
