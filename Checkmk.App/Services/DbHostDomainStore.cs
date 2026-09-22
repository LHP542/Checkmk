using Checkmk.Data;
using Microsoft.EntityFrameworkCore;
using NLog;

namespace Checkmk.App.Services;

/// <summary>
/// Host → Domain aus der zentralen Datenbank statt aus <c>hosts.json</c> auf
/// dem Fileshare.
///
/// Zwei Dinge, die der Dateiweg falsch machte und die hier nicht
/// zurueckgebaut werden duerfen:
///
/// <list type="number">
/// <item><b>Kein Read-Modify-Write der Gesamtmenge.</b> Die alte Fassung schrieb
/// bei jedem Speichern die komplette Datei zurueck — zwei gleichzeitige
/// Bearbeiter, und der Eintrag des Ersten war lautlos weg.
/// <see cref="Save"/> vergleicht stattdessen gegen den Stand in der Tabelle und
/// fasst nur an, was sich wirklich geaendert hat.</item>
/// <item><b>Kein I/O pro Abfrage.</b> <c>HostContext.DomainFor</c> ruft
/// <see cref="Load"/> fuer <i>jeden</i> Hostnamen auf; als Dateizugriff war das
/// schon grenzwertig, als Datenbank-Roundtrip waere es absurd. Deshalb haelt
/// der Store eine Momentaufnahme im Speicher und aktualisiert sie beim Start
/// und nach jedem Schreibvorgang.</item>
/// </list>
/// </summary>
public sealed class DbHostDomainStore : IHostDomainStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly CockpitDatabase _database;
    private readonly IHostDomainStore? _legacyFileStore;
    private volatile HostDomainState _snapshot = new();

    public DbHostDomainStore(CockpitDatabase database, IHostDomainStore? legacyFileStore = null)
    {
        _database = database;
        _legacyFileStore = legacyFileStore;
    }

    public string FilePath => "Datenbank CheckMK_Copilot (Tabelle HostDomain)";

    /// <summary>Momentaufnahme — kein I/O. Aktualitaet stellt
    /// <see cref="RefreshAsync"/> her.</summary>
    public HostDomainState Load() => _snapshot;

    /// <summary>
    /// Holt den aktuellen Stand aus der Datenbank. Schlaegt das fehl, bleibt die
    /// bisherige Momentaufnahme stehen — eine leere Zuordnung waere schlimmer
    /// als eine veraltete, weil dann jeder Host auf die Default-Domain fiele und
    /// Ping/RDP/SSH ins Leere liefen.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = _database.CreateContext();
            var rows = await db.HostDomains.AsNoTracking()
                .OrderBy(x => x.HostName)
                .ToListAsync(ct).ConfigureAwait(false);

            _snapshot = new HostDomainState
            {
                Hosts = [.. rows.Select(r => new HostDomainEntry { Host = r.HostName, Domain = r.Domain })]
            };
            Log.Info("Host-Domains aus der Datenbank gelesen: {Count} Eintraege.", rows.Count);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Host-Domains konnten nicht gelesen werden — behalte {Count} Eintraege "
                       + "aus der vorherigen Momentaufnahme.", _snapshot.Hosts.Count);
        }
    }

    public void Save(HostDomainState state)
    {
        try
        {
            using var db = _database.CreateContext();
            var existing = db.HostDomains.ToDictionary(x => x.HostName, StringComparer.OrdinalIgnoreCase);
            var wanted = state.Hosts
                .Where(h => !string.IsNullOrWhiteSpace(h.Host) && !string.IsNullOrWhiteSpace(h.Domain))
                .GroupBy(h => h.Host, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Domain, StringComparer.OrdinalIgnoreCase);

            var who = CurrentUser.Name;
            var now = DateTime.UtcNow;

            foreach (var (host, domain) in wanted)
            {
                if (existing.TryGetValue(host, out var row))
                {
                    if (string.Equals(row.Domain, domain, StringComparison.OrdinalIgnoreCase)) continue;
                    row.Domain = domain;
                    row.ChangedAtUtc = now;
                    row.ChangedBy = who;
                }
                else
                {
                    db.HostDomains.Add(new Checkmk.Data.HostDomain
                    {
                        HostName = host, Domain = domain, ChangedAtUtc = now, ChangedBy = who
                    });
                }
            }

            foreach (var (host, row) in existing)
                if (!wanted.ContainsKey(host))
                    db.HostDomains.Remove(row);

            var changed = db.SaveChanges();
            Log.Info("Host-Domains gespeichert ({Changed} Aenderungen).", changed);

            _snapshot = new HostDomainState
            {
                Hosts = [.. wanted.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                                  .Select(x => new HostDomainEntry { Host = x.Key, Domain = x.Value })]
            };
        }
        catch (Exception ex)
        {
            // Bewusst nicht durchreichen: der Aufrufer ist ein Dialog-Klick, und
            // eine Exception aus einem RelayCommand hat diese App schon einmal
            // beendet (siehe §5 in CLAUDE.md).
            Log.Error(ex, "Host-Domains konnten nicht gespeichert werden.");
        }
    }

    /// <summary>
    /// Einmalige Uebernahme aus der alten <c>hosts.json</c>: nur wenn die
    /// Tabelle noch komplett leer ist. Danach ist die Datenbank die Wahrheit und
    /// die Datei wird nie wieder angefasst — sonst wuerde ein Rechner mit altem
    /// Dateistand spaeter zentrale Aenderungen ueberschreiben.
    /// </summary>
    public async Task ImportLegacyIfEmptyAsync(CancellationToken ct = default)
    {
        if (_legacyFileStore is null) return;

        try
        {
            await using var db = _database.CreateContext();
            if (await db.HostDomains.AnyAsync(ct).ConfigureAwait(false)) return;

            var legacy = _legacyFileStore.Load().Hosts
                .Where(h => !string.IsNullOrWhiteSpace(h.Host) && !string.IsNullOrWhiteSpace(h.Domain))
                .GroupBy(h => h.Host, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (legacy.Count == 0) return;

            var now = DateTime.UtcNow;
            db.HostDomains.AddRange(legacy.Select(h => new Checkmk.Data.HostDomain
            {
                HostName = h.Host, Domain = h.Domain, ChangedAtUtc = now,
                ChangedBy = $"Import aus {_legacyFileStore.FilePath}"
            }));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            Log.Info("{Count} Host-Domains aus {Path} in die Datenbank uebernommen.",
                legacy.Count, _legacyFileStore.FilePath);
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Uebernahme der alten hosts.json ist fehlgeschlagen.");
        }
    }
}
