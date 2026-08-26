# Datenbank `CheckMK_Copilot` (FOC-SQL01)

Zentrale Ablage für alles, was **allen** Cockpit-Nutzern gemeinsam gehört:
globale Vorgaben, Host-Metadaten, Bereiche der Karte und den Filter-Katalog.

Was hier bewusst **nicht** hinein gehört: das Verbindungs-Secret und die
SSH-Passwörter. Die bleiben user-lokal unter `%APPDATA%\Kroste\Checkmk\` und mit
DPAPI an den Windows-User gebunden. Ein Geheimnis in einer Tabelle, die 48 Leute
lesen dürfen, ist keins mehr — unabhängig davon, ob die Datenbank verschlüsselt ist.

## Zwei Konten, mit Absicht

| Konto | Rechte | Wer benutzt es |
|---|---|---|
| `CheckMK_Copilot_SA` | `db_owner` | Administrator, nur zum Ausführen der Skripte in diesem Ordner |
| `CheckMK_Copilot_Worker` | `db_datareader` + `db_datawriter` | die ausgelieferte Anwendung |

Die Anwendung braucht zur Laufzeit **kein** `db_owner`. Sie liest und schreibt
Zeilen, mehr nicht. Da der Verbindungsstring mit der EXE auf ~50 Arbeitsplätzen
liegt und dort bestenfalls verschleiert ist, entscheidet allein dieses Recht,
was jemand anrichten kann, der ihn ausliest: Zeilen ändern ja, Tabellen löschen
nein. Das Trennen der Konten ist deshalb keine Förmlichkeit — es ist der einzige
wirksame Schutz an dieser Stelle.

## Migrationen laufen nicht vom Client

Kein `Database.Migrate()` beim Start. Sonst rennen 50 Clients gleichzeitig in ein
DDL-Update, für das die meisten gar keine Rechte haben — und der Erste, der
gewinnt, entscheidet über den Rest.

Stattdessen: Der Administrator führt die Skripte in Reihenfolge mit dem
SA-Konto aus, die Anwendung prüft beim Start nur `dbo.SchemaVersion` und sagt
klar Bescheid, wenn Anwendung und Schema nicht zusammenpassen.

Die Skripte sind **idempotent** (`IF NOT EXISTS`), ein zweiter Lauf schadet also
nicht. Reihenfolge:

```
001-initial.sql      Schema-Version, globale Einstellungen, Host-Domains
002-map-teams.sql    Bereiche, Host-Zuordnung, Teams, geteilte Filter, Sichten
003-area-points.sql  Bereiche als Punkt, Anschrift, Herkunft (Standort-Import)
004-area-sites.sql   Sichtbarkeit je Checkmk-Site (LHP / Schul_IT)
005-area-hostpattern.sql  Namensmuster je Bereich fuer Zuordnungsvorschlaege
006-area-hosttag.sql      Checkmk-Ortstag je Bereich (der bessere Weg zu 005)
007-fachbereich-katalog.sql  Filter-Katalog je Fachbereich mit Abo (loest Teams ab)
008-filter-target.sql        Filter matcht wahlweise auf den Host-Alias statt den Hostnamen
009-filter-selfsubscribe.sql Autoren abonnieren ihre eigenen veroeffentlichten Filter
```

Optional, kein Schema-Eingriff und ohne Versionssprung:

```
seed-map-settings.sql   Kartenquellen als Zeilen in GlobalSetting
```

**Passt die Version nicht**, sagt das Cockpit es in der Statusleiste („Datenbank-Schema
veraltet") und arbeitet mit dem weiter, was geht — es bricht nicht ab, aber die
neuen Funktionen fehlen, bis das Skript gelaufen ist.

## Wenn die Datenbank nicht erreichbar ist

Die Verfügbarkeit des Fileshares war der Grund, hier überhaupt hinzuziehen — also
darf die Datenbank nicht der nächste Engpass werden. Die Anwendung legt nach
jedem erfolgreichen Lesen eine Kopie der globalen Einstellungen unter
`%APPDATA%\Kroste\Checkmk\globals-cache.json` ab und startet damit weiter, wenn
FOC-SQL01 nicht antwortet. Sichtbar wird das in der Statusleiste, nicht nur im Log.

## Verbindungsstring

Drei Quellen, in dieser Reihenfolge — die erste, die etwas liefert, gewinnt:

| Reihenfolge | Datei | Wofür |
|---|---|---|
| 1 | `%APPDATA%\Kroste\Checkmk\db-dev.json` | Entwicklung. Überstimmt auf dem eigenen Rechner die ausgelieferte Datei. |
| 2 | `database.json` **neben der Exe** | Der Ausrollweg. |
| 3 | `bootstrap.json` → `DatabaseConnectionString` | Notnagel, falls der Wert doch zentral kommen soll. |

`TrustServerCertificate=True` gehört in den String, weil `Microsoft.Data.SqlClient`
seit Version 4 standardmäßig verschlüsselt und ein selbstsigniertes
Serverzertifikat sonst den ersten Verbindungsversuch mit einer Meldung abbricht,
die nach einem Passwortproblem aussieht. Hat FOC-SQL01 ein reguläres Zertifikat,
kann die Option weg.

### `database.json` erzeugen

Der Wert darin ist verschleiert und lässt sich nicht von Hand schreiben. Dafür
gibt es einen Schalter:

```
Checkmk.App.exe --protect-db "Server=FOC-SQL01;Database=CheckMK_Copilot;User Id=CheckMK_Copilot_Worker;Password=…;Encrypt=True;TrustServerCertificate=True"
```

Das schreibt `database.json` neben die Exe. Optional lässt sich ein Zielpfad als
zweites Argument angeben, um die Datei fürs Ausrollpaket woanders zu erzeugen.

> **Gelesen wird sie nur neben `Checkmk.App.exe`.** Eine Kopie in diesem
> `db/`-Verzeichnis ist ein Aufbewahrungsort (und per `.gitignore` vor dem
> versehentlichen Commit geschützt) — beim Ausrollen muss sie in den Ordner
> der Exe. Sonst startet das Cockpit ohne Datenbank und zeigt „Zentral: Cache".

Ergebnis:

```json
{ "ProtectedConnectionString": "obf1:EPTlRSeAIAYIE9Ee…" }
```

Ein Feld `ConnectionString` mit Klartext wird ebenfalls gelesen — praktisch für
eine schnell hingeschriebene Testdatei. Stehen beide drin, gewinnt der
verschleierte Wert.

### Was die Verschleierung leistet — und was nicht

**Sie ist kein Zugriffsschutz.** Der Schlüssel steckt im Binary, das neben der
Datei liegt; wer beides hat — und beides liegt auf ~50 Arbeitsplätzen — kommt an
den Klartext. Die Methoden heißen deshalb `Obfuscate`/`Deobfuscate` und nicht
Encrypt/Decrypt.

Was sie verhindert: dass ein Passwort im Klartext in Backups, Ticketanhängen und
über die Schulter landet. Zufallsfunde, keine Angreifer.

Was tatsächlich schützt, ist das Recht des Laufzeitkontos (siehe oben): Zeilen
lesen und schreiben in *einer* Datenbank, sonst nichts. Wer den String ausliest,
kann Daten ändern — aber keine Tabellen löschen und an keine andere Datenbank.
Genau dafür sind die zwei Konten da.
