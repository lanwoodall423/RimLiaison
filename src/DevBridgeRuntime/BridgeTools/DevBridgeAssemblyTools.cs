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
        Description = "Verify one authenticated target assembly by exact name, SHA-256, and module MVID.",
        ResultDescription = "A bounded exact loaded-assembly identity, hash, and MVID result.",
        RequiresAuth = true)]
    [ToolResponse("success", "boolean", "Whether exactly one target assembly passed SHA-256 or MVID verification")]
    [ToolResponse("loaded", "boolean", "Whether at least one target assembly was loaded")]
    [ToolResponse("uniqueMatch", "boolean", "Whether exactly one target assembly matched")]
    [ToolResponse("assemblyName", "string", "Canonical requested or loaded assembly simple name")]
    [ToolResponse("assemblyFullName", "string", "Loaded assembly full identity", Nullable = true)]
    [ToolResponse("moduleName", "string", "Loaded manifest module name", Nullable = true)]
    [ToolResponse("moduleFullyQualifiedName", "string", "Loaded manifest module path or name", Nullable = true)]
    [ToolResponse("location", "string", "Loaded assembly backing-file path", Nullable = true)]
    [ToolResponse("fileName", "string", "Loaded assembly backing-file name", Nullable = true)]
    [ToolResponse("sha256", "string", "Loaded backing-file SHA-256", Nullable = true)]
    [ToolResponse("runtimeSha256", "string", "Loaded backing-file SHA-256 when readable", Nullable = true)]
    [ToolResponse("expectedSha256", "string", "Expected backing-file SHA-256")]
    [ToolResponse("hashMatches", "boolean", "Whether the loaded backing-file hash matched")]
    [ToolResponse("mvid", "string", "Loaded module MVID", Nullable = true)]
    [ToolResponse("loadedMvid", "string", "Loaded module MVID", Nullable = true)]
    [ToolResponse("expectedMvid", "string", "Expected MVID from the SHA-verified candidate assembly")]
    [ToolResponse("mvidMatches", "boolean", "Whether the loaded MVID matched")]
    [ToolResponse("backingFileReadable", "boolean", "Whether a readable backing file hash was obtained")]
    [ToolResponse("verificationMethod", "string", "sha256+mvid, mvid, or none")]
    [ToolResponse("matches", "array", "Bounded identities for duplicate matching assemblies", Nullable = true)]
    [ToolResponse("errorCode", "string", "Bounded verification error code", Nullable = true)]
    [ToolResponse("error", "string", "Bounded verification diagnostic", Nullable = true)]
    public DevBridgeAssemblyVerificationPayload VerifyLoadedAssembly(
        [ToolParameter(Description = "Assembly simple name or file name.", Required = true)] string assemblyName,
        [ToolParameter(Description = "Expected SHA-256 of the candidate/deployed assembly file.", Required = true)] string expectedSha256,
        [ToolParameter(Description = "Expected module MVID extracted from that SHA-verified assembly file.", Required = true)] string expectedMvid)
    {
        string requestedName = CanonicalSimpleName(assemblyName);
        string expectedHash = NormalizeHash(expectedSha256);
        string normalizedExpectedMvid = NormalizeMvid(expectedMvid);
        if (string.IsNullOrWhiteSpace(requestedName))
            return Failure("TARGET_ASSEMBLY_NAME_INVALID", "An assembly simple name or file name is required.", expectedHash, normalizedExpectedMvid);
        if (expectedHash == null)
            return Failure("TARGET_ASSEMBLY_HASH_INVALID", "The expected assembly SHA-256 must be 64 hexadecimal characters.", expectedSha256?.Trim(), normalizedExpectedMvid);
        if (normalizedExpectedMvid == null)
            return Failure("TARGET_ASSEMBLY_MVID_INVALID", "The expected assembly MVID must be a GUID.", expectedHash, expectedMvid?.Trim());

        Assembly[] loadedAssemblies;
        try
        {
            loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
        }
        catch (Exception exception)
        {
            return Failure("TARGET_ASSEMBLY_ENUMERATION_FAILED", Bound(exception.Message), expectedHash, normalizedExpectedMvid);
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
                ExpectedMvid = normalizedExpectedMvid,
                HashMatches = false,
                MvidMatches = false,
                VerificationMethod = "none",
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
                ExpectedMvid = normalizedExpectedMvid,
                HashMatches = false,
                MvidMatches = false,
                VerificationMethod = "none",
                Matches = matches,
                ErrorCode = "TARGET_ASSEMBLY_AMBIGUOUS",
                Error = "More than one loaded assembly matched the requested simple name."
            };
        }

        DevBridgeLoadedAssemblyIdentity match = matches[0];
        bool backingFileReadable = match.Sha256 != null;
        bool hashMatches = backingFileReadable &&
            string.Equals(match.Sha256, expectedHash, StringComparison.OrdinalIgnoreCase);
        bool mvidAvailable = match.Mvid != null;
        bool mvidMatches = mvidAvailable &&
            string.Equals(match.Mvid, normalizedExpectedMvid, StringComparison.OrdinalIgnoreCase);
        bool success = backingFileReadable
            ? hashMatches && mvidMatches
            : mvidMatches;
        string verificationMethod = success
            ? backingFileReadable ? "sha256+mvid" : "mvid"
            : "none";
        string? errorCode = success
            ? null
            : backingFileReadable && !hashMatches || mvidAvailable && !mvidMatches
                ? "TARGET_ASSEMBLY_IDENTITY_MISMATCH"
                : "TARGET_ASSEMBLY_EVIDENCE_UNAVAILABLE";
        string? error = success
            ? null
            : errorCode == "TARGET_ASSEMBLY_IDENTITY_MISMATCH"
                ? "The loaded assembly SHA-256 or module MVID did not match the expected candidate identity."
                : "The loaded assembly exposed neither a complete readable hash proof nor a matching usable MVID.";
        return new DevBridgeAssemblyVerificationPayload
        {
            Success = success,
            Loaded = true,
            UniqueMatch = true,
            AssemblyName = match.AssemblyName,
            AssemblyFullName = match.AssemblyFullName,
            ModuleName = match.ModuleName,
            ModuleFullyQualifiedName = match.ModuleFullyQualifiedName,
            Location = match.Location,
            FileName = match.FileName,
            Sha256 = match.Sha256,
            RuntimeSha256 = match.Sha256,
            ExpectedSha256 = expectedHash,
            HashMatches = hashMatches,
            Mvid = match.Mvid,
            LoadedMvid = match.Mvid,
            ExpectedMvid = normalizedExpectedMvid,
            MvidMatches = mvidMatches,
            BackingFileReadable = backingFileReadable,
            VerificationMethod = verificationMethod,
            ErrorCode = errorCode,
            Error = error
        };
    }

    private static DevBridgeLoadedAssemblyIdentity Inspect(Assembly assembly, AssemblyName identity)
    {
        string fullName = SafeAssemblyFullName(assembly);
        string moduleName = SafeModuleName(assembly);
        string moduleFullyQualifiedName = SafeModuleFullyQualifiedName(assembly);
        string location = SafeLocation(assembly);
        string fileName = string.IsNullOrWhiteSpace(location) ? null : Bound(Path.GetFileName(location));
        string mvid = SafeMvid(assembly);
        string hash = string.IsNullOrWhiteSpace(location) ? null : HashFile(location);
        return new DevBridgeLoadedAssemblyIdentity
        {
            AssemblyName = Bound(identity?.Name),
            AssemblyFullName = fullName,
            ModuleName = moduleName,
            ModuleFullyQualifiedName = moduleFullyQualifiedName,
            Location = location,
            FileName = fileName,
            Sha256 = hash,
            Mvid = mvid
        };
    }
    private static string SafeAssemblyFullName(Assembly assembly)
    {
        try { return Bound(assembly.FullName); }
        catch { return null; }
    }

    private static string SafeModuleName(Assembly assembly)
    {
        try { return Bound(assembly.ManifestModule.Name); }
        catch { return null; }
    }

    private static string SafeModuleFullyQualifiedName(Assembly assembly)
    {
        try { return Bound(assembly.ManifestModule.FullyQualifiedName); }
        catch { return null; }
    }

    private static string SafeLocation(Assembly assembly)
    {
        try
        {
            string location = assembly.Location;
            return string.IsNullOrWhiteSpace(location) ? null : Bound(location);
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

    private static string NormalizeMvid(string value)
    {
        return Guid.TryParse(value?.Trim(), out Guid mvid)
            ? mvid.ToString("D")
            : null;
    }

    private static DevBridgeAssemblyVerificationPayload Failure(
        string code, string error, string expectedHash, string expectedMvid)
    {
        return new DevBridgeAssemblyVerificationPayload
        {
            Success = false,
            Loaded = false,
            UniqueMatch = false,
            AssemblyName = null,
            ExpectedSha256 = expectedHash,
            ExpectedMvid = expectedMvid,
            HashMatches = false,
            MvidMatches = false,
            VerificationMethod = "none",
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
    [DataMember(Name = "moduleName")] public string ModuleName { get; set; }
    [DataMember(Name = "moduleFullyQualifiedName")] public string ModuleFullyQualifiedName { get; set; }
    [DataMember(Name = "location")] public string Location { get; set; }
    [DataMember(Name = "fileName")] public string FileName { get; set; }
    [DataMember(Name = "sha256")] public string Sha256 { get; set; }
    [DataMember(Name = "runtimeSha256")] public string RuntimeSha256 { get; set; }
    [DataMember(Name = "expectedSha256")] public string ExpectedSha256 { get; set; }
    [DataMember(Name = "hashMatches")] public bool HashMatches { get; set; }
    [DataMember(Name = "mvid")] public string Mvid { get; set; }
    [DataMember(Name = "loadedMvid")] public string LoadedMvid { get; set; }
    [DataMember(Name = "expectedMvid")] public string ExpectedMvid { get; set; }
    [DataMember(Name = "mvidMatches")] public bool MvidMatches { get; set; }
    [DataMember(Name = "backingFileReadable")] public bool BackingFileReadable { get; set; }
    [DataMember(Name = "verificationMethod")] public string VerificationMethod { get; set; }
    [DataMember(Name = "matches")] public List<DevBridgeLoadedAssemblyIdentity> Matches { get; set; }
    [DataMember(Name = "errorCode")] public string ErrorCode { get; set; }
    [DataMember(Name = "error")] public string Error { get; set; }
}

[DataContract]
public sealed class DevBridgeLoadedAssemblyIdentity
{
    [DataMember(Name = "assemblyName")] public string AssemblyName { get; set; }
    [DataMember(Name = "assemblyFullName")] public string AssemblyFullName { get; set; }
    [DataMember(Name = "moduleName")] public string ModuleName { get; set; }
    [DataMember(Name = "moduleFullyQualifiedName")] public string ModuleFullyQualifiedName { get; set; }
    [DataMember(Name = "location")] public string Location { get; set; }
    [DataMember(Name = "fileName")] public string FileName { get; set; }
    [DataMember(Name = "sha256")] public string Sha256 { get; set; }
    [DataMember(Name = "mvid")] public string Mvid { get; set; }
}
