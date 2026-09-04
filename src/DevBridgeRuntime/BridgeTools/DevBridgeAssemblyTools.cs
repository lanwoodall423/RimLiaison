using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using RimBridgeServer.Sdk;

namespace DevBridge2.BridgeTools;

public sealed class DevBridgeAssemblyTools
{
    private const int MaximumReportedMatches = 4;

    [Tool("devbridge/verify_loaded_assembly",
        Title = "Verify one loaded assembly",
        Description = "Verify one authenticated target assembly and its backing-file SHA-256 without enumerating unrelated assemblies.",
        ResultDescription = "A bounded exact loaded-assembly identity and hash result.",
        RequiresAuth = true)]
    [ToolResponse("success", "boolean", "Whether exactly one target assembly was loaded and its hash matched")]
    [ToolResponse("loaded", "boolean", "Whether at least one target assembly was loaded")]
    [ToolResponse("uniqueMatch", "boolean", "Whether exactly one target assembly matched")]
    [ToolResponse("assemblyName", "string", "Canonical requested or loaded assembly simple name")]
    [ToolResponse("assemblyFullName", "string", "Loaded assembly full identity", Nullable = true)]
    [ToolResponse("location", "string", "Loaded assembly backing-file path", Nullable = true)]
    [ToolResponse("fileName", "string", "Loaded assembly backing-file name", Nullable = true)]
    [ToolResponse("sha256", "string", "Loaded backing-file SHA-256", Nullable = true)]
    [ToolResponse("expectedSha256", "string", "Expected backing-file SHA-256")]
    [ToolResponse("hashMatches", "boolean", "Whether the loaded backing-file hash matched")]
    [ToolResponse("mvid", "string", "Loaded module MVID", Nullable = true)]
    [ToolResponse("matches", "array", "Bounded identities for duplicate matching assemblies", Nullable = true)]
    [ToolResponse("errorCode", "string", "Bounded verification error code", Nullable = true)]
    [ToolResponse("error", "string", "Bounded verification diagnostic", Nullable = true)]
    public DevBridgeAssemblyVerificationPayload VerifyLoadedAssembly(
        [ToolParameter(Description = "Assembly simple name or file name.", Required = true)] string assemblyName,
        [ToolParameter(Description = "Expected SHA-256 of the loaded assembly backing file.", Required = true)] string expectedSha256)
    {
        string requestedName = CanonicalSimpleName(assemblyName);
        string expectedHash = NormalizeHash(expectedSha256);
        if (string.IsNullOrWhiteSpace(requestedName))
            return Failure("TARGET_ASSEMBLY_NAME_INVALID", "An assembly simple name or file name is required.", expectedHash);
        if (expectedHash == null)
            return Failure("TARGET_ASSEMBLY_HASH_INVALID", "The expected assembly SHA-256 must be 64 hexadecimal characters.", expectedSha256?.Trim());

        Assembly[] loadedAssemblies;
        try
        {
            loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
        }
        catch (Exception exception)
        {
            return Failure("TARGET_ASSEMBLY_ENUMERATION_FAILED", Bound(exception.Message), expectedHash);
        }

        List<DevBridgeLoadedAssemblyIdentity> matches = new();
        int matchingCount = 0;
        foreach (Assembly assembly in loadedAssemblies)
        {
            AssemblyName identity;
            try
            {
                identity = assembly.GetName();
            }
            catch
            {
                continue;
            }
            if (!string.Equals(identity?.Name, requestedName, StringComparison.Ordinal))
                continue;

            matchingCount++;
            if (matches.Count < MaximumReportedMatches)
                matches.Add(Inspect(assembly, identity));
        }

        if (matchingCount == 0)
        {
            return new DevBridgeAssemblyVerificationPayload
            {
                Success = false,
                Loaded = false,
                UniqueMatch = false,
                AssemblyName = requestedName,
                ExpectedSha256 = expectedHash,
                HashMatches = false,
                ErrorCode = "TARGET_ASSEMBLY_NOT_LOADED",
                Error = "No loaded assembly matched the requested simple name."
            };
        }

        if (matchingCount != 1)
        {
            return new DevBridgeAssemblyVerificationPayload
            {
                Success = false,
                Loaded = true,
                UniqueMatch = false,
                AssemblyName = requestedName,
                ExpectedSha256 = expectedHash,
                HashMatches = false,
                Matches = matches,
                ErrorCode = "TARGET_ASSEMBLY_AMBIGUOUS",
                Error = "More than one loaded assembly matched the requested simple name."
            };
        }

        DevBridgeLoadedAssemblyIdentity match = matches[0];
        bool hashMatches = match.Sha256 != null &&
            string.Equals(match.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase);
        return new DevBridgeAssemblyVerificationPayload
        {
            Success = hashMatches,
            Loaded = true,
            UniqueMatch = true,
            AssemblyName = match.AssemblyName,
            AssemblyFullName = match.AssemblyFullName,
            Location = match.Location,
            FileName = match.FileName,
            Sha256 = match.Sha256,
            ExpectedSha256 = expectedHash,
            HashMatches = hashMatches,
            Mvid = match.Mvid,
            ErrorCode = hashMatches ? null :
                match.Sha256 == null ? "TARGET_ASSEMBLY_HASH_UNAVAILABLE" : "TARGET_ASSEMBLY_IDENTITY_MISMATCH",
            Error = hashMatches ? null :
                match.Sha256 == null
                    ? "The matching assembly has no readable backing file for hash verification."
                    : "The loaded assembly backing-file SHA-256 did not match the expected candidate hash."
        };
    }

