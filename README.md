# Checkmk Cockpit — Benutzerhandbuch

> Projekt der **Arbeitsgruppe 5424 IT-Basis-Dienste** der Landeshauptstadt Potsdam.
> Alle dienstlichen Repos liegen in der GitHub-Organisation [LHP542](https://github.com/LHP542).

Ein Windows-Desktop-Tool für den **täglichen Umgang mit Checkmk 2.5** (REST-API v1),
gebaut für die Arbeitsgruppe **5424 IT-Basis-Dienste**. Es holt die häufigen
Admin-Handgriffe, die das Checkmk-Webinterface tief in Menüs vergräbt, an die
Zeile, an der du das Problem siehst.

Dieses Handbuch beschreibt alle Funktionen des Cockpits aus Anwendersicht. Wer
sich für Architektur und Interna interessiert: [`CLAUDE.md`](CLAUDE.md).

---

## Inhalt

1. [Installation](#1-installation)
2. [Ersteinrichtung](#2-ersteinrichtung)
3. [Die Oberfläche](#3-die-oberfläche)
4. [Die drei Alltags-Handgriffe](#4-die-drei-alltags-handgriffe)
5. [Tabellen- und Baumansicht](#5-tabellen--und-baumansicht)
6. [Host-Details](#6-host-details)
7. [Filter und Favoriten](#7-filter-und-favoriten)
8. [Regex-Beispiele für Filter](#8-regex-beispiele-für-filter)
9. [Tray und Notifications](#9-tray-und-notifications)
10. [Hosts-Tab: Service Discovery und Änderungen aktivieren](#10-hosts-tab-service-discovery-und-änderungen-aktivieren)
11. [Client-Aktualisierung (jetzt als Plugin)](#11-client-aktualisierung-jetzt-als-plugin)
12. [CSV-Export](#12-csv-export)
13. [Mehrere Sites am selben Server](#13-mehrere-sites-am-selben-server)
14. [Updates](#14-updates)
15. [Wo liegen meine Daten](#15-wo-liegen-meine-daten)
16. [Wenn etwas nicht funktioniert](#16-wenn-etwas-nicht-funktioniert)
17. [Hilfe und Kontakt](#17-hilfe-und-kontakt)
18. [Viewer-Modus: nur-lesen-Ausgabe an Fachbereiche](#18-viewer-modus-nur-lesen-ausgabe-an-fachbereiche)
19. [Zentrale Einstellungen in der Datenbank](#19-zentrale-einstellungen-in-der-datenbank)
20. [Bereiche: welcher Standort hat gerade ein Problem](#20-bereiche-welcher-standort-hat-gerade-ein-problem)

---

## 1. Installation

**Windows (empfohlen):**

1. Neuestes ZIP von den [GitHub Releases](https://github.com/LHP542/Checkmk/releases)
   herunterladen (`Checkmk-x.y.z-win-x64.zip`).
2. In einen beliebigen Ordner entpacken — z. B. `C:\Tools\Checkmk\`.
3. `Checkmk.App.exe` starten.

Das ZIP ist **self-contained** — es ist kein .NET-Runtime auf dem Rechner nötig,
alles Nötige ist im Bundle. Rechnen etwa 130 MB.

**Die gemeinsamen Daten** — Update-Kanal, OS-Attribut-Keys, Default-Domain und
die Domain-Zuordnung je Host — kommen aus der Datenbank `CheckMK_Copilot` auf
**FOC-SQL01**. Der Zugang dazu liegt als `database.json` im entpackten Ordner und
kommt mit dem Ausrollpaket; du musst nichts einrichten. Ist die Datenbank mal
nicht erreichbar, läuft das Cockpit mit der letzten bekannten Kopie weiter und
zeigt **„Zentral: Cache"** in der Statusleiste
([Abschnitt 19](#19-zentrale-einstellungen-in-der-datenbank)).

**Deine persönliche Anmeldung** liegt **lokal** unter `%APPDATA%\Kroste\Checkmk\settings.json`
— DPAPI-verschlüsselt (nur du kannst sie entschlüsseln). Sie liegt bewusst nicht
zentral: Anmeldedaten gehören pro Nutzer.

**Hinter einem Proxy?** Der Update-Check nutzt automatisch die
Windows-Standard-Anmeldedaten für den Proxy (Negotiate/NTLM). Am FortiProxy der
Arbeitsgruppe funktioniert das ohne Zusatzkonfiguration.

---

## 2. Ersteinrichtung

Beim ersten Start ist noch keine Verbindung eingerichtet — die Statusleiste zeigt
„Nicht konfiguriert". Menüpunkt **„Einstellungen"** oben rechts.

### Anmeldemethode wählen

Ganz oben im Dialog: **„Anmeldemethode"** mit zwei Optionen.

**Windows/LDAP (empfohlen)** — jeder Nutzer meldet sich mit seinem echten
AD-Account an. Damit steht im Checkmk-Audit-Log bei Ack/Downtime dein Name,
nicht „automation-User". Der Anmeldename ist mit deinem Windows-User
vorbelegt; du tippst nur einmal dein AD-Passwort ein. Weil Passwörter in der
Arbeitsgruppe maximal einmal jährlich rotieren, ist das kein Alltagsaufwand.

**Automation-User (Legacy)** — der klassische Weg mit einem dedizierten
Automation-Account und dessen Secret. Für Skript-artige Nutzung oder für Server,
die noch kein LDAP-Passwort für den User haben.

### Felder ausfüllen

| Feld | Was da rein muss |
|---|---|
| **Host** | Der DNS-Name des Checkmk-Servers, z. B. `monitoring.lhp.intern`. |
| **Site** | Der Site-Name — das URL-Segment hinter dem Host, meist `LHP-Prod` oder `main`. |
| **Bekannte Sites am selben Server** | *Optional.* Kommasepariert, z. B. `LHP-Prod, Schul_IT`. Sobald mehr als eine Site drin steht, erscheint oben rechts ein **Site-Umschalter** (siehe [Abschnitt 13](#13-mehrere-sites-am-selben-server)). |
| **Anmeldename** | Bei Windows/LDAP: dein Windows-User. Bei Automation: der Automation-User-Name (meist `automation`). |
| **Passwort/Secret** | Bei Windows/LDAP: dein AD-Passwort. Bei Automation: das lange Automation-Secret (der Zufalls-String aus der User-Verwaltung, **nicht** das GUI-Passwort). |
| **HTTPS** | Fast immer ja. Nur ausschalten, wenn dein Server nur HTTP kann (Lab). |
| **Zertifikatsfehler ignorieren (Lab)** | Nur setzen bei selbst-signierten Zertifikaten. In Produktion: aus lassen. |

Klick **„Testen"** — das Tool ruft `/version` auf und meldet Edition und
Version. Grün = klappt. Klick **„Speichern"** — Passwort/Secret wird
DPAPI-verschlüsselt in `%APPDATA%\Kroste\Checkmk\settings.json` abgelegt.

### Voraussetzung serverseitig (Windows/LDAP-Modus)

Der Nutzer muss in Checkmk unter *Setup → Users* **„REST API access"** angehakt
haben. LDAP-User bekommen das typischerweise beim Sync gesetzt. Wenn das fehlt,
antwortet Checkmk mit `401 Wrong credentials` — nicht wegen falschem Passwort,
sondern weil dieser User die REST-API gar nicht darf.

---

## 3. Die Oberfläche

Ganz oben die eigene Titelleiste mit:

- links **„Checkmk Cockpit"** + Versions-Badge
- rechts (wenn mehrere Sites konfiguriert sind) ein **Site-Umschalter**
- **„Einstellungen"** und **„Über"**
- Fensterkontroll-Buttons

Darunter die Reiter:

- **Status** — Live-Status aller überwachten Services (Startseite).
- **Hosts** — Host-Liste im Setup, Service Discovery, Änderungen aktivieren.
- **Bereiche** — Standorte mit Ampel je Bereich ([Abschnitt 20](#20-bereiche-welcher-standort-hat-gerade-ein-problem)).
  Nur vorhanden, wenn die zentrale Datenbank erreichbar ist.
- **Dashboard** — Kacheln je Favorit mit Hosts-Zahl und Service-Aggregat.

Am unteren Rand die blaue **Statusleiste** mit:

- links ein **Health-Punkt** (grün = letzter Refresh OK, rot = Fehler) plus die
  aktuelle Rückmeldung („Aktualisiert 14:32:07 — 87 Services, 14 Hosts").
- mittig, wenn zutreffend, ein gelber **Update-Badge**.
- rechts die Verbindungsangabe (`https://monitoring.lhp.intern/LHP-Prod (lars.kruegel)`).

Sobald du das Fenster minimierst, verschwindet die App **ins System-Tray**
(nicht in die Taskleiste). Siehe [Tray und Notifications](#9-tray-und-notifications).

---

## 4. Die drei Alltags-Handgriffe

Ack, Downtime und Kommentar — drei Aktionen, die im Webinterface je 4–6 Klicks
kosten. Hier eine Zeile wählen und ein Menü öffnen.

### Acknowledge (Problem quittieren)

1. Zeile im Status-Tab wählen (oder Rechtsklick).
2. Toolbar-Button **„Acknowledge…"** oder Menüpunkt.
3. Kommentar eingeben — **Pflicht** (Checkmk-Vorgabe).
4. **OK** — die Warnung ist quittiert. In der „Ack"-Spalte steht ein Haken.

### Downtime (geplante Wartung)

1. Zeile wählen, **„Downtime…"** klicken.
2. Kommentar eingeben (Pflicht).
3. **Dauer-Preset** wählen: 1 Stunde, 2 Stunden, 4 Stunden oder „bis morgen
   06:00" (praktisch für Overnight-Wartung).
4. **OK** — Downtime läuft ab **jetzt** bis zum berechneten Ende.

### Kommentar

Kontext an Host oder Service hinterlassen:

- **Status-Tab:** Zeile wählen → **Rechtsklick → „Kommentar…"**.
- **Host-Detail-Fenster:** entweder **„Host-Kommentar…"** oder **„Kommentar…"**
  in der Aktions-Toolbar (letzterer legt am markierten Service an).

Kommentar-Text eingeben (Pflicht) und wählen, ob der Kommentar **persistent**
sein soll (überlebt einen Neustart des Monitorings). Bestehende Kommentare
werden im Host-Detail-Fenster unten als Liste angezeigt (neueste oben, mit
Autor + Zeitstempel).

**Löschen:** in der Kommentarliste hat jeder Eintrag rechts einen roten
✕-Button — Klick löscht den Kommentar sofort (Host- und Service-Kommentare).
Kein Bestätigungs-Dialog; wenn du daneben klickst, kannst du den Kommentar
sofort neu anlegen.

### Bulk-Aktionen — mehrere Services gleichzeitig

1. **Ctrl-Klick** oder **Shift-Klick** in der Service-Tabelle markiert mehrere
   Zeilen.
2. **„Acknowledge…"** oder **„Downtime…"** öffnen den Dialog. Im Ziel steht
   z. B. **„7 Services auf 3 Hosts"**.
3. Ein Kommentar gilt für alle.
4. **OK** — das Tool arbeitet die Auswahl iterativ ab; Fortschritt in der
   Statusleiste: **„Ack 3/12: DBSQL01 / CPU load"**.

Wenn einzelne Aktionen fehlschlagen, bricht der Bulk **nicht ab** — Fehler
werden gesammelt und am Ende gemeldet.

---

## 5. Tabellen- und Baumansicht

### Spalten anpassen

**Rechtsklick auf die Kopfzeile** der Tabelle öffnet eine Liste aller verfügbaren
Spalten mit Häkchen. Anklicken blendet ein oder aus; das Menü bleibt dabei offen,
man kann also mehrere hintereinander umschalten. Denselben Eintrag gibt es als
Untermenü **„Spalten"** im normalen Rechtsklick-Menü einer Zeile.

**Reihenfolge**: Spaltenköpfe lassen sich mit der Maus an die gewünschte Stelle
ziehen. **Breite**: den Trenner zwischen zwei Köpfen ziehen.

Alles davon ist **persistent** — Reihenfolge und Sichtbarkeit werden sofort
gespeichert, die Breiten beim Schließen der App. Ablage:
`%APPDATA%\Kroste\Checkmk\columns.json`.

Zwei Einträge unten im Menü: **„Alle einblenden"** und **„Auf Vorgabe
zurücksetzen"**. Mindestens eine Spalte bleibt immer sichtbar — sonst käme man
an die Kopfzeile und damit ans Menü nicht mehr heran.

| Spalte | Inhalt |
|---|---|
| Status-Punkt (Ampel) | farbiger Punkt nach Service-Status |
| Host | Hostname |
| Host-Alias | Alias aus Checkmk |
| Anzeigename | sprechender Name, siehe [Abschnitt 18](#service_display_name--wo-der-sprechende-name-herkommt) |
| Service-Beschreibung | technische Service-Kennung |
| Status (OK/WARN/CRIT) | Statustext |
| Ausgabe der Prüfung | Plugin-Output, nimmt den Restplatz ein |
| Acknowledged | Ack-Häkchen |
| In Wartung | Downtime-Häkchen |
| Zeit seit letztem Check | wie lange der Check her ist |
| Zeit seit Statuswechsel | eingefärbt nach Frische |

Standardmäßig sind alle außer *Anzeigename* und *Zeit seit letztem Check*
eingeblendet — das ist genau die Ansicht, die es vor der Spaltenkonfiguration
schon gab. Kommt in einer neuen Version eine Spalte dazu, taucht sie
**ausgeblendet** im Menü auf und baut die gewohnte Ansicht nicht um.

> Im [Viewer-Modus](#18-viewer-modus-nur-lesen-ausgabe-an-fachbereiche) gibt es
> das nicht: dort bestimmt `viewer.json` den Spaltensatz, und weder Umsortieren
> noch das Menü sind verfügbar.



Der Status-Tab kann die Services entweder als flache Tabelle oder als **Baum
(Hosts → Services)** zeigen. Umschalter oben in der Toolbar.

**Baum:**

- Jeder Host ist ein oberster Knoten mit **OS-Pictogramm** (Fenster für Windows,
  Tux für Linux, „?" bei unbekanntem OS), Ampel und **Problem-Zähler**.
- Aufgeklappt: die Services des Hosts mit Ausgabe.
- **Rechtsklick** funktioniert kontextabhängig — auf einem Host-Knoten stehen
  andere Aktionen zur Verfügung als auf einem Service-Knoten.

Die **OS-Erkennung** liest primär das Custom Host Attribute (z. B.
„Operation System"), das ihr auf Folder-Ebene setzt und das auf die Hosts
vererbt wird. Fallback ist der Parse aus dem Check_MK-Agent-Service. Der
Attribut-Key ist konfigurierbar in der Tabelle `GlobalSetting` unter dem
Schlüssel `HostOsAttributeKeys` — Default probiert `tag_operation_system`,
`operation_system`, `operating_system` und `os_family` durch.

---

## 6. Host-Details

**Doppelklick** auf eine Zeile (Status-Tab, Hosts-Tab oder Baum) oder
**Rechtsklick → „Host-Details…"** öffnet ein eigenes Fenster mit:

- **Host-State-Ampel** (UP/DOWN/UNREACH)
- **In-Wartung- und Acknowledged-Badge** neben der Ampel (falls zutreffend)
- **Ordner-Pfad, IP-Adresse, Alias** aus der Config. Fehlt in Checkmk eine IP,
  ermittelt das Tool sie per **Ping/DNS** und markiert die Herkunft.
- **Plugin-Ausgabe** des Host-Checks

Rechts daneben Buttons:

- **„Host-Ack…"** — quittiert das Host-Problem
- **„Host-Downtime…"** — setzt den ganzen Host in Wartung
- **„Host-Kommentar…"** — Kommentar am Host anlegen

Darunter die Service-Tabelle mit Aggregat-Zählern (OK/WARN/CRIT/UNK) und den
bekannten Ack/Downtime/Kommentar-Aktionen. Bulk-Ack und Bulk-Downtime funktionieren
hier genauso.

Ganz unten die Liste **bestehender Kommentare** mit dem ✕-Button zum Löschen.

Mehrere Detail-Fenster können parallel offen sein.

---

## 7. Filter und Favoriten

Wenn ihr über tausend Hosts habt, will keiner alle sehen. Speicherbare Filter —
hier **„Favoriten"** — beschränken die Sicht auf das, was für dich relevant ist.

### Freitext-Filter (immer sichtbar)

Oben im Status-Tab: einfaches Suchfeld. Sucht case-insensitive über **Host,
Service, Ausgabe und Alias**. Ideal um schnell auf „CPU load" oder eine
Ticket-Nummer in der Plugin-Ausgabe zu filtern. **Ctrl+F** fokussiert das Feld,
**Esc** leert es.

### Persistente Favoriten (Combobox)

In der Toolbar (Status-Tab **und** Hosts-Tab) gibt es die Combobox
**„Host-Filter:"**. Wählst du dort einen Favoriten, sind sofort in beiden Tabs
nur noch die passenden Hosts sichtbar. Zurück auf alle: Auswahl leeren
(„(Alle Hosts)").

### Favoriten pro Site

Favoriten sind **pro Site** organisiert — in der Site `LHP-Prod` andere als in
`Schul_IT`. Beim Site-Wechsel lädt das Cockpit automatisch die Favoriten der
neuen Site nach. Neu angelegte Favoriten landen unter der aktuell aktiven Site.

### Favoriten aus einer Auswahl speichern

- **Im Hosts-Tab**: Ctrl-/Shift-Klick markiert mehrere Hosts, dann
  **„Auswahl als Favorit…"** in der Toolbar oder im Kontextmenü.
- **Im Status-Tab**: Rechtsklick auf einen Service (oder mehrere markierte) →
  **„Zu Favorit hinzufügen…"** oder **„Als neuen Favorit speichern…"**. Der
  Hostname wird aus dem Service ermittelt und dedupliziert.

Wenn du zu einem Favoriten hinzufügen willst und noch keiner existiert, legt
das Tool automatisch einen neuen an — der Klick landet nie ins Leere.

### Favoriten verwalten

**„Filter verwalten…"** öffnet ein eigenes Fenster mit einer Liste aller
Favoriten der aktuellen Site. Rechts der Editor mit drei Feldern:

- **Name** — was in der Combobox erscheint.
- **Gehört zu** — *persönlich* oder ein **Team**. Siehe unten.
- **Hostname-Regex** — .NET-Regex, case-insensitive. Siehe
  [Abschnitt 8](#8-regex-beispiele-für-filter) für ausführliche Beispiele.
- **Explizite Hostnamen** — eine feste Liste, ein Hostname pro Zeile. Wenn hier
  etwas steht, wird das **Regex ignoriert** — es zählen exakt diese Hostnamen.

Buttons:

- **„Übernehmen"** — Änderungen speichern. Ein kaputter Regex wird
  **vorher** validiert; die Fehlermeldung erscheint direkt unter dem Regex-Feld
  und der Filter wird nicht gespeichert, bis du ihn korrigierst.
- **„Aktivieren"** — den gewählten Filter sofort aktiv setzen.
- **„Filter deaktivieren"** — kein Filter aktiv, alle Hosts sichtbar.

### Filter teilen: der Katalog

Bisher baute sich jeder seinen eigenen Filtersatz — und wenn der
Netzwerkkollege im Urlaub war, fing die Vertretung bei null an.

**Veröffentlichen:** Setz einen Filter im Feld **„Veröffentlicht in"** auf einen
Fachbereich. Er steht dann im Katalog, und jeder kann ihn abonnieren. In der
Liste links trägt er den Fachbereich als Marke; daran siehst du, dass eine
Änderung daran alle Abonnenten betrifft.

**Abonnieren:** Knopf **„Katalog…"**. Dort steht alles, was Fachbereiche
veröffentlicht haben — mit Autor, Abonnentenzahl und, was am meisten hilft,
**wie viele Hosts der Filter gerade trifft**. Anhaken, übernehmen, fertig; der
Filter erscheint in deiner Auswahl.

> Ein Filter kommt **nicht** von allein in dein Dropdown, nur weil ihn jemand
> veröffentlicht hat. Du entscheidest, was du siehst. Deine eigenen Filter sind
> immer dabei und lassen sich nicht abbestellen.

**Ändern darf nur der Autor.** Ein abonnierter Filter steht bei dir in der
Liste, die Felder sind aber gesperrt — sonst würde deine Korrektur ungefragt
bei allen anderen Abonnenten landen. Willst du eine eigene Variante, kopierst
du ihn: „Neu", Regex übernehmen, anpassen.

**Fachbereiche** legt ein Admin über **„Fachbereiche…"** an. Sie sind reine
Ordnung im Katalog, **kein Zugriffsschutz** — veröffentlichen und abonnieren
darf jeder, unabhängig davon, in welchem Fachbereich er arbeitet. Die echte
Grenze ist deine Checkmk-Rolle. Solange in `dbo.AppAdmin` niemand steht, darf
jeder Fachbereiche verwalten; ab dem ersten Eintrag nur noch die Genannten.

Einen Fachbereich zu löschen nimmt seine Filter **nicht** mit: Sie gehen an
ihre Autoren zurück und bleiben dort als persönliche Filter. Nur die Abos
verfallen. Der Dialog sagt vorher, wie viele es sind, und will einen zweiten
Klick.

**Ohne zentrale Datenbank** ändert sich nichts: Favoriten liegen dann
**user-lokal** unter `%APPDATA%\Kroste\Checkmk\filter.json`, pro Site ein
eigenes Set, und jeder hat seine eigenen. Mit Datenbank wird diese Datei beim
ersten Start **einmalig übernommen**.

Ist die Datenbank gerade nicht erreichbar, siehst du deinen letzten bekannten
Stand, kannst ihn aber **nicht ändern** — die Statuszeile des Dialogs sagt das.
Das ist Absicht: Eine Änderung, die nur lokal landet, wäre beim nächsten
erfolgreichen Laden lautlos wieder weg.

> Welcher Filter zuletzt **aktiv** war, bleibt immer bei dir auf dem Rechner.
> Sonst würde dein Umschalten die Ansicht aller anderen im Team mit umstellen.

---

## 8. Regex-Beispiele für Filter

Das Regex-Feld nimmt einen **.NET-Regex, case-insensitive**, gematcht wird
**IsMatch** (nicht Full-Match) — die Regel greift also, sobald der Ausdruck
**irgendwo** im Hostnamen passt. `sql` matcht `dbsql01` genauso wie
`sql-cluster-b`.

### Einfache Enthält-Suchen

| Regex | Trifft auf |
|---|---|
| `sql` | `DBSQL01`, `sql-cluster-b`, `mssql-prod`, `dbsql-hotstandby` |
| `sql\|ora` | alle DB-Server (MSSQL **oder** Oracle) |
| `web\|iis\|nginx` | alle Web-Server-Kandidaten |
| `dc0\|dc1` | Domain-Controller `dc0*` und `dc1*` |

### Anker: Anfang und Ende

Der Caret `^` bindet an den **Anfang** des Hostnamens, das Dollar `$` an das
**Ende**.

| Regex | Trifft auf | Trifft *nicht* auf |
|---|---|---|
| `^db-` | `db-prod01`, `db-schulen` | `xdb-prod`, `webdb-01` |
| `-prod$` | `dc0-prod`, `mssql-prod` | `dc0-prod-hot`, `prod-dc0` |
| `^srv-.*-prod$` | `srv-mssql-prod`, `srv-web-prod` | `srv-web-test`, `mssql-prod` |

### Alternativen mit Gruppen

`(a|b|c)` ist eine Gruppe mit Alternativen. Das drumherum funktioniert wie
in normalen Regexen.

| Regex | Trifft auf | Bedeutung |
|---|---|---|
| `^(dc0\|dc1)-` | `dc0-prod`, `dc1-schulen` | DC0- oder DC1-Präfix |
| `-(prod\|test\|dev)$` | `srv-web-prod`, `srv-app-test` | endet auf einer der Umgebungen |
| `^(db\|mssql\|ora).*prod` | `db-cluster-prod`, `mssql-schul-prod` | DB-Präfix + irgendwo „prod" |

### Zeichenklassen

Eckige Klammern definieren eine Zeichenmenge — genau **eines** dieser Zeichen
trifft.

| Regex | Trifft auf |
|---|---|
| `dbsql0[1-9]` | `dbsql01` bis `dbsql09` |
| `srv-[abcd]` | `srv-a`, `srv-b`, `srv-c`, `srv-d` |
| `[a-z]{3}-[0-9]{2}$` | dreistellige Bezeichnung + Bindestrich + zwei Ziffern am Ende: `abc-01`, `dev-42` |

### Zahlen und Bereiche

| Regex | Trifft auf |
|---|---|
| `\d` | irgendeine Ziffer |
| `\d{2,}` | zwei oder mehr aufeinanderfolgende Ziffern |
| `sql\d{2}$` | `sql01`…`sql99` am Ende |
| `(0[1-9]\|1[0-2])$` | endet auf einer Zahl von 01 bis 12 (z. B. für Monatscodes im Hostnamen) |

### Ausschlüsse mit Negation

Regex kann „passt **nicht** auf X" nur über **Lookarounds** — geht in .NET, ist
aber selten die einfachste Lösung.

| Regex | Trifft auf | Bedeutung |
|---|---|---|
| `^(?!.*test).*sql` | `dbsql01` | Enthält „sql", aber **nicht** „test" |
| `^srv-(?!prod)` | `srv-dev-01`, `srv-test-a` | Fängt mit `srv-` an, aber nicht mit `srv-prod` |

Für Ausschlüsse ist eine **Include-Liste** oft einfacher: einfach die Hostnamen
zeilenweise ins Feld „Explizite Hostnamen" schreiben.

### Praxis-Rezepte

**Alle Datenbank-Server (MSSQL, Oracle, PostgreSQL):**
```
sql|ora|pgsql|postgres
```

**Nur Produktions-Web-Server, unabhängig vom Präfix:**
```
(web|iis|nginx|apache).*prod
```

**Alle Terminalserver an drei Standorten:**
```
^ts-(lhp|schul|kita)-\d+
```

**„Kritische Server" mit ein paar Namenskonventionen:**
```
^(dc\d|mssql-cluster|exchange-)|core-network
```

**Alle Hosts eines Kunden anhand Ordner-Namens im Präfix:**
```
^kunde42-
```

### Häufige Fehler

- **`^` mit `IsMatch` vergessen**: `db-` matcht auch `webdb-01`. Wenn du wirklich
  „beginnt mit" willst, brauchst du `^db-`.
- **Wildcard falsch geschrieben**: der Regex-Wildcard ist `.*` (Punkt-Stern),
  nicht `*` alleine.
- **Sonderzeichen nicht escaped**: Klammern, Punkt, Pipe usw. haben in Regex
  eine Sonderbedeutung. Wenn du sie **wörtlich** meinst, `\.` schreiben. Beispiel:
  `srv\.lhp\.intern` — sucht nach dem literalen String „srv.lhp.intern".
- **Case-Sensitivity überdenken**: Das Cockpit setzt automatisch
  `IgnoreCase` — `db-prod` matcht auch `DB-PROD`. Keine Sorge um Groß-/Kleinschreibung.

### Wenn der Regex nicht funktioniert wie erwartet

Zwei einfache Tests:

- **Ins Freitext-Feld tippen**: der macht auch case-insensitive Contains-Match
  auf den Hostnamen. Wenn dein Wunsch-Ergebnis dort schon nicht sichtbar ist,
  ist das kein Regex-Problem sondern eine Frage der Namen.
- **Explizite Liste vorziehen**: bei kleinen, festen Server-Gruppen (< 20 Hosts)
  ist die zeilenweise Liste unschlagbar — kein Regex-Debugging.

Wer .NET-Regex im Browser testen will:
[regex101.com](https://regex101.com) → Flavor auf „.NET" umschalten,
„Case Insensitive" anhaken.

---

## 9. Tray und Notifications

Minimieren legt die App **ins System-Tray**. Das Tray-Icon zeigt per Ampelfarbe
den **schlechtesten Status im aktiven Filter**. Im Tray läuft der Auto-Refresh
weiter, und bei Statusänderungen bekommst du eine **Toast-Notification** —
Action-Center-kompatibel.

Beim ersten Toast legt das Tool automatisch einen Startmenü-Eintrag
„Checkmk Cockpit" an — Windows-Requirement für Toast-Notifications von
unpackaged Apps.

**Wichtig:** Prüfe unter `Win+I` → System → Benachrichtigungen, dass
**„Benachrichtigungen von anderen Apps und Absendern erlauben"** angeschaltet
ist. Ist diese Sammel-Option aus, kommen keine Toasts durch.

Notifications sind **gebündelt** (eine Sammelmeldung statt zehn einzelner Toasts)
und **filter-scoped** (dein DB-Favorit alarmiert nicht bei Web-Server-Ausfällen).

Zurück aus dem Tray: Klick auf das Tray-Icon oder Rechtsklick → **„Anzeigen"**.
Beenden über Rechtsklick → **„Beenden"**.

---

## 10. Hosts-Tab: Service Discovery und Änderungen aktivieren

### Hosts-Liste

Zeigt Hostname, Ordner, IP und Alias jedes konfigurierten Hosts. Die aktuelle
Filter-Auswahl greift auch hier. Doppelklick öffnet das **Host-Detail-Fenster**.

### Änderungen aktivieren

Nach jeder Änderung im Setup: **„Änderungen aktivieren"**.

### Service Discovery — bestehende Hosts ins Monitoring bringen

1. Zeile in der Host-Liste anklicken.
2. Toolbar-Button **„Services entdecken"** oder Rechtsklick →
   **„Services entdecken (fix_all + aktivieren)"**.
3. Das Tool startet einen Hintergrund-Task auf dem Server (`fix_all`), pollt bis
   fertig, aktiviert die Änderungen automatisch, lädt die Liste neu.

### Host anlegen (standardmäßig ausgeblendet)

Das Formular ist per Default **nicht sichtbar** — Setup läuft zentral. Zum
Einblenden: in der Tabelle `GlobalSetting` den Schlüssel `ShowHostCreation`
auf `true` setzen.

Bei sichtbarem Formular:

- **Hostname** *(Pflicht)*.
- **Ordner** — **ID-Pfad**, nicht Titel. Root ist `/`, ein DB-Ordner z. B.
  `/datenbanken/db-mssql`.
- **IP-Adresse** — optional.
- **Alias** — optional.

**„Anlegen"** legt den Host im Setup an. Danach fehlt noch die
Service-Discovery, damit er überwacht wird.

---

## 11. Client-Aktualisierung (jetzt als Plugin)

Ab **v1.7.0** liegt die Client-Aktualisierung nicht mehr im Cockpit-Kern —
nur wer das externe Plugin installiert, sieht den Menüpunkt „Client
aktualisieren…" im Kontextmenü. Hintergrund: die Aktion greift mit Admin-
Credentials auf entfernte Hosts zu und ist bewusst nicht für jeden
Cockpit-Nutzer verfügbar.

**Installation** (nur wenn du die Funktion brauchst):

1. Neuestes ZIP von den
   [Plugin-Releases](https://github.com/LHP542/Checkmk-Plugin-AgentUpdater/releases)
   herunterladen.
2. Entpacken → `CheckmkPlugin.AgentUpdater.dll` in den Ordner **`plugins/`**
   **neben** deiner `Checkmk.App.exe` legen (Ordner ggf. anlegen).
3. Cockpit neu starten. Im NLog-File siehst du unter Info:
   `Plugin registriert: Client-Aktualisierung x.y.z`.

Danach steht „Client aktualisieren…" in den Kontextmenüs der Service-Grid, der
Baumansicht und der Hosts-Liste. Bedienung und Konfiguration siehe README des
Plugin-Repos. Agent-Share und Skript-Vorlage liegen im Plugin-Datenordner
`%APPDATA%\Kroste\Checkmk\plugins\kroste.checkmk.agent-updater\settings.json`.

---

## 12. CSV-Export

Toolbar → **„CSV-Export…"**. Exportiert die **aktuell gefilterte Ansicht** —
mit allen Filter-Einstellungen (Favorit, Freitext, „Nur Probleme").

Format:

- Semikolon-getrennt (Excel öffnet das direkt korrekt)
- UTF-8-BOM (Umlaute stimmen)
- RFC-4180-konformes Quoting

Spalten — **fest**, unabhängig davon, was in der Tabelle gerade eingeblendet ist:

```
Host;Alias;Anzeigename;Service;Status;Ausgabe;Ack;Downtime;Age
```

> „Anzeigename" ist seit v1.7.8 dabei. Wer den Export weiterverarbeitet: alle
> Spalten ab „Service" sind dadurch um eine Position nach rechts gerutscht.

---

## 13. Mehrere Sites am selben Server

Wenn ihr am selben Checkmk-Server mehrere Sites betreibt (z. B. `LHP-Prod` für
den Regelbetrieb und `Schul_IT` für die Schulen), muss man nicht ständig die
Verbindung neu einrichten.

**Einrichtung**: in den Einstellungen das Feld **„Bekannte Sites am selben Server"**
mit den Site-Namen kommasepariert füllen, z. B. `LHP-Prod, Schul_IT`. Host,
Anmeldung und Secret bleiben identisch.

**Nutzung**: sobald mehr als eine Site drin steht, erscheint oben rechts in der
Titelleiste ein **Site-Dropdown**. Ein Klick wechselt die aktive Site — das
Cockpit lädt die Livestatus-Daten der neuen Site und wechselt gleichzeitig die
Favoriten auf das Set der neuen Site.

Die zuletzt aktive Site wird gespeichert; beim nächsten App-Start landest du
wieder dort.

---

## 14. Updates

Das Tool prüft **beim Start** einmal, ob es eine neuere Version auf GitHub gibt.
Der Check läuft im Hintergrund und blockiert die App nicht.

Am Firmen-Proxy (Fortinet): der Update-Check nutzt automatisch die
Windows-Anmeldedaten für die Proxy-Auth.

Bei neuerer Version erscheint in der Statusleiste ein gelbes Feld **„Update auf
1.7.1 verfügbar"**. Klick öffnet einen Dialog:

- **Jetzt installieren** *(seit v1.7.1)* — lädt das Windows-ZIP mit
  Fortschrittsanzeige herunter, entpackt es in einen Temp-Ordner, startet ein
  Austausch-Skript, beendet die App. Das Skript wartet auf das Prozess-Ende,
  kopiert die neuen Dateien über die alte Installation (`xcopy /E /Y /I`) und
  startet die App neu. Ein `update.log` im Temp-Ordner (`%TEMP%\Checkmk-update-*\`)
  dokumentiert den Ablauf für die Fehlersuche.
- **Release-Seite öffnen** — führt zum GitHub-Release im Browser (Fallback,
  wenn der Auto-Install mal hakt oder du händisch installieren willst).
- **Später** — Badge bleibt, beim nächsten Start wird wieder geprüft.
- **Diese Version überspringen** — der Badge kommt erst wieder, wenn eine
  **noch neuere** Version rauskommt.

---

## 15. Wo liegen meine Daten

| Was | Wo | Wer teilt sich das |
|---|---|---|
| App-Konfiguration (Update-Kanal, OS-Attribut-Keys, Default-Domain, …) | Datenbank `CheckMK_Copilot` auf **FOC-SQL01**, Tabelle `GlobalSetting` | zentral, alle Nutzer |
| Domain-Zuordnung je Host | Datenbank, Tabelle `HostDomain` | zentral, alle Nutzer |
| Zugang zur Datenbank | `database.json` neben `Checkmk.App.exe` | kommt mit dem Ausrollpaket |
| Kopie der zentralen Einstellungen (Ausfallschutz) | `%APPDATA%\Kroste\Checkmk\globals-cache.json` | lokal, automatisch |
| Verbindung (Host/Site/User/Secret) | `%APPDATA%\Kroste\Checkmk\settings.json` | lokal, DPAPI-verschlüsselt |
| SSH-Logins (User+Passwort je Host) | `%APPDATA%\Kroste\Checkmk\ssh-creds.json` | lokal, DPAPI-verschlüsselt |
| Filter/Favoriten (pro Site) | Datenbank, Tabellen `HostFilter` / `HostFilterHost` | zentral; eigene plus abonnierte aus dem Katalog |
| Fachbereiche und Abos | Datenbank, Tabellen `Fachbereich` / `HostFilterSubscription` / `AppAdmin` | zentral |
| Filter **ohne** Datenbank | `%APPDATA%\Kroste\Checkmk\filter.json` | lokal; wird mit Datenbank einmalig übernommen |
| Kopie der Filter (Ausfallschutz) | `%APPDATA%\Kroste\Checkmk\filter-cache.json` | lokal, automatisch |
| Übersprungene Update-Version | `%APPDATA%\Kroste\Checkmk\updates.json` | lokal |
| UI-Zustand (Auto-Refresh, Baum/Tabelle, letzter Filter) | `%APPDATA%\Kroste\Checkmk\statusview.json` | lokal |
| Spalten (Reihenfolge, Sichtbarkeit, Breiten) | `%APPDATA%\Kroste\Checkmk\columns.json` | lokal |
| Logs | `logs\` neben `Checkmk.App.exe` | lokal |

**Grundregel**: alles was mehreren Nutzern nutzt und **keine Secrets** enthält,
liegt zentral. User-spezifische Anmeldedaten, persönliche Favoriten und
UI-Präferenzen liegen lokal.

> **Seit v1.9 liegen die zentralen Daten in der Datenbank, nicht mehr auf dem
> Fileshare.** Der Umzug passiert von selbst: Beim ersten Start mit
> Datenbankzugang übernimmt das Cockpit die vorhandene `hosts.json` einmalig in
> die Tabelle. Danach wird die Datei nicht mehr gelesen. Details in
> [Abschnitt 19](#19-zentrale-einstellungen-in-der-datenbank).

---

## 16. Wenn etwas nicht funktioniert

### „Nicht konfiguriert — bitte Verbindung in den Einstellungen setzen"

Deine lokale `settings.json` existiert nicht oder ist unvollständig. Einfach
Einstellungen öffnen, Verbindung eingeben, speichern.

### „Wrong credentials" (HTTP 401) beim Testen

Zwei häufige Ursachen:

- **Bei Windows/LDAP-Modus**: dein AD-Passwort ist abgelaufen (jährliche
  Rotation) — einfach neu tippen und speichern. Oder der User hat in Checkmk
  **REST API access** nicht angehakt.
- **Bei Automation-Modus**: du hast das GUI-Passwort statt des Automation-Secrets
  eingetragen. Das Automation-Secret ist ein langer Zufalls-String aus der
  User-Verwaltung.

### Regex-Filter: „Regex ungültig" beim Übernehmen

Der Filter-Manager validiert deinen Regex, bevor er ihn speichert. Bei Fehler
erscheint eine rote Meldung direkt unter dem Regex-Feld:

- **Klammern nicht geschlossen**: fehlende `)`, `]` oder `}`.
- **Ungültiger Escape**: `\p` oder `\q` sind keine gültigen Zeichenklassen.
- **Endlose Alternation**: `|` am Anfang oder Ende oder doppelt.

Siehe [Abschnitt 8](#8-regex-beispiele-für-filter) für Beispiele.

### Der Filter zeigt keine Hosts, obwohl welche passen sollten

- **Ist die richtige Site aktiv?** Filter sind pro Site — auf `Schul_IT` siehst
  du keine LHP-Filter.
- **Ist im Regex ein `^` vergessen?** `^db-` bindet an den Anfang; ohne `^`
  matcht `db-` auch mitten im Namen.
- **Sind Sonderzeichen escaped?** `srv.lhp.intern` matcht mehr als du denkst,
  weil `.` in Regex „irgendein Zeichen" heißt. Für den literalen Punkt: `srv\.lhp\.intern`.

### OS-Erkennung stimmt nicht (Windows als Linux erkannt oder umgekehrt)

Das Cockpit liest das OS aus dem Custom Host Attribute (Default: Suche in
`tag_operation_system` etc.). Prüfen:

1. Ist auf Folder-Ebene ein Custom Attribute gesetzt und wird es vererbt?
2. Wie heißt der interne Key? Im **Log** (Debug-Level) listet das Cockpit
   beim ersten Refresh alle gesehenen Attribute-Keys — dort taucht der echte
   Key auf.
3. Nicht der erwartete Key dabei? In der Tabelle `GlobalSetting` unter dem
   Schlüssel `HostOsAttributeKeys` den echten Key ergänzen (JSON-Liste).

Fallback ist der Parse der Check_MK-Agent-Ausgabe („OS: windows/linux") — der
greift nur, wenn kein Custom Attribute gesetzt ist.

### Site-Umschalter zeigt nur eine Site

Prüfe unter Einstellungen das Feld **„Bekannte Sites am selben Server"** —
Kommasepariert, mindestens zwei Sites. Nach dem Speichern zeigt der Dropdown
alle Einträge.

### „Ordner nicht gefunden" beim Host-Anlegen

Wahrscheinlich der Titel aus der Breadcrumb genommen statt des ID-Pfads. Im
Checkmk-Webinterface in die URL schauen — hinter `folder=` steht der ID-Pfad.

### Zertifikatsfehler beim Verbinden

Selbst-signiertes Zertifikat oder nicht im Windows-Zertifikatspeicher. **Lab**:
Haken „Zertifikatsfehler ignorieren (Lab)" setzen. **Produktion**: ein
korrektes Zertifikat installieren.

### Woher die Updates kommen

Der Kanal steht zentral in `GlobalSetting.UpdateChannelUrl` und darf **eine
Adresse oder ein Ordner** sein — das Cockpit erkennt das an der Schreibweise:

| Wert | Weg |
|---|---|
| `https://api.github.com/repos/LHP542/Checkmk/releases/latest` | GitHub-Release |
| `\\samba01\542$\5424_IT-Basis-Dienste\CheckMK\CheckMK_Copilot` | Ordner im Netz |

Beim Ordner reicht es, das ZIP hineinzukopieren — **die Version liest das
Cockpit aus dem Dateinamen** (`Checkmk-1.14.0-win-x64.zip`). Optional daneben:

- `update.json` — das signierte Manifest (siehe unten). Liegt es da, gibt **es**
  den Ausschlag, nicht das jüngste ZIP im Ordner.
- `v1.14.0.md` oder `RELEASE_NOTES.md` — was im Update-Dialog angezeigt wird.

Es gewinnt immer die **höchste Version**, nicht die neueste Datei. Kopierst du
ein älteres Paket zurück in den Ordner, wird daraus also kein „Update".

„Release-Seite öffnen" öffnet beim Ordner-Kanal den Ordner im Explorer.

### Der Update-Badge kommt nie, obwohl es eine neue Version gibt

- Ordner-Kanal: Netzlaufwerk nicht erreichbar, oder kein `Checkmk-*-win-x64.zip`
  darin (im Log als Debug-Meldung).
- Ordner-Kanal mit eingeschalteter Signaturprüfung: `update.json` fehlt — dann
  gibt es bewusst **kein** Update statt eines ungeprüften.
- GitHub-Kanal: kein Internetzugang, oder Proxy-Auth klappt nicht → einmal
  ab-/anmelden.
- Version explizit übersprungen → `%APPDATA%\Kroste\Checkmk\updates.json`
  löschen, dann erscheint der Badge wieder.

### „Update abgelehnt: …" im Update-Dialog

> **Bei uns tritt das nicht auf.** Die Signaturprüfung ist bewusst
> abgeschaltet — wer auf den Update-Ordner schreiben darf, regelt die
> NTFS-Berechtigung. Der folgende Abschnitt gilt nur, falls sie jemand
> einschaltet.

Das ist keine Störung, sondern eine Aussage: Das Paket ist **nicht** das, was es
sein sollte. Der Dialog nennt den Grund.

| Meldung | heißt |
|---|---|
| *Signatur des Manifests stimmt nicht* | Das Paket kommt nicht von der Stelle, die dein Cockpit als Herausgeber kennt |
| *kein `update.json`* | Zum Release fehlt das signierte Manifest |
| *Prüfsumme stimmt nicht* | Das Paket wurde unterwegs verändert |
| *Größe weicht ab* | Meist ein abgebrochener Download — erneut versuchen |
| *Manifest ist für Version X* | Es wird eine andere Version angeboten als signiert wurde |

**In keinem dieser Fälle wird das Paket auch nur entpackt.** Wenn du sicher
bist, dass alles stimmt, ist der Weg über „Release-Seite öffnen" und manuelles
Ersetzen der richtige — nicht das Abschalten der Prüfung.

### Notifications erscheinen nicht (Windows)

- Fokusassistent (Ruhezeiten) aktiv? Dann werden Toasts unterdrückt.
- Startmenü-Eintrag „Checkmk Cockpit" fehlt (Windows-Requirement) — beim
  ersten Toast-Trigger ist etwas schiefgelaufen (Log prüfen).
- Toast im Action Center suchen — dort landen sie auch nach dem Popup.
- Sammel-Option unter `Win+I` → System → Benachrichtigungen prüfen.

### „Client aktualisieren…" fehlt im Kontextmenü

Ab v1.7.0 ist die Funktion ins externe Plugin ausgezogen. Siehe [Abschnitt 11](#11-client-aktualisierung-jetzt-als-plugin).

### Die App fühlt sich falsch an — was tun

1. Ins Logfile schauen (`logs\` neben der Exe). Passwörter/Secrets sind
   maskiert — kannst du bedenkenlos anhängen.
2. Reproduzierbar? Issue auf GitHub mit Logauszug und Kontext.

---

## 17. Hilfe und Kontakt

- **Arbeitsgruppe:** 5424 IT-Basis-Dienste
- **GitHub-Repo:** <https://github.com/LHP542/Checkmk>
- **Bugs, Feature-Wünsche:** dort als Issue oder direkt an Lars.

---

## 18. Viewer-Modus: nur-lesen-Ausgabe an Fachbereiche

Für Leute, die **nur gucken** sollen — Fachbereiche, Bereitschaft, Leitstand —
gibt es eine zweite Betriebsart. Sie wird allein dadurch aktiviert, dass eine
Datei `viewer.json` **neben `Checkmk.App.exe`** liegt. Ohne die Datei verhält
sich die App exakt wie bisher; beide Modi existieren also parallel aus
demselben Binary.

### Was sich im Viewer-Modus ändert

| | ohne `viewer.json` | mit `viewer.json` |
|---|---|---|
| Verbindung | Einstellungen-Dialog, `%APPDATA%\…\settings.json` (DPAPI) | aus der Datei |
| Tabs | Status, Hosts, Dashboard, Bereiche | nur **Status** — plus **Bereiche**, wenn `map.show` gesetzt ist |
| Einstellungen-Button | da | weg |
| Ack / Downtime / Kommentar | da | weg (Toolbar, Kontextmenü **und** Hotkeys Ctrl+A/D/K) |
| RDP / SSH / Ping / Host-Einstellungen | da | weg |
| Plugins aus `plugins\` | werden geladen | werden **nicht** geladen |
| Spalten der Tabelle | fest | aus der Datei |
| Host-Filter | eigene und abonnierte | **nur** aus der Datei |
| Bei neuem Problem | Toast (wenn im Tray) | Toast **+ Fenster springt auf** |
| Favoriten anlegen / „Filter verwalten…" | da | weg |
| Host-Details, Baumansicht, Freitext-Filter, CSV-Export | da | bleiben da |

Der Host-Detail-Dialog geht weiter auf, zeigt aber ebenfalls keine
Schreib-Buttons und kein ✕ an den Kommentaren.

### Das Secret: Base64-maskiert

Das Secret gehört in **`secretBase64`**. Erzeugen in PowerShell:

```powershell
[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('DAS-SECRET'))
```

Gegenprobe (zeigt es wieder im Klartext):

```powershell
[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('REFTLVNFQ1JFVA=='))
```

Das Klartext-Feld `secret` funktioniert weiterhin. Sind beide gesetzt, gewinnt
`secretBase64` und `secret` wird ignoriert (mit Hinweis im Log).

Tippt sich jemand bei der Kodierung — oder pastet den Klartext versehentlich ins
Base64-Feld — startet die App nicht stumm mit kaputten Zugangsdaten, sondern
meldet es in der Statusleiste samt PowerShell-Zeile zum Erzeugen. Das ist
deshalb geprüft, weil der Server sonst nur „401 Wrong credentials" sagt und man
lange am falschen Ende sucht.

### ⚠️ Base64 ist Maskierung, keine Verschlüsselung

Base64 ist mit einem Einzeiler wieder Klartext. Es verhindert das Mitlesen beim
Überfliegen der Datei — mehr nicht, und mehr geht hier auch nicht: die Datei
wird mit der Exe an viele Leute verteilt, DPAPI scheidet damit aus
(user-gebunden), und ein Schlüssel im Binary wäre genauso trivial auslesbar.

**Deshalb gilt: der in `viewer.json` hinterlegte Automation-User muss in Checkmk
eine reine Lese-Rolle haben.** Die ausgeblendete Oberfläche ist Bedienkomfort,
kein Zugriffsschutz — wer die Datei lesen kann, kann auch `curl` gegen die
REST-API werfen. Ist die Rolle richtig gesetzt, ist das unkritisch: mehr als
Gucken geht mit diesen Zugangsdaten ohnehin nicht.

### Beispiel

Vorlage im Repo: [`viewer.example.json`](viewer.example.json) — kopieren,
ausfüllen, als `viewer.json` neben die Exe legen.

```json
{
  "title": "Checkmk — Sicht Fachbereich 42",
  "connection": {
    "host": "cmk.lhp.intern",
    "site": "LHP-Prod",
    "username": "cockpit_viewer",
    "secretBase64": "REFTLUFVVE9NQVRJT04tU0VDUkVU",
    "useHttps": true,
    "authMode": "AutomationBearer"
  },
  "columns": [
    "state_dot", "host", "service_display_name", "service_description",
    "service_state", "svc_check_age", "svc_state_age"
  ],
  "view": {
    "filterName": "Fachbereich 42",
    "hostRegex": ".*(sql|ora).*",
    "onlyProblems": true,
    "autoRefresh": true,
    "refreshSeconds": 60
  }
}
```

### Verfügbare Spalten

Die Schlüssel sind die Namen aus den Checkmk-Sichten — eine vorhandene Web-Sicht
lässt sich damit abschreiben. Reihenfolge in der Liste = Reihenfolge im Grid.

| Schlüssel | Spalte |
|---|---|
| `state_dot` | Ampelpunkt (Cockpit-eigen) |
| `host` | Hostname |
| `host_alias` | Host-Alias (Cockpit-eigen) |
| `service_display_name` | Anzeigename des Service (sonst = Beschreibung, siehe unten) |
| `service_description` | Service-Beschreibung |
| `service_state` | OK / WARN / CRIT / UNKNOWN |
| `service_plugin_output` | Prüfausgabe (nimmt den Restplatz ein) |
| `svc_acknowledged` | Ack-Häkchen |
| `svc_in_downtime` | Wartungs-Häkchen |
| `svc_check_age` | Zeit seit dem letzten Check |
| `svc_state_age` | Zeit seit der letzten Statusänderung (eingefärbt nach Frische) |

Ein unbekannter Schlüssel wird ignoriert und ins Log geschrieben; bleibt gar
nichts übrig, greift der Standardsatz.

#### `service_display_name` — wo der sprechende Name herkommt

Bei den meisten Diensten ist der Anzeigename mit der Service-Beschreibung
identisch. Interessant wird die Spalte bei SNMP-Geräten, die ihre Kanäle selbst
benennen — etwa einem Rittal CMC III:

| `service_description` | `service_display_name` |
|---|---|
| `CMCIII-IO3 Input 1` | `USV Netzausfall (Input 1)` |
| `CMCIII-IO3 Input 3` | `USV Batterie low (Input 3)` |
| `CMCIII-IO3 Input 6` | `NSV Diffstr Warnung (Input 6)` |
| `Uptime` | `Uptime` |

Es ist derselbe Wert, den die Checkmk-Weboberfläche in der Spalte „Display name"
zeigt (Livestatus-Spalte `display_name`).

> **Achtung beim Sortieren:** Wenn du nach Anzeigename sortierst, landen die
> sprechenden Namen dort, wo ihr Anfangsbuchstabe hingehört — `USV …` unter U,
> also weit hinter `CMCIII-…` und `Filesystem …`. Oben im Bild stehen dann nur
> Dienste, bei denen beide Spalten gleich sind. Das sieht schnell so aus, als
> würde die Spalte nicht funktionieren.

Der Freitext-Filter durchsucht den Anzeigenamen mit — die Eingabe `USV` findet
also die Zeilen, die man in der Spalte liest. Im CSV-Export ist er eine eigene
Spalte.

### `view` — der Filter kommt ausschließlich aus dem Profil

`hostRegex` bzw. `includeHosts` erscheinen als aktiver Filter unter dem Namen
aus `filterName`. **Die persönliche `filter.json` wird im Viewer-Modus weder
gelesen noch geschrieben** — sonst hingen auf dem Rechner, auf dem das Profil
gebaut wurde, die eigenen Favoriten mit im Dropdown, und der dort zuletzt
aktive Filter würde die Vorgabe überstimmen.

Lässt man `hostRegex` leer (oder weg), heißt das **alle Hosts** — der Filter
heißt dann trotzdem wie in `filterName`, ist aber ohne Einschränkung:

```json
"view": { "filterName": "Alles", "hostRegex": "", "onlyProblems": true }
```

`onlyProblems`, `onlyOpen`, `autoRefresh`, `refreshSeconds` und `treeView` sind
**Startwerte** — der Anwender darf sie umschalten, es wird nur nichts nach
`statusview.json` zurückgeschrieben, damit jeder Start wieder mit der
vorgesehenen Sicht beginnt.

### `popUpOnProblem` — Fenster springt bei Problemen auf

Für Ausgaben, die dauerhaft auf einem Bildschirm laufen (Wachschutz, Leitstand),
reicht ein Toast oft nicht. Mit `"popUpOnProblem": true` (Standard im
Viewer-Modus) holt sich das Cockpit bei einer **Verschlechterung** selbst nach
vorn: Fenster maximiert, ganz oben im Stapel, und die betroffene Zeile ist
markiert. Kommt die App aus dem Tray, wird sie dabei wieder eingeblendet.

Was es auslöst:

| Ereignis | Fenster springt auf |
|---|---|
| OK → WARN/CRIT/UNKNOWN (neues Problem) | ja |
| WARN → CRIT (Verschlechterung) | ja |
| CRIT → OK (Recovery) | **nein** |
| Snooze aktiv (Tray-Menü) | **nein** |

Sind mehrere Dinge gleichzeitig kaputtgegangen, springt die Ansicht auf das
schwerwiegendste. Abschalten mit `"popUpOnProblem": false` — der Toast bleibt
davon unberührt.

> Windows gibt einer Hintergrund-Anwendung nicht immer den Tastaturfokus. Das
> Fenster wird zuverlässig sichtbar und nach ganz oben geholt; ob es zusätzlich
> den Fokus bekommt, entscheidet Windows.

### `map` — Standortkarte im Kiosk

Für den Bildschirm im Leitstand oder beim Wachschutz: eine Stadtkarte, auf der
jeder Standort grün, gelb oder rot ist. Ohne diesen Abschnitt bleibt es beim
reinen Status-Tab.

```jsonc
"map": {
  "show": true,                    // ohne das bleibt der Bereiche-Tab weg
  "area": "Stadthaus",             // Startbereich per Name; leer = ganze Stadt
  "zoom": 16,                      // 0 = automatisch einpassen
  "layer": "Topographisch grau",   // Name aus den Kartenquellen
  "tree": false                    // Baum links weg -> reine Kartenwand
}
```

**Der Tab ist rein lesend.** Anlegen, Umbenennen, Löschen, Zuweisen, Zeichnen
und Importieren sind im Viewer-Modus ohnehin nicht da; dieser Abschnitt schaltet
nichts davon frei.

`area` ist der **Name** des Bereichs, nicht seine Nummer — die steht nirgends,
wo du sie ablesen könntest. Gibt es den Namen nicht, bleibt die Karte auf der
Gesamtübersicht und schreibt eine Zeile ins Log.

Die Werte sind **Startwerte**. Wer vor dem Bildschirm steht, darf schieben und
zoomen; nach einem Neustart steht wieder die vorgesehene Sicht da.

> Welche Hosts die Ampel eines Standorts bestimmen, entscheidet weiterhin allein
> der Filter aus `view`. Derselbe Serverraum ist für das DB-Team grün und für den
> Wachschutz rot, wenn die USV Netzausfall meldet.

### Log beim Start

Welcher Filter und welche Spalten tatsächlich gegriffen haben, steht im Log:

```
INFO|StatusView|Viewer-Spalten gesetzt (7):  | Host | Anzeigename | Service | Status | …
INFO|StatusViewModel|Viewer-Vorgabe aktiv: Filter 'Alles' (Regex=—, 0 Hosts explizit),
NurProbleme=true, NurOffen=false, AutoRefresh=true/60s.
```

Und auf Debug-Ebene bei jedem Refresh, warum ein Toast/Popup kam oder eben nicht:

```
DEBUG|TrayController|Refresh-Diff: 3 Services (CRIT 1/WARN 0/UNK 0/OK 2),
Aenderungen=1 (1 neues Problem), ImTray=false, Snooze=aus, PopUp=true.
INFO |TrayController|Viewer-Modus: Verschlechterung erkannt (1 neues Problem) —
hole Fenster nach vorn und springe auf TUER-CTRL02/Zutrittskontrolle.
```

| Feld | Default | Bedeutung |
|---|---|---|
| `filterName` | `Vorgabe` | Name des vorgewählten Filters in der ComboBox |
| `hostRegex` | — | Host-Regex (case-insensitive) |
| `includeHosts` | `[]` | explizite Hostliste; hat Vorrang vor `hostRegex` |
| `filterText` | `""` | Freitext-Filter |
| `onlyProblems` | `true` | „Nur Probleme" vorbelegen |
| `onlyOpen` | `false` | „Nur offen" vorbelegen |
| `autoRefresh` | `true` | Auto-Refresh an |
| `refreshSeconds` | `60` | Intervall |
| `treeView` | `false` | mit Baumansicht statt Tabelle starten |

### Wenn die Datei kaputt ist

Ein Tippfehler im JSON schaltet den Viewer-Modus **nicht** ab — sonst hätte
jemand, der nur gucken soll, nach einem Syntaxfehler plötzlich die volle
Oberfläche. Stattdessen startet die App im Viewer-Modus ohne Verbindung und
schreibt in die Statusleiste, welche Datei betroffen ist. Details stehen im Log.

---

## 19. Zentrale Einstellungen in der Datenbank

Seit v1.9 liegen die Daten, die **alle** Cockpit-Nutzer teilen, in der Datenbank
`CheckMK_Copilot` auf **FOC-SQL01** statt in JSON-Dateien auf dem Fileshare.

### Warum der Umzug

Der Fileshare hatte zwei Probleme, die im Alltag beide auftraten:

- **Zugriff.** Lesen durften alle, schreiben nur wenige. Wer eine Domain-Zuordnung
  korrigieren wollte, konnte es schlicht nicht.
- **Gleichzeitiges Speichern.** Beim Speichern wurde die *komplette* `hosts.json`
  zurückgeschrieben. Zwei Leute gleichzeitig, und der Eintrag des Ersten war
  lautlos weg — ohne Fehlermeldung, ohne dass es jemandem auffiel.

In der Datenbank ist jede Zuordnung eine eigene Zeile. Ein Änderung fasst nur
diese Zeile an, und in `ChangedBy`/`ChangedAtUtc` steht, wer sie zuletzt
angefasst hat.

### Für dich ändert sich nichts

Der Umzug passiert beim ersten Start automatisch: Ist die Tabelle noch leer,
übernimmt das Cockpit die vorhandene `hosts.json` einmalig. Danach ist die
Datenbank die Wahrheit und die Datei wird nie wieder gelesen.

Kein Fileshare-Zugriff mehr nötig — außer für die alte `bootstrap.json`, solange
sie noch existiert.

### Wenn FOC-SQL01 nicht erreichbar ist

Das Cockpit **startet trotzdem**. Nach jedem erfolgreichen Lesen legt es eine
Kopie der zentralen Einstellungen unter
`%APPDATA%\Kroste\Checkmk\globals-cache.json` ab und arbeitet damit weiter. In
der Statusleiste erscheint dann ein Hinweis:

> **Zentral: Cache**

Das ist kein Fehler, sondern eine Ansage: Du arbeitest mit dem letzten bekannten
Stand. Wenn ein Kollege gerade zentral etwas geändert hat, siehst du es noch
nicht. Sobald die Datenbank wieder da ist, verschwindet der Hinweis beim nächsten
Start von selbst.

Gibt es weder Datenbank noch Cache (frisch installierter Rechner ohne
Verbindung), läuft das Cockpit mit eingebauten Vorgaben — alle Alltagsfunktionen
bleiben nutzbar, nur die zentralen Vorgaben fehlen.

### Für Administratoren

**Schema anlegen oder aktualisieren:** Die Skripte in [`db/`](db/) mit dem
Konto `CheckMK_Copilot_SA` (db_owner) in der Reihenfolge ihrer Nummerierung
ausführen. Sie sind idempotent, ein zweiter Lauf schadet nicht. Das Cockpit
migriert **nicht** selbst — es prüft nur die Version und meldet, wenn sie nicht
passt.

**Zwei Konten, mit Absicht:**

| Konto | Rechte | Wer benutzt es |
|---|---|---|
| `CheckMK_Copilot_SA` | `db_owner` | nur der Administrator, nur für die Skripte |
| `CheckMK_Copilot_Worker` | `db_datareader` + `db_datawriter` | die ausgelieferte Anwendung |

Die Anwendung braucht zur Laufzeit kein `db_owner`. Da der Verbindungsstring auf
rund 50 Arbeitsplätzen liegt, entscheidet allein dieses Recht, was jemand
anrichten kann, der ihn ausliest: Zeilen ändern ja, Tabellen löschen nein.

**Verbindung ausrollen:** `database.json` neben die Exe legen. Erzeugt wird sie
mit

```
Checkmk.App.exe --protect-db "Server=FOC-SQL01;Database=CheckMK_Copilot;User Id=CheckMK_Copilot_Worker;Password=…;Encrypt=True;TrustServerCertificate=True"
```

`TrustServerCertificate=True` gehört dazu, wenn FOC-SQL01 ein selbstsigniertes
Zertifikat hat — sonst bricht der erste Verbindungsversuch mit einer Meldung ab,
die nach einem Passwortproblem aussieht.

> ⚠️ **Der Wert in `database.json` ist verschleiert, nicht geschützt.** Der
> Schlüssel steckt im Programm, das daneben liegt. Das verhindert, dass ein
> Passwort im Klartext in Backups und Ticketanhängen landet — es hält niemanden
> auf, der es darauf anlegt. Wirksam ist allein das Recht des Laufzeitkontos.
> Bitte nicht als „das Passwort ist ja verschlüsselt" weitererzählen.

Technische Details und das Schema: [`db/README.md`](db/README.md).

---

## 20. Bereiche: welcher Standort hat gerade ein Problem

Der Tab **„Bereiche"** ordnet Hosts ihrem physischen Standort zu und zeigt je
Standort einen Ampelpunkt: **schlechtester Status der Hosts, die dort stehen.**
Er erscheint nur, wenn die zentrale Datenbank erreichbar ist — Bereiche leben
dort.

### Der Baum

Bereiche sind verschachtelt, weil Stadtsicht und Gebäudesicht dasselbe auf zwei
Zoomstufen sind:

```
● ZR2                    14 Hosts · 1 Problem
  ● Serverraum 3          8 Hosts
  ● Serverraum 4          6 Hosts · 1 Problem
● Stadthaus              23 Hosts
● Ohne Bereich          412 Hosts · 7 Probleme
```

Der Status rollt nach oben durch: Ist ein Host in Serverraum 4 kritisch, wird
auch ZR2 rot. So sieht man auf der obersten Ebene, wo es brennt, und klappt sich
nach unten durch.

**„Ohne Bereich"** ist kein echter Bereich, sondern die Restmenge — alle Hosts,
die noch nirgends zugeordnet sind. Das ist die Arbeitsliste: Solange dort etwas
steht, ist die Zuordnung nicht fertig. Und es ist der einzige Weg, einen
vergessenen Host zu bemerken; sonst taucht er schlicht nirgends auf.

Ein **grauer Punkt** heißt „diesem Bereich sind keine Hosts zugeordnet" — bewusst
nicht grün, denn ein leerer Bereich ist nicht dasselbe wie ein gesunder.

### Hosts zuordnen

Zwei Wege, je nachdem wo du gerade bist:

- **Aus dem Status-Tab**: Zeilen markieren (Ctrl/Shift wie gewohnt) →
  Rechtsklick → **„Bereich zuweisen…"**. Der Alltagsweg, wenn du die Hosts
  ohnehin vor dir hast.
- **Aus dem Bereiche-Tab**: Bereich markieren → Rechtsklick → **„Hosts
  zuweisen…"**. Öffnet die Liste der noch nicht zugeordneten Hosts mit
  Freitextfilter und „Alle sichtbaren" — gedacht für den ersten Durchgang, wenn
  ein paar hundert Geräte zu verteilen sind.

Ein Host gehört zu **genau einem** Bereich — ein Gerät steht an einem Ort. Wer
es umträgt, ändert eine Zeile, und alle sehen es.

### Hosts automatisch vorschlagen lassen

Über tausend Geräte einzeln zuzuordnen wäre eine Woche Arbeit. Es gibt zwei
Wege, das abzukürzen — der erste ist deutlich besser, wo er zur Verfügung steht.

#### Weg 1: der Checkmk-Ortstag (Schulen)

Eure Schul-Hosts tragen in Checkmk das Attribut `tag_location_school` mit Werten
wie `schule_46` — **553 von 654 Hosts**. Das ist im Setup gepflegt und damit
verlässlicher als alles, was sich aus dem Hostnamen ableiten lässt.

Ein Klick auf **„Tags zuordnen…"** in der Toolbar gleicht diese Tags über die
Standortnummer gegen die Bereiche ab und zeigt dir das Ergebnis zum Durchsehen.
Bestätigen — fertig. Danach ordnet **„Zuordnung vorschlagen…"** die Hosts zu.

Damit das funktioniert, brauchen die Bereiche die Standortnummer, die beim
Standort-Import mitkommt. Wurden deine Schulen **vor** dieser Version importiert,
fehlt sie: **Standorte einmal erneut übernehmen**, dann ist sie da.

#### Weg 2: das Namensmuster (alles andere)

Auf der Site LHP gibt es solche Tags praktisch nicht, dort steht die Information
im Namen: Schule 46 hat `46-SW04`, `46-USV`, `NAS46-01`, `PA46-01`, `ESX46-02`,
`iRMC-46`. Dafür trägt jeder Bereich ein **Host-Muster** (Rechtsklick →
**„Host-Zuordnung…"**). Bei den Schulen ist es aus der Schulnummer vorbelegt.

Der Dialog zeigt beides nebeneinander und **sofort, welche Hosts treffen
würden** — du siehst also direkt, ob es stimmt. Das Muster greift nur bei Hosts
**ohne** passenden Tag, deshalb steht die Trefferzahl auch nur für diese.

Hier trägst du auch die beiden Schulen nach, die im offenen Schulverzeichnis
fehlen: **`schule_61`** (28 Hosts) und **`schule_63`** (10 Hosts). Bereich
anlegen, Tag aus der Liste wählen, speichern.

#### Und dann zuordnen

**„Zuordnung vorschlagen…"** zeigt jeden Treffer mit Zielbereich und Notiz:

| Notiz | heißt |
|---|---|
| *neu (Tag)* | Host ist noch nirgends zugeordnet, erkannt am Checkmk-Ortstag |
| *neu (Muster)* | dasselbe, aber aus dem Hostnamen erschlossen |
| *verschiebt von …* | Host steht schon woanders |
| *mehrdeutig* | Mehrere Bereiche passen — hier musst du entscheiden |

**Vorausgewählt sind nur die eindeutigen neuen.** Verschiebungen und
Mehrdeutigkeiten musst du bewusst ankreuzen. Zugeordnet wird erst mit
„Zuordnen" — nichts passiert ungefragt.

> **Warum der Tag gewinnt, wo beides etwas sagt:** Das Muster erschließt die
> Nummer aus dem Namen und irrt dabei. `29-SW11` landete bei der Grundschule
> Bornim, weil dort eine 11 steht; `PA04-1` beim Humboldt-Gymnasium wegen der
> abschließenden 1. **28 solcher Fehlgriffe** räumt der Tag aus, und er erfasst
> 85 Geräte zusätzlich, deren Name die Nummer gar nicht als Zahl enthält
> (`WLC-01SL-01`).

### Wenn ein Standort aufgelöst wird

Rechtsklick auf den Bereich → **„Technik verschieben nach…"** nimmt **alle**
Hosts mit. Genau der Fall, wenn Haus 2 aufgelöst wird und die Technik in den
Container wandert: einmal auswählen statt zwölf Hosts einzeln umzuhängen.

Das geht in beide Richtungen und beliebig oft — kommt die Technik später zurück
oder zieht ganz woanders hin, ist es derselbe Handgriff. Der leere Bereich lässt
sich danach löschen (das geht erst, wenn keine Technik mehr drinsteht) oder für
den Rückzug stehen lassen.

### Bereiche pflegen

**„Neuer Bereich…"** legt einen auf oberster Ebene an, **„Unterbereich…"**
unterhalb des markierten. **„Löschen"** geht nur, wenn der Bereich weder
Unterbereiche noch zugeordnete Hosts hat — sonst sagt die Statuszeile, was noch
drin steckt. Das ist Absicht: Ein Löschen, das stillschweigend Zuordnungen
mitnimmt, fällt erst Wochen später auf.

### Warum dasselbe Zimmer für dein Team eine andere Farbe hat

Die Ampel bezieht sich immer auf den **aktiven Host-Filter**. Steht im
Serverraum 3 sowohl ein Datenbankserver als auch die USV, dann ist derselbe Raum
für das DB-Team grün und für den Wachschutz rot, wenn die USV Netzausfall meldet.

Das ist kein Fehler, sondern der Kern: Der Ort wird **einmal** gezeichnet und ein
Gerät **einmal** zugeordnet — was ein Team davon sieht, entscheidet sein Filter.
Andernfalls müsste jedes Team seine eigene Zuordnung pflegen, und wer einen
Switch umträgt, müsste es acht Teams sagen.

### Die Karte

Rechts neben dem Baum liegt die Karte — ein Luftbild von Potsdam, auf dem die
Bereiche als farbige Flächen liegen. Die Farbe ist dieselbe Ampel wie im Baum.

- **Schieben** mit gedrückter linker Maustaste, **Zoomen** mit dem Mausrad
  (der Punkt unter dem Zeiger bleibt stehen).
- **Klick auf eine Fläche** markiert den Bereich im Baum. Liegt ein kleiner
  Bereich in einem großen, gewinnt der kleine.
- **Klick im Baum** springt umgekehrt auf die Fläche.

### Punkt oder Fläche

Ein Bereich braucht keine gezeichnete Fläche. Die meisten Standorte sind auf
einer Stadtkarte sinnvoll ein **Marker** — ein Fähnchen mit der Ampelfarbe. Eine
Fläche lohnt nur da, wo es auf den Umriss ankommt: ein Campus mit mehreren
Serverräumen etwa.

Hat ein Bereich beides, wird die Fläche gezeichnet. Ein Klick trifft immer
zuerst den Marker — er ist klein und liegt oft mitten in einer größeren Fläche.

**Eine Fläche einzeichnen:** Bereich im Baum markieren → **„Fläche zeichnen"** →
Ecken nacheinander anklicken. **Doppelklick** oder **Enter** schließt die Fläche,
**Rücktaste** nimmt den letzten Punkt zurück, **Esc** bricht ab. Gespeichert wird
sofort.

**Eine Fläche nachbessern:** Liegt eine Ecke daneben, musst du nicht alles neu
zeichnen. Bereich markieren → **„Fläche bearbeiten"** (der Knopf erscheint nur,
wenn es eine Fläche gibt):

| Handgriff | Wirkung |
|---|---|
| Griff ziehen | Ecke verschieben |
| Kantenmitte anklicken | neue Ecke einfügen — gleich weiterziehen |
| Rechtsklick auf einen Griff, oder **Entf** | Ecke entfernen |
| **Enter** | übernehmen |
| **Esc** | verwerfen, alles bleibt wie es war |

Die Karte lässt sich dabei weiter schieben. Unter drei Ecken geht es nicht — aus
einer Fläche würde sonst eine Linie, die man weder sieht noch anklicken kann.

### Rechtsklick auf der Karte

Auf einer Fläche oder einem Marker öffnet der Rechtsklick dasselbe Menü wie im
Baum: **Hosts zuweisen**, **Technik verschieben**, **Host-Zuordnung**, **Fläche
bearbeiten**, **Kartenhintergrund** und **Umbenennen**. Du musst den Standort
also nicht erst im Baum wiederfinden.

Die Ansicht springt dabei **nicht** — du siehst die Fläche ja gerade.

### Eigener Hintergrund je Bereich

Auf der Campus-Ebene ist die Liegenschaftskarte brauchbar, auf der
Stadtübersicht unlesbar. Deshalb kann ein Bereich seinen eigenen Hintergrund
mitbringen: Rechtsklick → **„Kartenhintergrund…"**. Markierst du den Bereich,
schaltet die Karte automatisch um; verlässt du ihn, gilt wieder deine Auswahl
aus der Toolbar. **„(Vorgabe)"** entfernt die Bindung.

### Standorte übernehmen statt tippen

Der Knopf **„Standorte übernehmen…"** holt fertige Standortlisten vom
Kartenserver der Landeshauptstadt. Drei stehen zur Wahl:

| Liste | Inhalt |
|---|---|
| **Verwaltungsstandorte** | 35 Dienstgebäude mit Behörde und Anschrift |
| **Schulen** | 82 Schulen mit Schulform und Träger — die Site `Schul_IT` |
| **Hochschulen** | 11 Standorte |

Du wählst erst die Liste **und die Sites**, dann die Einträge; Filter und „Alle
sichtbaren" helfen beim Eingrenzen.

**Für welche Site?** Im ersten Dialog hakst du an, in welchen Checkmk-Sites die
neuen Bereiche erscheinen sollen — die aktive ist vorausgewählt. Schulen gehören
typischerweise zu `Schul_IT`, Dienstgebäude zu `LHP`, und beides gleichzeitig ist
erlaubt: In einem Haus kann Technik aus beiden Sites stehen.

Nichts angehakt heißt **überall sichtbar**. Werden die Sites irgendwann
zusammengeführt, nimmt man die Einschränkung einfach wieder heraus — die
Bereiche und alle Host-Zuordnungen bleiben, wie sie sind.

Die importierten Standorte landen auf **oberster Ebene**. Ist im Baum etwas
markiert, bietet der Dialog an, sie stattdessen darunter einzuhängen — das musst
du aber ausdrücklich ankreuzen.

### Sichtbarkeit nachträglich ändern

Rechtsklick auf einen Bereich → **„Sichtbar in Sites…"**. Dort lässt sich
jederzeit korrigieren, in welchen Sites er erscheint.

Das brauchst du vor allem für Bereiche, die du **von Hand angelegt hast, bevor
es die Site-Zuordnung gab** — die haben keine und erscheinen deshalb in jeder
Site. Neu angelegte Bereiche bekommen automatisch die Site, in der du gerade
arbeitest.

Nichts ist vorausgewählt: In den Listen stehen auch Bibliotheken und das Museum,
und alles zu übernehmen macht den Bereichsbaum unbrauchbar.

Jede Liste wird getrennt abgeglichen — ein späterer Lauf der Schulen stört die
schon übernommenen Verwaltungsstandorte nicht.

**Doppelte Namen** lösen sich von selbst auf: „Musikschule" gibt es in Potsdam
zweimal, daraus wird „Musikschule" und „Musikschule (Galileistraße 6)".

Ist im Baum ein Bereich markiert, kommen die Standorte **darunter** — so lassen
sich „Außenstellen" gebündelt einhängen, statt dreißig Wurzelknoten zu erzeugen.

Ein zweiter Lauf erzeugt **keine Dubletten**: Bekannte Standorte werden
abgeglichen, verschobene bekommen die neue Koordinate. Ein Bereich, den du
umbenannt hast — „Stadthaus" statt der amtlichen Bezeichnung — behält seinen
Namen.

Bereiche ohne Fläche verschwinden nicht — sie stehen im Baum und funktionieren
dort vollständig. Die Karte ist eine zusätzliche Sicht, keine Voraussetzung.

### Kartenhintergrund wechseln

Oben rechts das Auswahlfeld **„Karte:"** — sechs amtliche Hintergründe:

| | wofür |
|---|---|
| **Luftbild** | Orthophoto 20 cm. Man erkennt Gebäude und Wege, aber eingefärbte Flächen sind auf buntem Untergrund schwerer zu lesen. |
| **Stadtplan** | basemap.de mit Straßennamen und Beschriftung — am besten zum Wiederfinden von Adressen. |
| **Topographisch grau** | DTK 1:10.000 in Graustufen. Hier tritt die Ampelfarbe der Bereiche am deutlichsten hervor. |
| **Luftbild grau** | Orthophoto in Graustufen — Luftbild-Detail ohne Farbkonkurrenz. |
| **Liegenschaftskarte** | ALKIS: Flurstücke mit Nummern, Gebäudeumringe, Hausnummern. |
| **Stadtkarte Potsdam** | Die eigene Stadtkarte 1:500 der Landeshauptstadt — Gebäudeumringe, Höfe, Wege, Treppen. Die detaillierteste Grundlage, wenn es um einzelne Gebäude auf einem Gelände geht. |

Die ersten fünf kommen von der LGB Brandenburg, die letzte vom Kartenserver der
Landeshauptstadt (`geoportal.potsdam.de`).

Die Wahl wird pro Benutzer gemerkt. Der Kachel-Cache ist je Hintergrund
getrennt, ein Wechsel mischt also nichts.

### Karten vorladen — und offline arbeiten

Eine Kachel, die noch nie geholt wurde, dauert gut **eine Sekunde**; aus dem
Zwischenspeicher **acht Millisekunden**. Ein Bildschirm sind rund ein Dutzend
Kacheln — deshalb fühlt sich der *erste* Blick auf einen Standort zäh an und
jeder danach sofort.

Der Knopf **„Karten vorladen"** erledigt das im Voraus: Er holt die
Stadtübersicht und die Umgebung aller Standorte im Hintergrund. Ein zweiter
Klick bricht ab, das schon Geladene bleibt.

| Standorte | Kacheln | Dauer | Platz |
|---|---|---|---|
| 35 | ~1.000 | ~10 min | ~100 MB |
| 80 | ~2.300 | ~23 min | ~220 MB |

(Luftbild; die Graustufenkarte braucht etwa ein Viertel davon. Jeder
Kartenhintergrund wird getrennt vorgeladen.)

**Danach funktioniert die Standort-Sicht auch ohne Internet.** Die Karte
zeichnet aus dem Zwischenspeicher; nur Ausschnitte, die noch nie jemand
angesehen hat, bleiben leer.

Die Kacheln altern still: Was älter als ein halbes Jahr ist, wird beim nächsten
Anzeigen im Hintergrund erneuert — angezeigt wird immer sofort der vorhandene
Stand. Orthophotos werden jährlich beflogen, häufiger lohnt es nicht.

> **Für Administratoren — einmal laden statt 48-mal:** Trägt man in
> `dbo.GlobalSetting` unter `MapTileSharePath` einen Ordner ein (etwa auf dem
> Fileshare), lesen alle Clients zuerst von dort. Wer Schreibrecht hat, füllt
> ihn im Vorbeigehen mit. So zahlt der Erste die Wartezeit und alle anderen
> lesen — und der Landesdienst bekommt nicht 48-mal dieselbe Anfrage. Ist der
> Ordner nicht erreichbar, wird er stillschweigend übergangen.
> `MapTileMaxAgeDays` steuert das Auffrischen (Vorgabe 180, `0` = nie).

Das Kartenmaterial sind amtliche Geobasisdaten der **LGB Brandenburg**, Open
Data unter dl-de/by-2.0. Der Quellenvermerk unten rechts im Kartenbild ist
Lizenzpflicht und bleibt deshalb stehen. Zwischengespeichert wird lokal unter
`%LOCALAPPDATA%\Kroste\Checkmk\tiles` — ein Wandmonitor befragt den
Landesdienst also nicht stundenlang.

> **Für Administratoren:** Die Auswahlliste steht in `dbo.GlobalSetting` unter
> `MapLayers` (JSON: Name, Url, Layer), der Quellenvermerk unter
> `MapAttribution`. Ein zusätzlicher Hintergrund — etwa ein eigener
> WMS mit Gebäudeplänen — ist damit ein `UPDATE` auf eine Zeile, kein neues
> Ausrollpaket. Anlegen mit [`db/seed-map-settings.sql`](db/seed-map-settings.sql);
> ohne die Zeilen greifen dieselben Werte als eingebaute Vorgabe.

---

## Was ist bewusst *nicht* enthalten

- **Kein Checkmk-Setup** — das Tool spricht mit einem vorhandenen Checkmk 2.5.
- **Kein Ersatz für das Webinterface** — deckt Alltagshandgriffe ab, nicht die
  selteneren Sachen (Rollen, Regeln, Notifications, Reports, Event-Console).
- **Kein automatisches Selbst-Update** — nur der Hinweis auf neue Versionen.
- **Kein SSO** — der Checkmk-Server hat keine Kerberos/SPNEGO-Konfig, deshalb
  meldest du dich einmal mit deinem AD-Passwort an (jährliche Rotation).
- **Kein DB-Health-Board als eigener Tab** — der Filter mit Regex oder
  Include-Liste deckt das ab (Favorit „DB-Server" anlegen).
