namespace SonNuage.UI.Services;

/// <summary>
/// Listes chargées par pages (« Afficher plus ») : un élément n'y figure qu'une fois, même si deux chargements se
/// croisent ou si la liste a bougé côté serveur entre deux pages. Blazor plante sur une clé (<c>@key</c>) en double.
/// </summary>
public static class Paging
{
    /// <summary>Ajoute à la fin les éléments de la page qui ne sont pas déjà dans la liste.</summary>
    public static void AppendNew<T, TKey>(List<T> list, IEnumerable<T> page, Func<T, TKey> key) where TKey : notnull
    {
        var seen = list.Select(key).ToHashSet();
        foreach (var item in page)
            if (seen.Add(key(item)))
                list.Add(item);
    }
}

/// <summary>
/// Numéro du chargement en cours : un rechargement (filtre, tri) rend caduc le chargement précédent, dont la réponse
/// arrivée en retard est alors ignorée.
/// </summary>
public sealed class LoadGeneration
{
    private int _current;

    public int Start() => ++_current;

    public bool IsCurrent(int generation) => generation == _current;
}
