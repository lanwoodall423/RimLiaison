namespace RimLiaison.RimDev;

internal static class RimDevHumanUi
{
    public static void WriteMenu(TextWriter stdout)
    {
        stdout.WriteLine("RimDev quick menu");
        stdout.WriteLine();
        stdout.WriteLine("Type one of the commands shown in parentheses at the prompt:");
        stdout.WriteLine("  1. Status   - show what is ready and what needs attention   (rimdev status)");
        stdout.WriteLine("  2. Routine  - safely update, test, build, deploy, and push    (rimdev all)");
        stdout.WriteLine("  3. Build    - build projects affected by your changes       (rimdev build)");
        stdout.WriteLine("  4. Test     - run the tests selected for your changes       (rimdev test)");
        stdout.WriteLine("  5. Deploy   - deploy a current validated build              (rimdev deploy)");
        stdout.WriteLine("  6. Push     - push committed work without force-push         (rimdev push)");
        stdout.WriteLine("  7. Merge    - review one approved merge plan                 (rimdev merge)");
        stdout.WriteLine("  8. Help     - show the full beginner guide                   (rimdev help)");
        stdout.WriteLine();
        stdout.WriteLine("Nothing changed. Start with: rimdev status");
    }

    public static void WriteHelp(TextWriter stdout)
    {
        stdout.WriteLine("RimDev help");
        stdout.WriteLine();
        stdout.WriteLine("RimDev safely handles routine work across the RimWorld projects in this workspace.");
        stdout.WriteLine("It leaves local work and uncommitted files alone, never force-pushes, and never guesses through conflicts.");
        stdout.WriteLine();
        stdout.WriteLine("Commands:");
        stdout.WriteLine("  rimdev                  Show a quick beginner menu.");
        stdout.WriteLine("  rimdev status           Show every project and what needs attention. Read-only.");
        stdout.WriteLine("  rimdev all              Do the routine update: safely update, test, build, deploy, and push.");
        stdout.WriteLine("  rimdev sync             Check GitHub for newer work and safely update copies without conflicts.");
        stdout.WriteLine("  rimdev build            Build only projects affected by source changes.");
        stdout.WriteLine("  rimdev test             Run the tests selected by RimLiaison for the changes.");
        stdout.WriteLine("  rimdev deploy           Deploy only a current build with matching validation evidence.");
        stdout.WriteLine("  rimdev push             Push committed work when it can be pushed safely. Never force-pushes.");
        stdout.WriteLine("  rimdev merge            Show an approved pull request and ask before merging. Default is No.");
        stdout.WriteLine("  rimdev help             Show this help.");
        stdout.WriteLine();
        stdout.WriteLine("Useful options:");
        stdout.WriteLine("  --root <folder>         Use a particular workspace folder.");
        stdout.WriteLine("  --yes                   Confirm the exact merge plan non-interactively.");
        stdout.WriteLine("  --json                  Print the versioned machine-readable result.");
        stdout.WriteLine();
        stdout.WriteLine("Normal workflow:");
        stdout.WriteLine("  rimdev status           First, see whether anything needs attention.");
        stdout.WriteLine("  rimdev all              Then do the safe routine update.");
        stdout.WriteLine("  rimdev merge            Finally review and confirm a merge-ready pull request.");
        stdout.WriteLine();
        stdout.WriteLine("Result words: PASS means completed, BLOCKED means RimDev stopped safely, ");
        stdout.WriteLine("SKIPPED means no affected work was found, and FAIL means a check or operation failed.");
        stdout.WriteLine("When attention is required, follow the Next line or ask your development agent.");
    }

    public static RimDevPullRequest? SelectMergeCandidate(
        TextWriter stdout,
        TextReader input,
        IReadOnlyList<RimDevPullRequest> candidates)
    {
        stdout.WriteLine();
        stdout.WriteLine("More than one pull request matches this branch. RimDev will not guess.");
        for (int index = 0; index < candidates.Count; index++)
        {
            RimDevPullRequest candidate = candidates[index];
            stdout.WriteLine($"  {index + 1}. PR #{candidate.Number} - {candidate.Title} ({candidate.HeadBranch} -> {candidate.BaseBranch})");
        }

        stdout.Write("Type the exact PR number to review, or press Enter for No: ");
        stdout.Flush();
        string? answer;
        try
        {
            answer = input.ReadLine();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            answer = null;
        }

        if (!int.TryParse(answer?.Trim(), out int number))
        {
            stdout.WriteLine("No merge was selected.");
            return null;
        }

        RimDevPullRequest? selected = candidates.FirstOrDefault(candidate => candidate.Number == number);
        if (selected is null)
        {
            stdout.WriteLine("That PR number was not one of the choices. No merge was selected.");
        }

        return selected;
    }

    public static void WriteMergePlan(TextWriter stdout, RimDevRepository repository, RimDevPullRequest pullRequest)
    {
        stdout.WriteLine();
        stdout.WriteLine("Merge plan");
        stdout.WriteLine("Repository: " + repository.Name);
        stdout.WriteLine("PR: #" + pullRequest.Number);
        stdout.WriteLine("From: " + pullRequest.HeadBranch);
        stdout.WriteLine("Into: " + pullRequest.BaseBranch);
        stdout.WriteLine("Checks: PASS");
    }

    public static bool ConfirmMerge(
        TextWriter stdout,
        TextReader input,
        RimDevRepository repository,
        RimDevPullRequest pullRequest)
    {
        WriteMergePlan(stdout, repository, pullRequest);
        stdout.Write("Merge this work into " + pullRequest.BaseBranch + "? [y/N] ");
        stdout.Flush();

        string? answer;
        try
        {
            answer = input.ReadLine();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            answer = null;
        }

        stdout.WriteLine();
        return string.Equals(answer?.Trim(), "y", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
    }
}
