namespace RimTest;

public static class WorkflowCorrelation
{
    public static string Create() => "rw-" + Guid.NewGuid().ToString("N");
}
