using System.Text;
using Checkmk.Core.Models;

namespace Checkmk.App.Services;

/// <summary>
/// Erzeugt CSV aus der aktuellen Service-Ansicht. Semikolon-Delimiter + UTF-8-BOM
/// beim Schreiben => oeffnet in deutschem Excel per Doppelklick korrekt (inkl. Umlaute).
/// Quoting nach RFC 4180 (Felder mit Delimiter/Quote/Zeilenumbruch werden gequotet).
/// </summary>
public static class CsvExporter
{
    private const char Delimiter = ';';
    private const string NewLine = "\r\n";

    // „Anzeigename" gehoert dazu, seit die Tabelle ihn zeigen kann: bei SNMP-Geraeten
    // ist er der sprechende Name („USV Netzausfall (Input 1)"), die Service-Beschreibung
    // dagegen nur die technische Kennung („CMCIII-IO3 Input 1").
    private static readonly string[] Headers =
        ["Host", "Alias", "Anzeigename", "Service", "Status", "Ausgabe", "Ack", "Downtime", "Age"];

    public static string ToCsv(IEnumerable<ServiceStatus> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(Delimiter, Headers)).Append(NewLine);

        foreach (var s in rows)
        {
            sb.Append(Esc(s.HostName)).Append(Delimiter);
            sb.Append(Esc(s.HostAlias ?? string.Empty)).Append(Delimiter);
            sb.Append(Esc(s.DisplayNameOrDescription)).Append(Delimiter);
            sb.Append(Esc(s.Description)).Append(Delimiter);
            sb.Append(Esc(s.StateLabel)).Append(Delimiter);
            sb.Append(Esc(s.PluginOutput ?? string.Empty)).Append(Delimiter);
            sb.Append(s.IsAcknowledged ? "ja" : "nein").Append(Delimiter);
            sb.Append(s.InDowntime ? "ja" : "nein").Append(Delimiter);
            sb.Append(Esc(s.Age));
            sb.Append(NewLine);
        }

        return sb.ToString();
    }

    /// <summary>UTF-8-Bytes inkl. BOM (fuer Excel-Umlaute).</summary>
    public static byte[] ToCsvBytes(IEnumerable<ServiceStatus> rows)
    {
        var enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var bom = enc.GetPreamble();          // EF BB BF (GetBytes liefert die BOM nicht mit)
        var body = enc.GetBytes(ToCsv(rows));
        var result = new byte[bom.Length + body.Length];
        Buffer.BlockCopy(bom, 0, result, 0, bom.Length);
        Buffer.BlockCopy(body, 0, result, bom.Length, body.Length);
        return result;
    }

    private static string Esc(string value)
    {
        var needsQuote = value.Contains(Delimiter) || value.Contains('"')
                         || value.Contains('\n') || value.Contains('\r');
        return needsQuote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
