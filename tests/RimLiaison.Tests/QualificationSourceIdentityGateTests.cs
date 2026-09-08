using System.Diagnostics;
using RimLiaison.Qualification;

namespace RimLiaison.Tests;

internal static class QualificationSourceIdentityGateTests
{
    public static void DirtySourceStopsBeforeBurnIn()
    {
        using Fixture fixture = new();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".rimdev"));
        File.WriteAllText(Path.Combine(fixture.Root, ".rimdev", "unrelated.json"), "{}");
        QualificationSourceGateResult result = QualificationSourceIdentityGate
            .CaptureAsync(fixture.Root)
            .GetAwaiter()
            .GetResult();
        Assert(!result.Succeeded && result.ErrorCode == "QUALIFICATION_SOURCE_DIRTY",
            "meaningful dirty source was not rejected before burn-in");
    }

    public static void QualificationOutputIsGeneratedIdentityNoise()
    {
        using Fixture fixture = new();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".rimdev", "qualification"));
        File.WriteAllText(Path.Combine(fixture.Root, ".rimdev", "qualification", "latest.json"), "{}");
        QualificationSourceGateResult result = QualificationSourceIdentityGate
            .CaptureAsync(fixture.Root)
            .GetAwaiter()
            .GetResult();
        Assert(result.Succeeded, result.Error ?? "qualification output was treated as source");
    }

    public static void SourceMutationFailsDuringQualification()
    {
        using Fixture fixture = new();
        QualificationSourceGateResult initial = QualificationSourceIdentityGate
            .CaptureAsync(fixture.Root)
            .GetAwaiter()
            .GetResult();
        Assert(initial.Succeeded && initial.Checkpoint is not null, initial.Error ?? "clean source was not accepted");
        File.AppendAllText(Path.Combine(fixture.Root, "Source", "Thing.cs"), " changed");
        QualificationSourceGateResult result = QualificationSourceIdentityGate
            .RecheckAsync(initial.Checkpoint!)
            .GetAwaiter()
            .GetResult();
        Assert(!result.Succeeded && result.ErrorCode == "QUALIFICATION_SOURCE_CHANGED_DURING_RUN",
            "source mutation was not detected after the identity checkpoint");
    }

    public static void CleanImmutableSourceRechecks()
    {
        using Fixture fixture = new();
        QualificationSourceGateResult initial = QualificationSourceIdentityGate
            .CaptureAsync(fixture.Root)
            .GetAwaiter()
            .GetResult();
        Assert(initial.Succeeded && initial.Checkpoint is not null, initial.Error ?? "clean source was not accepted");
        QualificationSourceGateResult result = QualificationSourceIdentityGate
            .RecheckAsync(initial.Checkpoint!)
            .GetAwaiter()
            .GetResult();
        Assert(result.Succeeded, result.Error ?? "unchanged source identity did not recheck");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rimliaison-qualification-gate-" + Guid.NewGuid().ToString("N"));

        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "Source"));
            File.WriteAllText(Path.Combine(Root, "Source", "Thing.cs"), "public sealed class Thing {}");
            RunGit("init");
            RunGit("add", ".");
            RunGit("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "fixture");
        }

        private void RunGit(params string[] arguments)
        {
            ProcessStartInfo start = new()
            {
                FileName = "git",
                WorkingDirectory = Root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Git fixture command.");
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root))
                return;
            foreach (string file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            foreach (string directory in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(directory, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
    }
}
