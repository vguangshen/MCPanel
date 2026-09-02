#nullable disable

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MarchCenter.AccountApi
{
    public static class PasswordHasher
    {
        public static bool IsSupported(string algorithm)
        {
            switch ((algorithm ?? "").Trim().ToLowerInvariant())
            {
                case "plain": case "itmc-java-long-md5": case "itmc-des": case "itmc-sandbox-des":
                case "md5-lower": case "md5-upper": case "double-md5-lower":
                case "sha1-lower": case "sha1-upper": case "sha256-lower": case "sha256-upper": return true;
                default: return false;
            }
        }

        public static string HashForSet(string password, SystemProfile profile) { return Hash(password, profile, false); }
        public static string HashForReset(string password, SystemProfile profile) { return Hash(password, profile, true); }

        private static string Hash(string password, SystemProfile profile, bool isReset)
        {
            var algorithm = (profile.PasswordAlgorithm ?? "").Trim().ToLowerInvariant();
            if (algorithm == "static")
            {
                if (!isReset) throw new AccountApiException("该系统仅配置了固定重置哈希，暂不支持设置任意新密码", 409, "random_password_not_supported");
                if (string.IsNullOrWhiteSpace(profile.StaticResetHash)) throw new AccountApiException("未配置 StaticResetHash", 500, "profile_invalid");
                return profile.StaticResetHash.Trim();
            }

            var source = (profile.SaltPrefix ?? "") + password + (profile.SaltSuffix ?? "");
            var bytes = Encoding.UTF8.GetBytes(source);
            switch (algorithm)
            {
                case "plain": return source;
                case "itmc-java-long-md5": return ItmcJavaLongMd5(source);
                case "itmc-des": return ItmcDes(source, "itmcwlyang");
                case "itmc-sandbox-des": return ItmcDes(source, "itmc2010-2011");
                case "md5-lower": return Hex(Md5(bytes), false);
                case "md5-upper": return Hex(Md5(bytes), true);
                case "double-md5-lower": return Hex(Md5(Encoding.UTF8.GetBytes(Hex(Md5(bytes), false))), false);
                case "sha1-lower": return Hex(Sha1(bytes), false);
                case "sha1-upper": return Hex(Sha1(bytes), true);
                case "sha256-lower": return Hex(Sha256(bytes), false);
                case "sha256-upper": return Hex(Sha256(bytes), true);
                default: throw new AccountApiException("不支持的密码算法：" + profile.PasswordAlgorithm, 500, "profile_invalid");
            }
        }

        private static string ItmcJavaLongMd5(string value)
        {
            var digest = Md5(Encoding.UTF8.GetBytes(value));
            var digits = new char[digest.Length];
            for (var index = 0; index < digest.Length; index++) digits[index] = (char)('0' + ((digest[index] >> 4) % 10));
            return long.Parse(new string(digits), NumberStyles.None, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }

        private static string ItmcDes(string value, string vendorKey)
        {
            var md5Hex = Hex(Md5(Encoding.Default.GetBytes(vendorKey)), true);
            var keyAndIv = Encoding.ASCII.GetBytes(md5Hex.Substring(0, 8));
            using (var des = DES.Create())
            {
                des.Key = keyAndIv; des.IV = keyAndIv; des.Mode = CipherMode.CBC; des.Padding = PaddingMode.PKCS7;
                using (var encryptor = des.CreateEncryptor())
                {
                    var bytes = Encoding.Default.GetBytes(value);
                    return Hex(encryptor.TransformFinalBlock(bytes, 0, bytes.Length), true);
                }
            }
        }

        private static byte[] Md5(byte[] bytes) { using (var value = MD5.Create()) return value.ComputeHash(bytes); }
        private static byte[] Sha1(byte[] bytes) { using (var value = SHA1.Create()) return value.ComputeHash(bytes); }
        private static byte[] Sha256(byte[] bytes) { using (var value = SHA256.Create()) return value.ComputeHash(bytes); }
        private static string Hex(byte[] bytes, bool upper)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var item in bytes) builder.Append(item.ToString(upper ? "X2" : "x2", CultureInfo.InvariantCulture));
            return builder.ToString();
        }
    }
}
