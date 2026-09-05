using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Security;using Org.BouncyCastle.X509;
using Org.BouncyCastle.X509.Extension;
using BcX509Certificate = Org.BouncyCastle.X509.X509Certificate;

namespace FlashGithub.Core.Services;

/// <summary>
/// 本地根证书颁发机构：生成或加载 CA，按需为启用的域名动态签发服务器证书（供本地 443 端口的 MITM 使用）。
/// CA 与证书仅存在于本机，只用于拦截 hosts 指向 127.0.0.1 的白名单域名。
/// </summary>
public sealed class CertificateAuthority
{
    private const string CaSubject = "CN=FlashGithub Local CA, O=FlashGithub, OU=Local Acceleration";
    private const string CaFileName = "flashgithub-ca.pfx";
    private const string CaPassword = "flashgithub-local-ca";

    private readonly string _caPath;
    private readonly string _caKeyPath;
    private readonly ConcurrentDictionary<string, X509Certificate2> _leafCache = new(StringComparer.OrdinalIgnoreCase);
    private X509Certificate2? _ca;
    private AsymmetricCipherKeyPair? _caKeyPair;

    public CertificateAuthority(string? configDirectory = null)
    {
        var dir = configDirectory ?? DomainRegistry.AppDataDirectory;
        Directory.CreateDirectory(dir);
        _caPath = Path.Combine(dir, CaFileName);
        _caKeyPath = Path.Combine(dir, "flashgithub-ca.key");
    }

    public string? CaThumbprint => _ca?.Thumbprint;

    public bool IsReady => _ca is not null;

    /// <summary>加载已有的 CA，或首次使用时创建新的自签名 CA。</summary>
    public void EnsureCreated()
    {
        if (_ca is not null) return;

        if (File.Exists(_caPath) && File.Exists(_caKeyPath))
        {
            try
            {
                _ca = X509CertificateLoader.LoadPkcs12FromFile(_caPath, CaPassword);
                LoadCaKeyPair(File.ReadAllBytes(_caKeyPath));
                Log.Info($"已加载本地根证书（指纹 {_ca.Thumbprint[..12]}…）");
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"本地根证书文件损坏，将重新生成：{ex.Message}");
            }
        }

        var (ca, pfxBytes, keyBytes) = CreateCa();
        File.WriteAllBytes(_caPath, pfxBytes);
        File.WriteAllBytes(_caKeyPath, keyBytes);
        _ca = ca;
        LoadCaKeyPair(keyBytes);
        Log.Info("已生成新的本地根证书，请在界面中安装并信任它");
    }

    /// <summary>从 PKCS#8 私钥重建可导出的 RSA 与 BouncyCastle 密钥对（macOS 上 PFX 导入的私钥不可导出，必须走此路径）。</summary>
    private void LoadCaKeyPair(byte[] pkcs8)
    {
        var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(pkcs8, out _);
        _caKeyPair = DotNetUtilities.GetRsaKeyPair(rsa);
    }

    private static (X509Certificate2 Cert, byte[] PfxBytes, byte[] KeyBytes) CreateCa()
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(CaSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        // macOS 上 X509Certificate.Export(Pkcs12) 无法导出私钥，改用 Pkcs12Builder 从 RSA 直接组装
        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000);
        var pfx = new System.Security.Cryptography.Pkcs.Pkcs12Builder();
        var certContents = new System.Security.Cryptography.Pkcs.Pkcs12SafeContents();
        certContents.AddCertificate(cert);
        var keyContents = new System.Security.Cryptography.Pkcs.Pkcs12SafeContents();
        keyContents.AddShroudedKey(rsa, CaPassword, pbe);
        pfx.AddSafeContentsUnencrypted(certContents);
        pfx.AddSafeContentsEncrypted(keyContents, CaPassword, pbe);
        pfx.SealWithMac(CaPassword, HashAlgorithmName.SHA256, 100_000);
        var bytes = pfx.Encode();
        var keyBytes = rsa.ExportPkcs8PrivateKey();
        return (X509CertificateLoader.LoadPkcs12(bytes, CaPassword), bytes, keyBytes);
    }

    /// <summary>Kestrel 的 ServerCertificateSelector 回调：按 SNI 返回对应域名的证书。</summary>
    public X509Certificate2? GetCertificate(string? sni)
    {
        if (string.IsNullOrEmpty(sni) || !IsReady) return null;
        return _leafCache.GetOrAdd(sni.ToLowerInvariant(), IssueLeaf);
    }

    private X509Certificate2 IssueLeaf(string host)
    {
        EnsureCreated();
        var ca = _ca!;

        using var rsa = RSA.Create(2048);
        var rsaKeyPair = DotNetUtilities.GetRsaKeyPair(rsa);

        // 用 CA 证书原始 DER 的 Subject 做颁发者：按字符串重建会颠倒 RDN 顺序，
        // 导致 OpenSSL/Chrome 严格 DER 匹配失败（Safari 宽松，所以此前只有 Chrome 报错）
        var caBcCert = DotNetUtilities.FromX509Certificate(ca);

        var generator = new X509V3CertificateGenerator();
        generator.SetSerialNumber(new Org.BouncyCastle.Math.BigInteger(1, RandomNumberGenerator.GetBytes(16)));
        generator.SetIssuerDN(caBcCert.SubjectDN);
        generator.SetSubjectDN(new X509Name($"CN={host}"));
        generator.SetNotBefore(DateTime.UtcNow.AddMinutes(-5));
        generator.SetNotAfter(DateTime.UtcNow.AddDays(825) < ca.NotAfter
            ? DateTime.UtcNow.AddDays(825)
            : ca.NotAfter.AddMinutes(-1));
        generator.SetPublicKey(rsaKeyPair.Public);

        // SAN：域名本身
        var san = new GeneralNames(new GeneralName(GeneralName.DnsName, host));
        generator.AddExtension(X509Extensions.SubjectAlternativeName, false, san);
        generator.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(false));
        generator.AddExtension(X509Extensions.KeyUsage, true,
            new KeyUsage(KeyUsage.DigitalSignature));
        generator.AddExtension(X509Extensions.ExtendedKeyUsage, true,
            new ExtendedKeyUsage(new[] { KeyPurposeID.id_kp_serverAuth })); // serverAuth
        generator.AddExtension(X509Extensions.SubjectKeyIdentifier, false,
            new SubjectKeyIdentifierStructure(rsaKeyPair.Public));
        generator.AddExtension(X509Extensions.AuthorityKeyIdentifier, false,
            new AuthorityKeyIdentifier(caBcCert.SubjectPublicKeyInfo));

        var caKeyPair = _caKeyPair
            ?? throw new InvalidOperationException("CA 私钥尚未初始化");
        var bcCert = generator.Generate(new Asn1SignatureFactory("SHA256WITHRSA", caKeyPair.Private));

        var leaf = X509CertificateLoader.LoadCertificate(bcCert.GetEncoded());
        return leaf.CopyWithPrivateKey(rsa);
    }

    /// <summary>导出 CA 的 PEM 文本（-----BEGIN CERTIFICATE-----），供系统信任库安装。</summary>
    public string ExportCaPem()
    {
        EnsureCreated();
        var der = _ca!.Export(X509ContentType.Cert);
        var base64 = Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks);
        return $"-----BEGIN CERTIFICATE-----\n{base64}\n-----END CERTIFICATE-----\n";
    }

    /// <summary>导出 CA 证书的 DER 字节。</summary>
    public byte[] ExportCaDer()
    {
        EnsureCreated();
        return _ca!.Export(X509ContentType.Cert);
    }
}
