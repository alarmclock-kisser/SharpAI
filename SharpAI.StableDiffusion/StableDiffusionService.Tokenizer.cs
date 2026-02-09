using SharpAI.Core;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SharpAI.StableDiffusion
{
    public partial class StableDiffusionService
    {
        private Dictionary<string, int> _vocab = new();
        private Dictionary<string, string> _merges = new();
        private const int MaxTokenLength = 77;

        public async Task LoadTokenizerAsync(IProgress<double>? progress = null)
        {
            if (this._config == null)
            {
                await StaticLogger.LogAsync("No model set for tokenizer loading.");
                return;
            }

            // 1. Vocab laden
            var vocabJson = await File.ReadAllTextAsync(this._config.TokenizerVocabPath);
            this._vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(vocabJson)
                     ?? throw new Exception("Vocab could not be deserialized.");

            await StaticLogger.LogAsync($"Tokenizer loaded with {this._vocab.Count} tokens.");
            progress?.Report(1.0);
        }

        public async Task<int[]> TokenizeAsync(string text, IProgress<double>? progress = null)
        {
            // Sicherheitscheck: Ist das Vokabular überhaupt geladen?
            if (this._vocab == null || this._vocab.Count == 0)
            {
                await StaticLogger.LogAsync("WARNING: Tokenizer vocab is empty! Call LoadTokenizerAsync first.");
                // Wir versuchen es trotzdem zu laden, falls möglich
                if (this._config != null) await this.LoadTokenizerAsync();
            }

            return await Task.Run(() =>
            {
                int startToken = this.GetTokenId("<|startoftext|>");
                int endToken = this.GetTokenId("<|endoftext|>");

                var cleanedText = text.ToLower().Replace(",", " , ").Replace(".", " . ").Trim();
                var words = Regex.Split(cleanedText, @"\s+").Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
                var tokens = new List<int> { startToken };

                foreach (var word in words)
                {
                    var wordWithSuffix = word + "</w>";
                    if (this._vocab.TryGetValue(wordWithSuffix, out int id)) tokens.Add(id);
                    else if (this._vocab.TryGetValue(word, out id)) tokens.Add(id);
                    else
                    {
                        // Fallback auf Zeichenebene
                        foreach (var c in word)
                        {
                            if (this._vocab.TryGetValue(c.ToString(), out int charId)) tokens.Add(charId);
                        }
                        if (this._vocab.TryGetValue("</w>", out int suffixId)) tokens.Add(suffixId);
                    }
                }

                tokens.Add(endToken);
                var finalTokens = tokens.Take(MaxTokenLength).ToList();
                while (finalTokens.Count < MaxTokenLength) finalTokens.Add(endToken);

                return finalTokens.ToArray();
            });
        }

        private int GetTokenId(string key)
        {
            if (this._vocab.TryGetValue(key, out int id)) return id;

            // Säuberung für die Suche (Klammern entfernen)
            var cleanKey = key.Replace("<|", "").Replace("|>", "");
            if (this._vocab.TryGetValue(cleanKey, out id)) return id;

            // Fallback auf Standard CLIP IDs für Stable Diffusion 1.5
            if (key.Contains("start")) return 49406;
            if (key.Contains("end")) return 49407;

            return 49407; // Sicherer Default
        }

        public async Task DebugTokenizationAsync(string text)
        {
            var ids = await this.TokenizeAsync(text);
            // Reverse Vocab für die Anzeige erstellen
            var reverseVocab = this._vocab.ToDictionary(x => x.Value, x => x.Key);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"--- Tokenizer Debug for: \"{text}\" ---");

            foreach (var id in ids)
            {
                if (reverseVocab.TryGetValue(id, out var word))
                {
                    sb.AppendLine($"ID: {id.ToString().PadRight(6)} -> Token: {word}");
                }
                else
                {
                    // Fallback-Anzeige für IDs, die nicht im Vocab-Dictionary als Key stehen
                    string manualLabel = id == 49406 ? "<|startoftext|>" : (id == 49407 ? "<|endoftext|>" : "[UNKNOWN]");
                    sb.AppendLine($"ID: {id.ToString().PadRight(6)} -> Token: {manualLabel}");
                }
            }

            await StaticLogger.LogAsync(sb.ToString());
        }
    }
}