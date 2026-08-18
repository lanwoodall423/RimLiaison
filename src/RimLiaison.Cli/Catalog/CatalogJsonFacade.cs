namespace RimLiaison.Catalog;

public static class CatalogJsonFacade
{
    public static string Serialize(object value)
    {
        return CatalogJson.Serialize(value);
    }
}
