using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Onj.SignedUpdater
{
    public static class SigningKeys
    {
        public static RSA CreatePrivateKey()
        {
#if NET48
            return new RSACryptoServiceProvider(3072, new CspParameters(24) { Flags = CspProviderFlags.CreateEphemeralKey }) { PersistKeyInCsp = false };
#else
            var rsa = RSA.Create(); rsa.KeySize = 3072; return rsa;
#endif
        }

        public static string ExportXml(RSA rsa, bool includePrivateKey)
        {
            var p = rsa.ExportParameters(includePrivateKey);
            var root = new XElement("RSAKeyValue",
                new XElement("Modulus", Convert.ToBase64String(p.Modulus)),
                new XElement("Exponent", Convert.ToBase64String(p.Exponent)));
            if (includePrivateKey)
            {
                root.Add(new XElement("P", Convert.ToBase64String(p.P)), new XElement("Q", Convert.ToBase64String(p.Q)),
                    new XElement("DP", Convert.ToBase64String(p.DP)), new XElement("DQ", Convert.ToBase64String(p.DQ)),
                    new XElement("InverseQ", Convert.ToBase64String(p.InverseQ)), new XElement("D", Convert.ToBase64String(p.D)));
            }
            return root.ToString(SaveOptions.DisableFormatting);
        }

        public static RSA ImportXml(string xml, bool privateKey)
        {
            if (string.IsNullOrWhiteSpace(xml) || xml.Length > 16384) throw new InvalidDataException("Invalid signing key.");
            XElement root;
            using (var input = new StringReader(xml))
            using (var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                root = XElement.Load(reader);
            var allowed = privateKey ? new[] { "Modulus", "Exponent", "P", "Q", "DP", "DQ", "InverseQ", "D" } : new[] { "Modulus", "Exponent" };
            if (root.Name != "RSAKeyValue" || root.HasAttributes || root.Elements().Count() != allowed.Length ||
                root.Elements().Any(e => e.HasAttributes || e.HasElements || !allowed.Contains(e.Name.LocalName) || e.Name.NamespaceName.Length != 0))
                throw new InvalidDataException("Only the expected RSA key fields are allowed.");
            Func<string, byte[]> field = name =>
            {
                var matches = root.Elements(name).ToArray();
                if (matches.Length != 1) throw new InvalidDataException("Missing or duplicated RSA key field.");
                return Convert.FromBase64String(matches[0].Value);
            };
            var p = new RSAParameters { Modulus = field("Modulus"), Exponent = field("Exponent") };
            if (p.Modulus.Length < 384 || p.Modulus.Length > 1024) throw new InvalidDataException("Use an RSA key of 3072 to 8192 bits.");
            if (privateKey) { p.P = field("P"); p.Q = field("Q"); p.DP = field("DP"); p.DQ = field("DQ"); p.InverseQ = field("InverseQ"); p.D = field("D"); }
            var rsa = RSA.Create();
            try { rsa.ImportParameters(p); return rsa; }
            catch { rsa.Dispose(); throw; }
        }
    }

    internal static class Wire
    {
        internal const int MaximumManifestBytes = 2 * 1024 * 1024;
        internal const int MaximumSignatureCharacters = 2048;
        internal static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        internal static byte[] Serialize<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return stream.ToArray();
            }
        }

        internal static T Deserialize<T>(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumManifestBytes) throw new InvalidDataException("Metadata size is invalid.");
            using (var reader = JsonReaderWriterFactory.CreateJsonReader(bytes, new XmlDictionaryReaderQuotas
            {
                MaxDepth = 16, MaxStringContentLength = MaximumManifestBytes,
                MaxArrayLength = 20000, MaxNameTableCharCount = MaximumManifestBytes, MaxBytesPerRead = 4096
            }))
                return (T)new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings { MaxItemsInObjectGraph = 100000 }).ReadObject(reader);
        }

        internal static string Hash(Stream stream)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        internal static string HashExact(Stream stream, long expected)
        {
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[65536]; long total = 0; int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total = checked(total + count);
                    if (total > expected) throw new InvalidDataException("ZIP entry exceeded its declared size.");
                    sha.TransformBlock(buffer, 0, count, buffer, 0);
                }
                if (total != expected) throw new InvalidDataException("ZIP entry did not match its declared size.");
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }

        internal static string HashFile(string path) { using (var stream = File.OpenRead(path)) return Hash(stream); }
        internal static bool ValidHash(string hash) { return hash != null && hash.Length == 64 && hash.All(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')); }
        internal static Version Version(string text)
        {
            System.Version version;
            if (!System.Version.TryParse(text, out version) || version.Build < 0) throw new InvalidDataException("Use a numeric version with three or four components.");
            return new System.Version(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision));
        }
    }
}
