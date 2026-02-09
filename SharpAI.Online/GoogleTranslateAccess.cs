using System;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes; // Benötigt System.Text.Json (Standard in .NET)
using System.Threading.Tasks;
using System.Web; // Für HttpUtility.UrlEncode

namespace SharpAI.Online;

public static class GoogleTranslateAccess
{
    // Wir nutzen eine statische HttpClient Instanz (Best Practice)
    private static readonly HttpClient Http = new HttpClient();

    // Der "geheime" öffentliche Endpunkt (client=gtx wird oft von Extensions genutzt)
    private const string BaseUrl = "https://translate.googleapis.com/translate_a/single";

    static GoogleTranslateAccess()
    {
        // WICHTIG: Ein User-Agent faken, sonst blockt Google oft direkt ab.
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    public static async Task<string?> TranslateAsync(string text, string? originalLanguage, string translationLanguage = "en")
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        try
        {
            // Fallback auf "auto", wenn keine Quellsprache angegeben ist
            string sourceLang = string.IsNullOrWhiteSpace(originalLanguage) ? "auto" : originalLanguage;

            // URL Encoding ist extrem wichtig für Sonderzeichen
            string encodedText = HttpUtility.UrlEncode(text);

            // Parameter Erklärung:
            // client=gtx  : Der Client-Typ (Google Translate Extension)
            // sl          : Source Language
            // tl          : Target Language
            // dt=t        : Data Type = Text (wir wollen nur den Text zurück)
            // q           : Query (der Text)
            string url = $"{BaseUrl}?client=gtx&sl={sourceLang}&tl={translationLanguage}&dt=t&q={encodedText}";

            var response = await Http.GetAsync(url);

            // Wenn wir geblockt wurden (429) oder Fehler auftreten
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[GoogleTranslate Error] Status: {response.StatusCode}");
                return null;
            }

            // Der Rückgabewert ist ein verschachteltes JSON Array, kein schönes Objekt.
            // Struktur ca.: [[["Hallo Welt","Hello World",...], ["Wie gehts", ...]], ...]
            string jsonString = await response.Content.ReadAsStringAsync();

            return ParseJsonResult(jsonString);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Exception] {ex.Message}");
            return null;
        }
    }

    private static string? ParseJsonResult(string json)
    {
        try
        {
            // Wir nutzen JsonNode, da die Struktur dynamisch ist (gemischte Datentypen im Array)
            var node = JsonNode.Parse(json);

            // Das erste Element ist das Array mit den Sätzen
            var sentencesArray = node?[0]?.AsArray();

            if (sentencesArray == null)
            {
                return null;
            }

            var fullTranslation = new StringBuilder();

            // Google zerhackt lange Texte in mehrere Sätze/Segmente. Wir müssen sie wieder zusammensetzen.
            foreach (var sentenceNode in sentencesArray)
            {
                // Das erste Element jedes Satz-Arrays ist der übersetzte Text
                // [0] = translatedText, [1] = originalText
                string? segment = sentenceNode?[0]?.ToString();
                if (!string.IsNullOrEmpty(segment))
                {
                    fullTranslation.Append(segment);
                }
            }

            return fullTranslation.ToString();
        }
        catch
        {
            // Parsing fehlgeschlagen (API Format geändert?)
            return null;
        }
    }
}