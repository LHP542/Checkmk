using Microsoft.EntityFrameworkCore;
using NLog;

namespace Checkmk.Data;

/// <summary>Ein Fachbereich, wie ihn die Oberfläche zeigt.</summary>
public sealed record FachbereichRow(int FachbereichId, string Name, string? Description)
{
    public override string ToString() => Name;
}

/// <summary>Momentaufnahme der Fachbereiche. Leere Admin-Liste = jeder ist Admin.</summary>
public sealed record FachbereichSnapshot(
    IReadOnlyList<FachbereichRow> Fachbereiche,
    IReadOnlyList<string> Admins)
{
    public static readonly FachbereichSnapshot Empty = new([], []);

    /// <summary>
    /// Darf dieser Anwender Fachbereiche verwalten und fremde Katalog-Einträge
    /// aufräumen?
    ///
    /// <b>Ist die Admin-Tabelle leer, darf es jeder.</b> Eine leere Tabelle heißt
    /// „noch nicht eingerichtet", und die Alternative wäre eine Funktion, die
    /// ohne einen SQL-Eingriff niemand benutzen kann.
    ///
    /// <b>Veröffentlichen darf ohnehin jeder</b> — dafür braucht es das hier
    /// nicht. Der Katalog ist Organisation, kein Zugriffsschutz.
    /// </summary>
    public bool IsAdmin(string user)
        => Admins.Count == 0
        || Admins.Any(a => a.Equals(user, StringComparison.OrdinalIgnoreCase));

    public FachbereichRow? ById(int id) => Fachbereiche.FirstOrDefault(f => f.FachbereichId == id);

    public string? NameOf(int? id) => id is { } v ? ById(v)?.Name : null;
}

public interface IFachbereichStore
{
    FachbereichSnapshot Current { get; }
    Task RefreshAsync(CancellationToken ct = default);

    Task<int> CreateAsync(string name, string? description, CancellationToken ct = default);
    Task RenameAsync(int id, string name, string? description, CancellationToken ct = default);

    /// <summary>Anzahl veröffentlichter Filter in diesem Fachbereich — für die
    /// Rückfrage vor dem Löschen.</summary>
    Task<int> CountFiltersAsync(int id, CancellationToken ct = default);

    /// <summary>Löscht einen Fachbereich. Die Filter darin werden **nicht**
    /// gelöscht, sondern aus dem Katalog genommen — sie bleiben ihrem Autor als
    /// persönliche Filter erhalten.</summary>
    Task DeleteAsync(int id, CancellationToken ct = default);
}

/// <summary>
/// Fachbereiche und Admin-Zuordnung aus der zentralen Datenbank.
///
/// Wie die übrigen Stores hält er eine Momentaufnahme im Speicher: Die Frage
/// „zu welchem Fachbereich gehört dieser Filter" wird bei jedem Aufbau der
/// Filterliste gestellt, das darf kein Datenbank-Roundtrip sein.
/// </summary>
public sealed class FachbereichStore(CockpitDatabase database) : IFachbereichStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private volatile FachbereichSnapshot _current = FachbereichSnapshot.Empty;

    public FachbereichSnapshot Current => _current;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = database.CreateContext();

            var rows = await db.Fachbereiche.AsNoTracking()
                .OrderBy(f => f.Name)
                .Select(f => new FachbereichRow(f.FachbereichId, f.Name, f.Description))
                .ToListAsync(ct).ConfigureAwait(false);

            // Eigener Versuch: Fehlt die Tabelle, sollen die Fachbereiche
            // trotzdem erscheinen — dann ist eben niemand als Admin eingetragen,
            // was nach der Regel oben heisst: jeder darf.
            var admins = new List<string>();
            try
            {
                admins = await db.AppAdmins.AsNoTracking()
                    .Select(a => a.UserName).ToListAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Admin-Liste nicht lesbar — jeder gilt als Admin.");
            }

            _current = new FachbereichSnapshot(rows, admins);
            Log.Info("Fachbereiche gelesen: {Count}, {Admins} Admins.", rows.Count, admins.Count);
        }
        catch (Exception ex)
        {
            // Alte Momentaufnahme stehen lassen: eine leere Liste saehe aus, als
            // haette jemand alle Fachbereiche geloescht — und wuerde nebenbei
            // jeden zum Admin machen.
            Log.Warn(ex, "Fachbereiche konnten nicht gelesen werden — behalte den vorherigen Stand.");
        }
    }

    public async Task<int> CreateAsync(string name, string? description,
        CancellationToken ct = default)
    {
        await using var db = database.CreateContext();

        var row = new Fachbereich
        {
            Name = name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        };
        db.Fachbereiche.Add(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await RefreshAsync(ct).ConfigureAwait(false);
        return row.FachbereichId;
    }

    public async Task RenameAsync(int id, string name, string? description,
        CancellationToken ct = default)
    {
        await using var db = database.CreateContext();

        var row = await db.Fachbereiche.FirstOrDefaultAsync(f => f.FachbereichId == id, ct)
            .ConfigureAwait(false);
        if (row is null) return;

        row.Name = name.Trim();
        row.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        await RefreshAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountFiltersAsync(int id, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();
        return await db.HostFilters.CountAsync(f => f.FachbereichId == id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Löscht einen Fachbereich.
    ///
    /// <b>Die Filter darin werden nicht gelöscht</b>, sondern nur aus dem
    /// Katalog genommen — sie fallen an ihren Autor zurück. Das ist der
    /// Unterschied zum alten Team-Modell, wo ein Filter dem Team gehörte und
    /// mit ihm verschwand: Hier hat jeder Filter einen Autor, und dem etwas
    /// wegzunehmen, weil eine Katalog-Gruppe aufgelöst wird, wäre unhöflich.
    ///
    /// Die Abonnements räumt die Datenbank per Cascade nicht mit — der Filter
    /// bleibt ja. Sie zeigen dann auf einen nicht mehr veröffentlichten Filter
    /// und werden beim Laden ignoriert.
    /// </summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = database.CreateContext();

        var published = await db.HostFilters.Where(f => f.FachbereichId == id)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var f in published) f.FachbereichId = null;

        // Abos auf jetzt unveroeffentlichte Filter sind gegenstandslos. Hier
        // ausdruecklich, weil kein Cascade greift — der Filter bleibt bestehen.
        var ids = published.Select(f => f.HostFilterId).ToList();
        if (ids.Count > 0)
        {
            var subs = await db.HostFilterSubscriptions
                .Where(s => ids.Contains(s.HostFilterId))
                .ToListAsync(ct).ConfigureAwait(false);
            db.HostFilterSubscriptions.RemoveRange(subs);
        }

        if (await db.Fachbereiche.FirstOrDefaultAsync(f => f.FachbereichId == id, ct)
                .ConfigureAwait(false) is { } row)
            db.Fachbereiche.Remove(row);

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        Log.Info("Fachbereich {Id} geloescht, {Count} Filter an ihre Autoren zurueckgegeben.",
            id, published.Count);

        await RefreshAsync(ct).ConfigureAwait(false);
    }
}
