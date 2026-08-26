using Microsoft.EntityFrameworkCore;
using NLog;

namespace Checkmk.Data;

/// <summary>
/// Ein Filter, wie ihn die Anwendung sieht.
/// </summary>
/// <param name="FachbereichId"><c>null</c> = persönlich, gesetzt = im Katalog
/// veröffentlicht.</param>
/// <param name="OwnerUserName">Der Autor — <b>immer</b> gesetzt, auch bei einem
/// veröffentlichten Filter. Er darf ihn ändern, alle anderen nur abonnieren.</param>
/// <param name="Subscribers">Wie viele ihn abonniert haben. Nur für die Anzeige
/// im Katalog („12 Abonnenten" sagt mehr über einen Filter als jede
/// Beschreibung).</param>
/// <param name="MatchTarget">0 = Hostname (Vorgabe), 1 = Host-Alias. Betrifft
/// nur den Regex — <paramref name="Hosts"/> bleibt eine Liste von Hostnamen.</param>
public sealed record SharedFilter(
    int HostFilterId,
    int? FachbereichId,
    string OwnerUserName,
    string Site,
    string Name,
    string? HostNameRegex,
    IReadOnlyList<string> Hosts,
    int Subscribers = 0,
    byte MatchTarget = 0)
{
    public bool IsPublished => FachbereichId is not null;

    public bool IsAuthor(string user)
        => OwnerUserName.Equals(user, StringComparison.OrdinalIgnoreCase);
}

public interface IFilterStore
{
    /// <summary>
    /// Filter, die dieser Anwender in seiner Auswahl hat: seine eigenen plus
    /// die <b>abonnierten</b> aus dem Katalog.
    ///
    /// <b>Nicht</b> automatisch alles Veröffentlichte — das ist der Kern des
    /// Modells. Was im Katalog steht, sieht man im Katalog; im Dropdown landet
    /// nur, was man selbst dazugenommen hat.
    /// </summary>
    Task<IReadOnlyList<SharedFilter>> LoadAsync(string site, string user,
        CancellationToken ct = default);

    /// <summary>Alle veröffentlichten Filter dieser Site — der Katalog.</summary>
    Task<IReadOnlyList<SharedFilter>> LoadCatalogAsync(string site,
        CancellationToken ct = default);

    /// <summary>Ids der Filter, die dieser Anwender abonniert hat.</summary>
    Task<IReadOnlyList<int>> LoadSubscriptionsAsync(string user, CancellationToken ct = default);

    /// <summary>Setzt die Abos eines Anwenders auf genau diese Menge.</summary>
    Task SetSubscriptionsAsync(string user, IReadOnlyList<int> filterIds,
        CancellationToken ct = default);

    /// <summary>Legt an oder aktualisiert. Gibt die Id zurück.</summary>
    Task<int> SaveAsync(SharedFilter filter, string changedBy, CancellationToken ct = default);

    Task DeleteAsync(int hostFilterId, CancellationToken ct = default);

    /// <summary>
    /// Übernimmt die persönlichen Filter aus <c>filter.json</c> — <b>genau
    /// einmal</b>, nämlich nur wenn dieser Anwender in dieser Site noch keinen
    /// einzigen Filter in der Datenbank hat. Danach ist die Tabelle die
    /// Wahrheit; sonst überschriebe ein Rechner mit altem Dateistand später
    /// zentrale Änderungen.
    /// </summary>
    Task<int> ImportLegacyIfEmptyAsync(string site, string user,
        IReadOnlyList<SharedFilter> fromFile, CancellationToken ct = default);
}

