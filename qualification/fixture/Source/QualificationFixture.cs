namespace RimLiaison.QualificationFixture;

/// <summary>Deliberately small source surface for deterministic build and deployment checks.</summary>
public static class QualificationFixture
{
    public const string Contract = "rimliaison.qualification.fixture/v1";

    public static string Describe() => Contract;
}
