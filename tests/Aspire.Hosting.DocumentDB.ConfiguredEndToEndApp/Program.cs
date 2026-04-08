// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting;

namespace Aspire.Hosting.DocumentDB.ConfiguredEndToEndApp;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);
        var assets = TestAssets.Create();

        builder.AddDocumentDB("documentdb")
            .WithLogLevel(DocumentDBLogLevel.Debug)
            .WithInitData(assets.InitDataPath)
            .WithTlsCertificate(assets.CertificatePath, assets.KeyPath)
            .WithTelemetry(enabled: false)
            .WithoutSampleData()
            .WithoutExtendedRum()
            .WithOwner("documentdb")
            .AddDatabase("appdb");

        var app = builder.Build();

        await app.RunAsync();
    }

    private sealed record TestAssets(string RootPath, string InitDataPath, string CertificatePath, string KeyPath)
    {
        public static TestAssets Create()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), nameof(Aspire), nameof(Hosting), "DocumentDBConfiguredEndToEnd", Guid.NewGuid().ToString("N"));
            var initDataPath = Path.Combine(rootPath, "init");
            var certificatePath = Path.Combine(rootPath, "documentdb.pem");
            var keyPath = Path.Combine(rootPath, "documentdb.key");

            Directory.CreateDirectory(initDataPath);
            File.WriteAllText(Path.Combine(initDataPath, "01-seed.js"), """
                db = db.getSiblingDB("appdb");
                db.widgets.insertOne({ _id: "seeded-widget", source: "custom-init" });
                """);

            WriteCertificateFiles(certificatePath, keyPath);

            AppDomain.CurrentDomain.ProcessExit += (_, _) => TryDeleteDirectory(rootPath);

            return new TestAssets(rootPath, initDataPath, certificatePath, keyPath);
        }

        private static void WriteCertificateFiles(string certificatePath, string keyPath)
        {
            using var rsa = RSA.Create(2048);

            var request = new CertificateRequest("CN=Aspire.Hosting.DocumentDB.E2E", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

            var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
            subjectAlternativeNames.AddDnsName("localhost");
            subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
            subjectAlternativeNames.AddIpAddress(IPAddress.IPv6Loopback);
            request.CertificateExtensions.Add(subjectAlternativeNames.Build());

            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));

            File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
            File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        }

        private static void TryDeleteDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to delete temporary directory '{path}': {ex.Message}");
            }
        }
    }
}
