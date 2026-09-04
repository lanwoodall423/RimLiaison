using System.Security.Cryptography;
using System.Text.Json;
using RimLiaison.DevBridge;

namespace RimLiaison.Tests;

internal static class DeploymentIdentityTests
{
    public static void PackageHashMatchesCandidateIdentityDomain()
    {
        InternalDevelopmentTransactionService.PackageEntry[] deployed =
        [
            new("source.dll", "1.6/Assemblies/Fixture.dll", new string('a', 64), 17),
            new("about.xml", "About/About.xml", new string('b', 64), 23)
        ];
        ReadyToTestCandidatePackageEntry[] candidate = deployed
            .Select(entry => new ReadyToTestCandidatePackageEntry(
                entry.SourcePath, entry.TargetPath, entry.Sha256, entry.Size))
            .ToArray();

        AssertEqual(
            ReadyToTestCandidateVerifier.ComputePackageHash(candidate),
            InternalDevelopmentTransactionService.ComputePackageHash(deployed),
            "candidate and deployment package identities must use the same path, hash, and size domain");
        InternalDevelopmentTransactionService.PackageEntry[] resized =
        [new("source.dll", "1.6/Assemblies/Fixture.dll", new string('a', 64), 18)];
        Assert(InternalDevelopmentTransactionService.ComputePackageHash(deployed) !=
            InternalDevelopmentTransactionService.ComputePackageHash(resized),
            "package identity must change when an included file size changes");
    }

    public static void OwnedStaleFilesTriggerMirrorAndUnmanagedFilesSurviveCleanup()
    {
        string targetRoot = Path.Combine(Path.GetTempPath(), "rimliaison-deployment-identity-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(targetRoot);
            string stalePath = Path.Combine(targetRoot, "About", "Legacy.xml");
            string currentPath = Path.Combine(targetRoot, "About", "About.xml");
            string unmanagedPath = Path.Combine(targetRoot, "notes.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(stalePath)!);
            File.WriteAllText(stalePath, "legacy");
            File.WriteAllText(currentPath, "current");
            File.WriteAllText(unmanagedPath, "keep");

            string staleHash = HashFile(stalePath);
            string currentHash = HashFile(currentPath);
            InternalDevelopmentTransactionService.PackageEntry[] package =
            [new("source.xml", "About/About.xml", currentHash, new FileInfo(currentPath).Length)];
            string packageHash = InternalDevelopmentTransactionService.ComputePackageHash(package);
            using JsonDocument previous = JsonDocument.Parse($$"""
                {"packageSha256":"{{packageHash}}","files":[
                  {"path":"About/Legacy.xml","sha256":"{{staleHash}}","size":6},
                  {"path":"About/About.xml","sha256":"{{currentHash}}","size":7}
                ]}
                """);

            Assert(InternalDevelopmentTransactionService.NeedsDeployment(
                    targetRoot, package, previous.RootElement, packageHash),
                "an owned file absent from the candidate package must force deployment");
            InternalDevelopmentTransactionService.RemoveStaleOwnedFiles(
                targetRoot, package, previous.RootElement);
            Assert(!File.Exists(stalePath), "owned stale content must be removed from the exact mirror");
            Assert(File.Exists(unmanagedPath), "unmanaged content must not be cleaned");
        }
        finally
        {
            if (Directory.Exists(targetRoot)) Directory.Delete(targetRoot, recursive: true);
        }
    }

    public static void MissingOrModifiedCandidateFilesForceDeployment()
    {
        string targetRoot = Path.Combine(Path.GetTempPath(), "rimliaison-deployment-identity-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(targetRoot, "About"));
            string target = Path.Combine(targetRoot, "About", "About.xml");
            File.WriteAllText(target, "modified");
            string candidateHash = HashText("candidate");
            InternalDevelopmentTransactionService.PackageEntry[] package =
            [new("source.xml", "About/About.xml", candidateHash, 9)];
            string packageHash = InternalDevelopmentTransactionService.ComputePackageHash(package);
            using JsonDocument previous = JsonDocument.Parse($$"""
                {"packageSha256":"{{packageHash}}","files":[
                  {"path":"About/About.xml","sha256":"{{candidateHash}}","size":9}
                ]}
                """);
            Assert(InternalDevelopmentTransactionService.NeedsDeployment(
                    targetRoot, package, previous.RootElement, packageHash),
                "modified candidate content must force deployment");
            File.Delete(target);
            Assert(InternalDevelopmentTransactionService.NeedsDeployment(
                    targetRoot, package, previous.RootElement, packageHash),
                "missing candidate content must force deployment");
        }
        finally
        {
            if (Directory.Exists(targetRoot)) Directory.Delete(targetRoot, recursive: true);
        }
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertEqual(string expected, string actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
    }
}