/// <summary>
/// Host-Filter aus der zentralen Datenbank, als <b>Katalog mit Abonnement</b>.
///
/// Der Alltagsgewinn: Ein guter Filter wird einmal gebaut und veröffentlicht;
/// wer ihn braucht, hakt ihn an. Niemand muss dafür Mitgliederlisten pflegen —
/// genau das war die Schwäche des vorherigen Team-Modells.
///
/// <para><b>Geschrieben wird immer einzeln, nie der ganze Satz.</b> Ein
/// Read-Modify-Write über alle Filter würde bei zwei gleichzeitigen Bearbeitern
/// lautlos Einträge verlieren — der Fehler, an dem die geteilte
/// <c>hosts.json</c> gestorben ist.</para>
/// </summary>
public sealed class FilterStore(CockpitDatabase database) : IFilterStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public async Task<IReadOnlyList<SharedFilter>> LoadAsync(string site, string user,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(site)) return [];

        await using var db = database.CreateContext();

        var subscribed = await db.HostFilterSubscriptions.AsNoTracking()
            .Where(s => s.UserName == user)
            .Select(s => s.HostFilterId)
            .ToListAsync(ct).ConfigureAwait(false);

        var rows = await db.HostFilters.AsNoTracking()
            .Where(f => f.Site == site
                     && (f.OwnerUserName == user
                         // Abonniert zaehlt nur, solange der Filter auch
                         // veroeffentlicht ist — ein zurueckgezogener Filter
                         // verschwindet aus fremden Auswahlen.
                         || (f.FachbereichId != null && subscribed.Contains(f.HostFilterId))))
            .OrderBy(f => f.Name)
            .ToListAsync(ct).ConfigureAwait(false);

        return await HydrateAsync(db, rows, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SharedFilter>> LoadCatalogAsync(string site,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(site)) return [];

        await using var db = database.CreateContext();

        var rows = await db.HostFilters.AsNoTracking()
            .Where(f => f.Site == site && f.FachbereichId != null)
            .OrderBy(f => f.Name)
            .ToListAsync(ct).ConfigureAwait(false);

        return await HydrateAsync(db, rows, ct).ConfigureAwait(false);
    }

    /// <summary>Include-Listen und Abonnentenzahl in einem Rutsch nachladen —
    /// statt je Filter eine Abfrage.</summary>
    private static async Task<IReadOnlyList<SharedFilter>> HydrateAsync(
        CockpitDbContext db, List<HostFilterRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];

        var ids = rows.Select(f => f.HostFilterId).ToList();

        var hosts = await db.HostFilterHosts.AsNoTracking()
            .Where(h => ids.Contains(h.HostFilterId))
            .ToListAsync(ct).ConfigureAwait(false);

        var counts = await db.HostFilterSubscriptions.AsNoTracking()
            .Where(s => ids.Contains(s.HostFilterId))
            .GroupBy(s => s.HostFilterId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);

        var byFilter = hosts.GroupBy(h => h.HostFilterId)
            .ToDictionary(g => g.Key,
                          g => (IReadOnlyList<string>)[.. g.Select(x => x.HostName)
                              .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)]);
        var subs = counts.ToDictionary(c => c.Id, c => c.Count);

        return [.. rows.Select(f => new SharedFilter(
            f.HostFilterId, f.FachbereichId, f.OwnerUserName, f.Site, f.Name, f.HostNameRegex,
            byFilter.GetValueOrDefault(f.HostFilterId, []),
            subs.GetValueOrDefault(f.HostFilterId),
            f.MatchTarget))];
    }

    public async Task<IReadOnlyList<int>> LoadSubscriptionsAsync(string user,
        CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        return await db.HostFilterSubscriptions.AsNoTracking()
            .Where(s => s.UserName == user)
            .Select(s => s.HostFilterId)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Setzt die Abos eines Anwenders. Gediffed, nicht gelöscht-und-neu — sonst
    /// ginge bei jedem Speichern der Zeitstempel verloren, und ein
    /// gleichzeitiger Lauf könnte fremde Zeilen treffen.
    /// </summary>
    public async Task SetSubscriptionsAsync(string user, IReadOnlyList<int> filterIds,
        CancellationToken ct = default)
    {
        await using var db = database.CreateContext();

        var existing = await db.HostFilterSubscriptions
            .Where(s => s.UserName == user)
            .ToListAsync(ct).ConfigureAwait(false);

        var wanted = filterIds.Distinct().ToHashSet();

        foreach (var gone in existing.Where(e => !wanted.Contains(e.HostFilterId)))
            db.HostFilterSubscriptions.Remove(gone);

        foreach (var added in wanted.Where(w => !existing.Any(e => e.HostFilterId == w)))
            db.HostFilterSubscriptions.Add(new HostFilterSubscription
            {
                HostFilterId = added,
                UserName = user,
                SubscribedAtUtc = DateTime.UtcNow
            });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        Log.Info("Abos von {User}: {Count} Filter.", user, wanted.Count);
    }

    public async Task<int> SaveAsync(SharedFilter filter, string changedBy,
        CancellationToken ct = default)
    {
        await using var db = database.CreateContext();

        HostFilterRow row;
        if (filter.HostFilterId > 0)
        {
            var found = await db.HostFilters
                .FirstOrDefaultAsync(f => f.HostFilterId == filter.HostFilterId, ct)
                .ConfigureAwait(false);

            // Von jemand anderem geloescht, waehrend der Dialog offen stand:
            // dann neu anlegen statt still nichts zu tun.
            if (found is null) { row = new HostFilterRow(); db.HostFilters.Add(row); }
            else row = found;
        }
        else
        {
            row = new HostFilterRow();
            db.HostFilters.Add(row);
        }

        row.FachbereichId = filter.FachbereichId;
        row.OwnerUserName = filter.OwnerUserName;
        row.Site = filter.Site;
        row.Name = filter.Name.Trim();
        row.HostNameRegex = string.IsNullOrWhiteSpace(filter.HostNameRegex)
            ? null : filter.HostNameRegex.Trim();
        // Unbekannte Werte auf die Vorgabe zurueckholen: die CHECK-Constraint in
        // der Datenbank laesst nur 0 und 1 zu, und ein INSERT, der daran
        // scheitert, waere fuer den Anwender ein nichtssagender SQL-Fehler.
        row.MatchTarget = filter.MatchTarget == 1 ? (byte)1 : (byte)0;
        row.ChangedAtUtc = DateTime.UtcNow;
        row.ChangedBy = changedBy;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await ReplaceHostsAsync(db, row.HostFilterId, filter.Hosts, ct).ConfigureAwait(false);

        Log.Info("Filter gespeichert: '{Name}' ({Scope}, Site {Site}, Autor {Owner}).",
            row.Name, row.FachbereichId is null ? "persoenlich" : $"Katalog {row.FachbereichId}",
            row.Site, row.OwnerUserName);
        return row.HostFilterId;
    }

    /// <summary>
    /// Die Include-Liste wird komplett ersetzt — anders als die Filter selbst.
    /// Sie gehört zu <i>einem</i> Filter und wird immer als Ganzes bearbeitet;
    /// hier gibt es keine zwei Bearbeiter, die sich Einträge wegnehmen könnten.
    /// </summary>
    private static async Task ReplaceHostsAsync(CockpitDbContext db, int filterId,
        IReadOnlyList<string> hosts, CancellationToken ct)
    {
        var existing = await db.HostFilterHosts.Where(h => h.HostFilterId == filterId)
            .ToListAsync(ct).ConfigureAwait(false);
        db.HostFilterHosts.RemoveRange(existing);

        foreach (var host in hosts.Where(h => !string.IsNullOrWhiteSpace(h))
                                  .Select(h => h.Trim())
                                  .Distinct(StringComparer.OrdinalIgnoreCase))
            db.HostFilterHosts.Add(new HostFilterHostRow
            {
                HostFilterId = filterId,
                HostName = host
            });

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Löscht einen Filter. Include-Liste und Abonnements nimmt die Datenbank
    /// per <c>ON DELETE CASCADE</c> mit — sie hier zusätzlich zu entfernen wäre
    /// nicht nur überflüssig, sondern falsch: EF würde DELETEs schicken, die
    /// nach dem Cascade keine Zeile mehr treffen, und das als
    /// Nebenläufigkeitskonflikt melden.
    /// </summary>
    public async Task DeleteAsync(int hostFilterId, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();

        if (await db.HostFilters.FirstOrDefaultAsync(f => f.HostFilterId == hostFilterId, ct)
                .ConfigureAwait(false) is not { } row) return;

        db.HostFilters.Remove(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> ImportLegacyIfEmptyAsync(string site, string user,
        IReadOnlyList<SharedFilter> fromFile, CancellationToken ct = default)
    {
        if (fromFile.Count == 0 || string.IsNullOrWhiteSpace(site)) return 0;

        await using var db = database.CreateContext();

        var any = await db.HostFilters
            .AnyAsync(f => f.Site == site && f.OwnerUserName == user, ct)
            .ConfigureAwait(false);
        if (any) return 0;

        foreach (var f in fromFile)
            await SaveAsync(
                f with { HostFilterId = 0, FachbereichId = null, OwnerUserName = user, Site = site },
                user, ct).ConfigureAwait(false);

        Log.Info("{Count} persoenliche Filter aus filter.json uebernommen (Site {Site}).",
            fromFile.Count, site);
        return fromFile.Count;
    }
}
