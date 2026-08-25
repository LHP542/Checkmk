using System.Text.Json;
using Checkmk.App.Models;
using Checkmk.Data;
using NLog;

namespace Checkmk.App.Services;

/// <summary>Woher der aktuelle Filtersatz stammt.</summary>
public enum FilterOrigin
{
    /// <summary>Kein Datenbankzugang — rein persönlich aus <c>filter.json</c>.</summary>
    Local,

    /// <summary>Aus der zentralen Datenbank, schreibbar.</summary>
    Central,

    /// <summary>Datenbank nicht erreichbar — letzter bekannter Stand, nur lesbar.</summary>
    Cache
}

/// <summary>
/// Host-Filter aus der zentralen Datenbank als <b>Katalog mit Abonnement</b>,
/// mit Ausfall-Cache.
///
/// Der Alltagsgewinn: Ein guter Filter wird einmal gebaut und in den Katalog
/// gestellt; wer ihn braucht, hakt ihn an. Niemand pflegt dafür Mitgliederlisten
/// — genau das war die Schwäche des vorherigen Team-Modells, das deshalb ersetzt
/// wurde.
///
/// <para><b>Bei Ausfall wird nicht geschrieben.</b> Anders als bei den globalen
/// Einstellungen, die nur gelesen werden, sind Filter bearbeitbar — und eine
/// Änderung, die nur im lokalen Cache landet, wäre beim nächsten erfolgreichen
/// Laden lautlos wieder weg. Lieber sagen „gerade nur lesbar" als eine Änderung
/// annehmen, die niemand wiedersieht.</para>
/// </summary>
public sealed class CentralFilterService(
    IFilterStore filters,
    IFachbereichStore fachbereiche,
    string cachePath,
    string userName)
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>Zuletzt geladener Stand — Grundlage für den Diff beim Speichern.</summary>
    private List<HostFilter> _loaded = [];
    private string _site = "";

    public FilterOrigin Origin { get; private set; } = FilterOrigin.Cache;

    /// <summary>Schreiben geht nur gegen die echte Datenbank.</summary>
    public bool CanWrite => Origin == FilterOrigin.Central;

    /// <summary>Alle Fachbereiche — für die Auswahl beim Veröffentlichen.</summary>
    public IReadOnlyList<FachbereichRow> Fachbereiche => fachbereiche.Current.Fachbereiche;

    /// <summary>Darf dieser Anwender Fachbereiche verwalten? Leere Admin-Tabelle
    /// = jeder. <b>Veröffentlichen darf ohnehin jeder</b>, dafür braucht es das
    /// hier nicht.</summary>
    public bool IsAdmin => fachbereiche.Current.IsAdmin(userName);

    public string UserName => userName;

    /// <summary>Kurzfassung für die Statuszeile.</summary>
    public string StatusHint => Origin switch
    {
        FilterOrigin.Central => "Filter: zentral",
        FilterOrigin.Cache => "Filter: Cache (nur lesbar)",
        _ => "Filter: lokal"
    };

    /// <summary>
    /// Lädt die Filter dieser Site: die eigenen plus die abonnierten.
    /// <paramref name="legacy"/> sind die aus <c>filter.json</c>; sie werden
    /// genau einmal übernommen, nämlich wenn dieser Anwender in dieser Site noch
    /// keinen Filter in der Datenbank hat.
    /// </summary>
    public async Task<IReadOnlyList<HostFilter>> LoadAsync(string site,
        IReadOnlyList<HostFilter> legacy, CancellationToken ct = default)
    {
        _site = site;

        try
        {
            await fachbereiche.RefreshAsync(ct).ConfigureAwait(false);

            var imported = await filters.ImportLegacyIfEmptyAsync(site, userName,
                [.. legacy.Select(f => ToShared(f, site))], ct).ConfigureAwait(false);
            if (imported > 0)
                Log.Info("{Count} Filter aus filter.json uebernommen — ab jetzt gilt die Datenbank.",
                    imported);

            var rows = await filters.LoadAsync(site, userName, ct).ConfigureAwait(false);
            _loaded = [.. rows.Select(ToModel)];
            Origin = FilterOrigin.Central;
            WriteCache(site, _loaded);
            return Snapshot();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Zentrale Filter nicht lesbar — greife auf den Cache zurueck.");
            _loaded = ReadCache(site);
            Origin = FilterOrigin.Cache;
            return Snapshot();
        }
    }

    /// <summary>
    /// Der Katalog: alle veröffentlichten Filter dieser Site, samt der Angabe,
    /// welche davon dieser Anwender abonniert hat.
    /// </summary>
    public async Task<(IReadOnlyList<HostFilter> Catalog, IReadOnlyList<int> Subscribed)>
        LoadCatalogAsync(CancellationToken ct = default)
    {
        var rows = await filters.LoadCatalogAsync(_site, ct).ConfigureAwait(false);
        var subs = await filters.LoadSubscriptionsAsync(userName, ct).ConfigureAwait(false);
        return ([.. rows.Select(ToModel)], subs);
    }

    /// <summary>Setzt die Abos auf genau diese Menge und lädt neu.</summary>
    public async Task<string?> SetSubscriptionsAsync(IReadOnlyList<int> filterIds,
        CancellationToken ct = default)
    {
        if (!CanWrite)
            return "Die Datenbank ist nicht erreichbar — Abos sind gerade nicht änderbar.";

        try
        {
            await filters.SetSubscriptionsAsync(userName, filterIds, ct).ConfigureAwait(false);
            var rows = await filters.LoadAsync(_site, userName, ct).ConfigureAwait(false);
            _loaded = [.. rows.Select(ToModel)];
            WriteCache(_site, _loaded);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Abos konnten nicht gespeichert werden.");
            return $"Speichern fehlgeschlagen: {ex.Message}";
        }
    }

    /// <summary>Der zuletzt geladene Stand — für die Liste nach einem Abo-Wechsel.</summary>
    public IReadOnlyList<HostFilter> Current => Snapshot();

    /// <summary>
    /// Schreibt geänderte Filter und löscht <paramref name="deleted"/>.
    ///
    /// <para><b>Gelöscht wird nur, was ausdrücklich genannt ist</b> — nie das,
    /// was gerade nicht in <paramref name="current"/> steht. Diese
    /// Schlussfolgerung war ein datenvernichtender Fehler: Die Collection ist
    /// bei jedem Neuaufbau der Liste kurzzeitig unvollständig, und ein in
    /// diesem Moment ausgelöstes Speichern löschte einen völlig unbeteiligten
    /// Filter aus der Datenbank. Real passiert am 2026-08-25: „XMS"
    /// verschwand in derselben Millisekunde, in der er veröffentlicht
    /// wurde.</para>
    ///
    /// <para><b>Fremde Filter werden nie geschrieben.</b> Ein abonnierter Filter
    /// gehört seinem Autor; er steht zwar in meiner Liste, aber eine Änderung
    /// daran ginge alle Abonnenten an. Deshalb überspringt der Diff alles, wo
    /// ich nicht der Autor bin — der Dialog sperrt die Felder ohnehin schon.</para>
    /// </summary>
    public async Task<string?> PersistAsync(IReadOnlyList<HostFilter> current,
        IReadOnlyList<int>? deleted = null, CancellationToken ct = default)
    {
        if (!CanWrite)
            return "Die Datenbank ist nicht erreichbar — Filter sind gerade nur lesbar.";

        try
        {
            var keep = current.Where(f => !f.IsTransient).ToList();

            // Nur eigene Filter loeschen, und nur die ausdruecklich genannten.
            // Ein abonnierter, den ich aus meiner Liste nehme, wird abbestellt —
            // nicht geloescht.
            foreach (var id in deleted ?? [])
            {
                var mine = _loaded.FirstOrDefault(l => l.Id == id);
                if (mine is null || !mine.IsAuthor(userName)) continue;
                await filters.DeleteAsync(id, ct).ConfigureAwait(false);
            }

            foreach (var f in keep.Where(f => f.IsAuthor(userName)))
            {
                var before = _loaded.FirstOrDefault(l => l.Id == f.Id && f.Id > 0);
                if (before is not null && !Differs(before, f)) continue;

                var id = await filters.SaveAsync(ToShared(f, _site), userName, ct)
                    .ConfigureAwait(false);
                f.Id = id;
            }

            // Den Ausgangsstand fortschreiben statt ersetzen. Ihn durch `keep`
            // zu ersetzen hatte denselben Fehler wie das Löschen per Diff: Ist
            // die Liste gerade unvollständig, gälte ein bestehender Filter
            // danach als unbekannt — und würde beim nächsten Speichern als
            // neuer Datensatz ein zweites Mal angelegt.
            foreach (var id in deleted ?? [])
                _loaded.RemoveAll(l => l.Id == id);

            foreach (var f in keep)
            {
                var i = _loaded.FindIndex(l => l.Id == f.Id && f.Id > 0);
                if (i >= 0) _loaded[i] = Clone(f);
                else _loaded.Add(Clone(f));
            }

            WriteCache(_site, _loaded);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Filter konnten nicht zentral gespeichert werden.");
            return $"Speichern fehlgeschlagen: {ex.Message}";
        }
    }

    private IReadOnlyList<HostFilter> Snapshot() => [.. _loaded.Select(Clone)];

    /// <summary>
    /// Der Fachbereichs-Name ist Anzeige, kein Datenbestand — er kommt bei jedem
    /// Klonen frisch aus dem Store. Sonst zeigt ein umbenannter Fachbereich im
    /// Filter-Manager weiter den alten Namen.
    /// </summary>
    private HostFilter Clone(HostFilter f) => new()
    {
        Id = f.Id,
        FachbereichId = f.FachbereichId,
        FachbereichName = fachbereiche.Current.NameOf(f.FachbereichId),
        Owner = f.Owner,
        Subscribers = f.Subscribers,
        Name = f.Name,
        HostNameRegex = f.HostNameRegex,
        ExplicitHosts = [.. f.ExplicitHosts]
    };

    private HostFilter ToModel(SharedFilter s) => new()
    {
        Id = s.HostFilterId,
        FachbereichId = s.FachbereichId,
        FachbereichName = fachbereiche.Current.NameOf(s.FachbereichId),
        Owner = s.OwnerUserName,
        Subscribers = s.Subscribers,
        Name = s.Name,
        HostNameRegex = s.HostNameRegex,
        ExplicitHosts = [.. s.Hosts]
    };

    private SharedFilter ToShared(HostFilter f, string site) => new(
        f.Id, f.FachbereichId,
        string.IsNullOrEmpty(f.Owner) ? userName : f.Owner,
        site,
        string.IsNullOrWhiteSpace(f.Name) ? "unbenannt" : f.Name,
        f.HostNameRegex, f.ExplicitHosts, f.Subscribers);

    private static bool Differs(HostFilter a, HostFilter b)
        => a.FachbereichId != b.FachbereichId
        || !string.Equals(a.Name, b.Name, StringComparison.Ordinal)
        || !string.Equals(a.HostNameRegex, b.HostNameRegex, StringComparison.Ordinal)
        || !a.ExplicitHosts.OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
              .SequenceEqual(b.ExplicitHosts.OrderBy(h => h, StringComparer.OrdinalIgnoreCase),
                             StringComparer.OrdinalIgnoreCase);

    // --- Ausfall-Cache ---------------------------------------------------

    private sealed class CacheDoc
    {
        public Dictionary<string, List<HostFilter>> Sites { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private void WriteCache(string site, IReadOnlyList<HostFilter> list)
    {
        try
        {
            var doc = ReadCacheDoc();
            doc.Sites[site] = [.. list];
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, JsonSerializer.Serialize(doc, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Filter-Cache konnte nicht geschrieben werden: {Path}", cachePath);
        }
    }

    private List<HostFilter> ReadCache(string site)
        => ReadCacheDoc().Sites.TryGetValue(site, out var l) ? l : [];

    private CacheDoc ReadCacheDoc()
    {
        try
        {
            if (!File.Exists(cachePath)) return new CacheDoc();
            var doc = JsonSerializer.Deserialize<CacheDoc>(File.ReadAllText(cachePath))
                      ?? new CacheDoc();
            doc.Sites = new Dictionary<string, List<HostFilter>>(doc.Sites,
                StringComparer.OrdinalIgnoreCase);
            return doc;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Filter-Cache nicht lesbar: {Path}", cachePath);
            return new CacheDoc();
        }
    }
}
