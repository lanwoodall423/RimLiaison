using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace RimLiaison.DevBridge;

internal static class AssemblyMetadataIdentity
{
    internal static bool TryReadModuleMvid(string path, out string? mvid)
    {
        mvid = null;
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using PEReader reader = new(stream, PEStreamOptions.LeaveOpen);
            if (!reader.HasMetadata)
                return false;

            MetadataReader metadata = reader.GetMetadataReader();
            ModuleDefinition module = metadata.GetModuleDefinition();
            if (module.Mvid.IsNil)
                return false;

            mvid = metadata.GetGuid(module.Mvid).ToString("D");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            BadImageFormatException or InvalidDataException or ArgumentException)
        {
            return false;
        }
    }
}
