using System.Text.Json;
using RimLiaison.DevBridge;

namespace RimLiaison.Recovery;

public static class DevBridgeIdentityMismatchPolicy
{
    public const string MismatchCode = "READINESS_IDENTITY_MISMATCH";
    public const int MaximumAttempts = 3;

    public static bool IsIdentityMismatch(DevBridgeAdapterStatus status) =>
        string.Equals(status.ErrorCode, MismatchCode, StringComparison.Ordinal) ||
        status.IdentityMismatch is not null;

    public static bool ShouldRecover(DevBridgeAdapterStatus status)
    {
        DevBridgeIdentityMismatch? mismatch = status.IdentityMismatch;
        if (mismatch is null || !mismatch.Recoverable)
        {
            return false;
        }

        if (mismatch.Classification is
                DevBridgeIdentityMismatchClassifications.InstallationRootOwner or
                DevBridgeIdentityMismatchClassifications.ProtocolSchema or
                DevBridgeIdentityMismatchClassifications.Unknown)
        {
            return false;
        }

        if (mismatch.Classification ==
                DevBridgeIdentityMismatchClassifications.CoordinatorIdentity &&
            string.IsNullOrWhiteSpace(mismatch.ActualRoot))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(mismatch.AuthoritativeRoot) ||
            string.IsNullOrWhiteSpace(mismatch.ActualRoot) ||
            PathsMatch(mismatch.AuthoritativeRoot, mismatch.ActualRoot);
    }
    public static DevBridgeAdapterStatus Refuse(
        DevBridgeAdapterStatus status,
        string action = "refuse-unsafe-identity-recovery") =>
        status with
        {
            RecoveryState = PrerequisiteRecoveryState.RecoveryFailed,
            RecoveryAction = action
        };

    private static bool PathsMatch(string expected, string actual)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(expected),
                Path.GetFullPath(actual),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException)
        {
            return string.Equals(expected.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);
        }

    }
}

public static class DevBridgeIdentityMismatchParser
{
    public static DevBridgeIdentityMismatch? Parse(
        JsonElement root,
        string authoritativeRoot,
        string? errorCode = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        JsonElement? source = FindObject(root, "identityMismatch");
        if (source is null &&
            root.TryGetProperty("failure", out JsonElement failure) &&
            failure.ValueKind == JsonValueKind.Object)
        {
            source = FindObject(failure, "identityMismatch");
        }

        string? field = source is { } mismatch
            ? FirstString(mismatch, "field", "property", "name", "mismatchField")
            : FirstString(root, "identityField", "mismatchField");
        string? expected = source is { } expectedSource
            ? FirstString(expectedSource, "expected", "expectedValue")
            : FirstString(root, "expected", "expectedValue");
        string? actual = source is { } actualSource
            ? FirstString(actualSource, "actual", "actualValue", "responding")
            : FirstString(root, "actual", "actualValue", "responding");
        string? categoryHint = source is { } categorySource
            ? FirstString(categorySource, "classification", "category", "kind")
            : FirstString(root, "classification", "category", "kind");
        string? actualRoot = FirstString(
            source,
            "actualRoot",
            "respondingRoot",
            "installationRoot",
            "devBridgeRoot");
        actualRoot ??= FirstString(
            root,
            "actualRoot",
            "respondingRoot",
            "installationRoot",
            "devBridgeRoot");
        string? expectedRoot = FirstString(
            source,
            "expectedRoot",
            "authoritativeRoot");
        expectedRoot ??= FirstString(root, "expectedRoot", "authoritativeRoot");
        expectedRoot ??= authoritativeRoot;

        string text = string.Join(
            " ",
            errorCode,
            field,
            categoryHint,
            FirstString(source, "message", "reason"),
            FirstString(root, "error", "message"));
        string classification = Classify(text);
        if (classification == DevBridgeIdentityMismatchClassifications.Unknown &&
            source is null &&
            string.IsNullOrWhiteSpace(field) &&
            string.IsNullOrWhiteSpace(expected) &&
            string.IsNullOrWhiteSpace(actual) &&
            !string.Equals(errorCode, DevBridgeIdentityMismatchPolicy.MismatchCode,
                StringComparison.Ordinal))
        {
            return null;
        }

        field ??= FieldFor(classification);
        bool? reportedRecoverable = source is { } reportedSource
            ? FirstBoolean(reportedSource, "recoverable", "transient")
            : FirstBoolean(root, "recoverable", "transient");
        bool sameRoot = string.IsNullOrWhiteSpace(actualRoot) ||
            PathsEqual(expectedRoot, actualRoot);
        bool recoverable = classification switch
        {
            DevBridgeIdentityMismatchClassifications.RuntimeGeneration =>
                reportedRecoverable is not false,
            DevBridgeIdentityMismatchClassifications.RimWorldProcessIdentity =>
                reportedRecoverable is not false,
            DevBridgeIdentityMismatchClassifications.CoordinatorIdentity =>
                sameRoot &&
                !string.IsNullOrWhiteSpace(actualRoot) &&
                reportedRecoverable == true,
            DevBridgeIdentityMismatchClassifications.StaleDescriptorProfileRegistration =>
                sameRoot && reportedRecoverable is not false,
            _ => false
        };

        if (!sameRoot && !string.IsNullOrWhiteSpace(actualRoot))
        {
            classification = DevBridgeIdentityMismatchClassifications.InstallationRootOwner;
            recoverable = false;
            field = "installationRoot";
        }

        return new DevBridgeIdentityMismatch(
            field!,
            expected ?? expectedRoot,
            actual ?? actualRoot,
            classification,
            recoverable,
            expectedRoot,
            actualRoot,
            "configured --devbridge-root");
    }

