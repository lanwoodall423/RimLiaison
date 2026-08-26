using System.Windows.Forms;
using RimLiaison.Observability;

namespace RimLiaison.Desktop;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new ObservabilityMainForm());
            return 0;
        }
        catch (Exception exception)
        {
            string diagnosticLocation;
            bool diagnosticWritten;
            try
            {
                diagnosticLocation = AgentObservabilityStorage.WriteStartupDiagnostic(exception);
                diagnosticWritten = true;
            }
            catch
            {
                diagnosticLocation = AgentObservabilityStorage.ResolveDiagnosticRoot();
                diagnosticWritten = false;
            }

            try
            {
                MessageBox.Show(
                    "The RimLiaison Observability UI could not start.\r\n\r\n" +
                    (diagnosticWritten
                        ? "A bounded diagnostic was written to:\r\n"
                        : "The bounded diagnostic could not be written. Expected location:\r\n") +
                    diagnosticLocation,
                    "RimLiaison Observability UI",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            catch
            {
                // The process still returns a failure code when WinForms cannot
                // display the fallback notification.
            }

            return 1;
        }
    }
}