    private static DevBridgeLoadedAssemblyIdentity Inspect(Assembly assembly, AssemblyName identity)
    {
        string fullName = SafeAssemblyFullName(assembly);
        string location = SafeLocation(assembly);
        string fileName = string.IsNullOrWhiteSpace(location) ? null : Path.GetFileName(location);
        string mvid = SafeMvid(assembly);
        string hash = string.IsNullOrWhiteSpace(location) ? null : HashFile(location);
        return new DevBridgeLoadedAssemblyIdentity
        {
            AssemblyName = identity?.Name,
            AssemblyFullName = fullName,
            Location = location,
            FileName = fileName,
            Sha256 = hash,
            Mvid = mvid
        };
    }

    private static string SafeAssemblyFullName(Assembly assembly)
    {
        try { return assembly.FullName; }
        catch { return null; }
    }

    private static string SafeLocation(Assembly assembly)
    {
        try
        {
            string location = assembly.Location;
            return string.IsNullOrWhiteSpace(location) ? null : location;
        }
        catch { return null; }
    }

    private static string SafeMvid(Assembly assembly)
    {
        try { return assembly.ManifestModule.ModuleVersionId.ToString("D"); }
        catch { return null; }
    }

    private static string HashFile(string location)
    {
        try
        {
            if (!File.Exists(location))
                return null;
            using FileStream stream = File.OpenRead(location);
            using SHA256 sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }
        catch { return null; }
    }

    private static string CanonicalSimpleName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string name = value.Trim();
        try { name = Path.GetFileName(name); }
        catch { }
        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = Path.GetFileNameWithoutExtension(name);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string NormalizeHash(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string hash = value.Trim();
        if (hash.Length != 64)
            return null;
        foreach (char character in hash)
        {
            bool hexadecimal = character >= '0' && character <= '9' ||
                character >= 'a' && character <= 'f' ||
                character >= 'A' && character <= 'F';
            if (!hexadecimal)
                return null;
        }
        return hash.ToLowerInvariant();
    }

    private static DevBridgeAssemblyVerificationPayload Failure(string code, string error, string expectedHash)
    {
        return new DevBridgeAssemblyVerificationPayload
        {
            Success = false,
            Loaded = false,
            UniqueMatch = false,
            AssemblyName = null,
            ExpectedSha256 = expectedHash,
            HashMatches = false,
            ErrorCode = code,
            Error = error
        };
    }

    private static string Bound(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();
        return value.Length <= 512 ? value : value.Substring(0, 512);
    }
}

[DataContract]
public sealed class DevBridgeAssemblyVerificationPayload
{
    [DataMember(Name = "success")] public bool Success { get; set; }
    [DataMember(Name = "loaded")] public bool Loaded { get; set; }
    [DataMember(Name = "uniqueMatch")] public bool UniqueMatch { get; set; }
    [DataMember(Name = "assemblyName")] public string AssemblyName { get; set; }
    [DataMember(Name = "assemblyFullName")] public string AssemblyFullName { get; set; }
    [DataMember(Name = "location")] public string Location { get; set; }
    [DataMember(Name = "fileName")] public string FileName { get; set; }
    [DataMember(Name = "sha256")] public string Sha256 { get; set; }
    [DataMember(Name = "expectedSha256")] public string ExpectedSha256 { get; set; }
    [DataMember(Name = "hashMatches")] public bool HashMatches { get; set; }
    [DataMember(Name = "mvid")] public string Mvid { get; set; }
    [DataMember(Name = "matches")] public List<DevBridgeLoadedAssemblyIdentity> Matches { get; set; }
    [DataMember(Name = "errorCode")] public string ErrorCode { get; set; }
    [DataMember(Name = "error")] public string Error { get; set; }
}

[DataContract]
public sealed class DevBridgeLoadedAssemblyIdentity
{
    [DataMember(Name = "assemblyName")] public string AssemblyName { get; set; }
    [DataMember(Name = "assemblyFullName")] public string AssemblyFullName { get; set; }
    [DataMember(Name = "location")] public string Location { get; set; }
    [DataMember(Name = "fileName")] public string FileName { get; set; }
    [DataMember(Name = "sha256")] public string Sha256 { get; set; }
    [DataMember(Name = "mvid")] public string Mvid { get; set; }
}