    private static string Classify(string text)
    {
        string value = text.ToLowerInvariant();
        if (value.Contains("root") || value.Contains("install") || value.Contains("owner"))
        {
            return DevBridgeIdentityMismatchClassifications.InstallationRootOwner;
        }

        if (value.Contains("schema") || value.Contains("protocol"))
        {
            return DevBridgeIdentityMismatchClassifications.ProtocolSchema;
        }

        if (value.Contains("generation") || value.Contains("profile generation"))
        {
            return DevBridgeIdentityMismatchClassifications.RuntimeGeneration;
        }

        if (value.Contains("rimworld") || value.Contains("process") || value.Contains("pid"))
        {
            return DevBridgeIdentityMismatchClassifications.RimWorldProcessIdentity;
        }

        if (value.Contains("descriptor") || value.Contains("profile") || value.Contains("stale"))
        {
            return DevBridgeIdentityMismatchClassifications.StaleDescriptorProfileRegistration;
        }

        if (value.Contains("coordinator") || value.Contains("registration"))
        {
            return DevBridgeIdentityMismatchClassifications.CoordinatorIdentity;
        }

        return DevBridgeIdentityMismatchClassifications.Unknown;
    }

    private static string FieldFor(string classification) => classification switch
    {
        DevBridgeIdentityMismatchClassifications.InstallationRootOwner => "installationRoot",
        DevBridgeIdentityMismatchClassifications.CoordinatorIdentity => "coordinatorIdentity",
        DevBridgeIdentityMismatchClassifications.RuntimeGeneration => "runtimeGeneration",
        DevBridgeIdentityMismatchClassifications.RimWorldProcessIdentity => "rimWorldProcessIdentity",
        DevBridgeIdentityMismatchClassifications.StaleDescriptorProfileRegistration => "descriptor",
        DevBridgeIdentityMismatchClassifications.ProtocolSchema => "protocol",
        _ => "identity"
    };

    private static JsonElement? FindObject(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : null;

    private static string? FirstString(JsonElement? parent, params string[] names)
    {
        if (parent is not { } value || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return FirstString(value, names);
    }

    private static string? FirstString(JsonElement parent, params string[] names)
    {
        foreach (string name in names)
        {
            if (parent.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static bool? FirstBoolean(JsonElement parent, params string[] names)
    {
        foreach (string name in names)
        {
            if (parent.TryGetProperty(name, out JsonElement value) &&
                value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }
        }

        return null;
    }

    private static bool PathsEqual(string? expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
        {
            return true;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(expected),
                Path.GetFullPath(actual),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException)
        {
            return string.Equals(expected.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
