/*
    009-filter-selfsubscribe.sql — Autoren abonnieren ihre eigenen
    veroeffentlichten Filter.

    Hintergrund: Bis Schema 8 sah ein Autor seine veroeffentlichten Filter
    IMMER in seiner Auswahl — die Abo-Tabelle galt nur fuer Fremde. Damit gab
    es keinen Weg, einen Filter fuer den Fachbereich zu pflegen, den man selbst
    nicht braucht, und "Loeschen" riss den Filter aus dem Katalog und damit aus
    den Auswahlen aller anderen.

    Ab jetzt entscheidet auch beim eigenen Filter das Abo. Damit dabei niemandem
    etwas aus der Auswahl verschwindet, bekommt jeder Autor eines bereits
    veroeffentlichten Filters hier sein Abo eingetragen — der Bestand sieht
    danach exakt aus wie vorher.

    Reines DATEN-Skript, kein DDL. Trotzdem ein Versionssprung: Die Anwendung
    verhaelt sich ohne diese Zeilen anders (Autoren verloeren ihre eigenen
    Katalog-Filter aus der Auswahl), und genau davor soll die Schema-Warnung
    schuetzen.

    Mit dem SA-Konto ausfuehren, nach 008-filter-target.sql. Idempotent.
*/

SET NOCOUNT ON;
GO

INSERT INTO dbo.HostFilterSubscription (HostFilterId, UserName, SubscribedAtUtc)
SELECT f.HostFilterId, f.OwnerUserName, SYSUTCDATETIME()
  FROM dbo.HostFilter AS f
 WHERE f.FachbereichId IS NOT NULL
   AND LEN(LTRIM(RTRIM(f.OwnerUserName))) > 0
   AND NOT EXISTS (SELECT 1
                     FROM dbo.HostFilterSubscription AS s
                    WHERE s.HostFilterId = f.HostFilterId
                      AND s.UserName     = f.OwnerUserName);

PRINT CONCAT('009: ', @@ROWCOUNT, ' Autoren-Abos ergaenzt.');
GO

/* Vollstaendigkeitswache: Bleibt ein veroeffentlichter Filter ohne das Abo
   seines Autors zurueck, wird die Version NICHT hochgesetzt — sonst faende der
   Autor ihn nach dem Update nicht mehr in seiner Auswahl und haette keinen
   Hinweis, woran es liegt. */
IF EXISTS (SELECT 1
             FROM dbo.HostFilter AS f
            WHERE f.FachbereichId IS NOT NULL
              AND LEN(LTRIM(RTRIM(f.OwnerUserName))) > 0
              AND NOT EXISTS (SELECT 1
                                FROM dbo.HostFilterSubscription AS s
                               WHERE s.HostFilterId = f.HostFilterId
                                 AND s.UserName     = f.OwnerUserName))
BEGIN
    RAISERROR(N'009-filter-selfsubscribe.sql UNVOLLSTAENDIG: es gibt veroeffentlichte Filter ohne Autoren-Abo. SchemaVersion wurde nicht hochgesetzt.', 16, 1);
END
ELSE
BEGIN
    UPDATE dbo.SchemaVersion
       SET Version = 9, AppliedAtUtc = SYSUTCDATETIME(), AppliedBy = SUSER_SNAME()
     WHERE Id = 1 AND Version < 9;

    PRINT '009-filter-selfsubscribe.sql angewendet (SchemaVersion = 9).';
END
GO
