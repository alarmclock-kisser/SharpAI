using Markdig;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SharpAI.Client;
using SharpAI.Core;
using SharpAI.Shared;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SharpAI.WebApp.ViewModels;

public sealed class ChatViewModel
{
    private readonly ApiClient api;
    private readonly IJSRuntime js;
    private ElementReference messageContainer;
    private CancellationTokenSource? generationCts;
    private bool scrollPending;
    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();
    // basic sanitizer: removes dangerous tags and attributes (not a full HTML sanitizer)

    public ChatViewModel(ApiClient api, IJSRuntime js)
    {
        this.api = api;
        this.js = js;
    }


    public List<ChatMessageView> Messages { get; } = new();
    public string CurrentPrompt { get; set; } = string.Empty;
    public int MaxTokens { get; set; } = 512;
    public float Temperature { get; set; } = 0.7f;
    public float TopP { get; set; } = 0.95f;
    public bool StreamResponse { get; set; } = true;
    public bool Isolated { get; set; }
    public bool UseSystemPrompt { get; set; } = true;
    public bool IsGenerating { get; private set; }
    public bool? IsModelLoaded { get; private set; }
    public string? LoadedModelName { get; private set; }
    public string ContextLabel { get; private set; } = "Temporary";
    public string? ErrorMessage { get; private set; }
    public bool ShowImages { get; private set; }
    public List<ImageSelection> ImageSelections { get; } = new();
    public Func<Task>? NotifyStateChanged { get; set; }


    public bool LogKeysEvents { get; set; } = false;


    private bool FirstRender { get; set; } = true;


    public Task InitializeAsync()
    {
        return this.InitializeInternalAsync();
    }

    private async Task InitializeInternalAsync()
    {
        try
        {
            // Ensure the keystroke logger attaches focus/start handlers on the prompt field
            await this.js.InvokeVoidAsync("keystrokeLogger.attachFocusStart", "chatInput");
        }
        catch { }

        await this.RefreshAsync();
    }

    public void SetMessageContainer(ElementReference container)
    {
        this.messageContainer = container;
    }

    public async Task RefreshAsync()
    {
        if (this.FirstRender)
        {
            var appsettings = await this.api.GetAppsettingsAsync();
            this.MaxTokens = appsettings?.MaxResponseTokens ?? this.MaxTokens;
            this.Temperature = (float) (appsettings?.Temperature ?? this.Temperature);
            this.TopP = (float) (appsettings?.TopP ?? this.TopP);
            this.UseSystemPrompt = !string.IsNullOrEmpty(string.Join(" ", appsettings?.SystemPrompts.Select(p => p.Trim()) ?? []));

            if (!string.IsNullOrEmpty(appsettings?.DefaultContext))
            {
                await this.api.LoadContextAsync(appsettings.DefaultContext);
            }

            this.FirstRender = false;
        }

        this.LoadedModelName = (await this.api.GetModelStatusAsync())?.ModelName;
        this.IsModelLoaded = this.LoadedModelName != null;
        var context = await this.api.GetCurrentContextAsync();
        this.Messages.Clear();
        if (context?.Messages != null)
        {
            foreach (var message in context.Messages)
            {
                this.Messages.Add(new ChatMessageView
                {
                    Role = message.Role,
                    Content = NormalizeFormatting(message.Content),
                    Timestamp = message.Timestamp,
                    Stats = message.Stats
                });
            }
        }

        this.ContextLabel = string.IsNullOrWhiteSpace(context?.FilePath) ? "Temporary" : "Loaded: '" + Path.GetFileNameWithoutExtension(context.FilePath) + "'";
        this.scrollPending = true;
    }

    public async Task RenewAsync()
    {
        await this.api.CreateContextAsync();
        await this.RefreshAsync();
    }

    public async Task ToggleImagesAsync()
    {
        this.ShowImages = !this.ShowImages;
        if (this.ShowImages)
        {
            await this.RefreshImagesAsync();
        }
    }

    public async Task RefreshImagesAsync()
    {
        var items = await this.api.ListImagesAsync();
        var selectedIds = this.ImageSelections.Where(item => item.IsSelected)
            .Select(item => item.Image.Id)
            .ToHashSet();
        this.ImageSelections.Clear();
        if (items != null)
        {
            foreach (var image in items)
            {
                this.ImageSelections.Add(new ImageSelection
                {
                    Image = image,
                    IsSelected = selectedIds.Contains(image.Id)
                });
            }
        }
    }

    public async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(this.CurrentPrompt))
        {
            return;
        }

        this.ErrorMessage = null;
        this.IsGenerating = true;
        this.generationCts = new CancellationTokenSource();

        // --- OPTIONALER KEYSTROKE LOG ---
        string keystrokeLogJson = "[]";
        string finalPromptForLlama = this.CurrentPrompt;

        if (this.LogKeysEvents)
        {
            try
            {
                // Stop logging to ensure all events are flushed and handlers removed,
                // then read the current log. startLogging() should have been called on focus.
                try
                {
                    await this.js.InvokeVoidAsync("keystrokeLogger.stopLogging");
                }
                catch { }

                // Small delay to allow browser event loop to settle
                await Task.Delay(5);

                // Prefer the safer snapshot-enabled getter so we at least capture typed content
                keystrokeLogJson = await this.js.InvokeAsync<string>("keystrokeLogger.getLogWithSnapshot", "chatInput");

                // Nur wenn wir loggen, bauen wir den Prompt um
                finalPromptForLlama = @$"[FINAL_TEXT]: {this.CurrentPrompt}

[BEHAVIORAL_KEYMAP_JSON]:
{keystrokeLogJson}

INSTRUCTION: Answer the FINAL_TEXT. Use the KEYMAP (pauses, backspaces) to understand my emotions while writing.";
            }
            catch (Exception ex)
            {
                StaticLogger.Log($"Keystroke retrieval failed: {ex.Message}");
            }
        }

        // UI-Nachricht (immer der saubere Text)
        var userMessage = new ChatMessageView
        {
            Role = "user",
            Content = this.CurrentPrompt,
            Timestamp = DateTime.UtcNow
        };
        this.Messages.Add(userMessage);

        var assistantMessage = new ChatMessageView
        {
            Role = "assistant",
            Content = string.Empty,
            Timestamp = DateTime.UtcNow
        };
        this.Messages.Add(assistantMessage);

        var request = new LlamaGenerationRequest
        {
            Prompt = finalPromptForLlama, // Nutzt entweder den sauberen oder den angereicherten Prompt
            MaxTokens = this.MaxTokens,
            Temperature = this.Temperature,
            TopP = this.TopP,
            Stream = this.StreamResponse,
            UseSystemPrompt = this.UseSystemPrompt,
            Isolated = this.Isolated
        };

        // Bilder Handling (Bestand)
        var selectedImages = this.ImageSelections.Where(item => item.IsSelected).ToList();
        if (selectedImages.Count > 0)
        {
            foreach (var selected in selectedImages)
            {
                var data = await this.api.GetImageDataAsync(selected.Image.Id);
                if (!string.IsNullOrWhiteSpace(data?.Data))
                {
                    request.Base64Images.Add(data.Data);
                }
            }
        }

        this.CurrentPrompt = string.Empty;

        // Logger im Browser leeren, egal ob wir ihn genutzt haben oder nicht (für die nächste Runde)
        if (this.LogKeysEvents)
        {
            await this.js.InvokeVoidAsync("keystrokeLogger.clearLog");
        }

        await this.ScrollToBottomAsync();

        // Ab hier folgt dein bestehender Try-Catch Block für GenerateStreamAsync / GenerateTextAsync...
        try
        {
            if (this.StreamResponse)
            {
                await foreach (var chunk in this.api.GenerateStreamAsync(request, this.generationCts.Token))
                {
                    if (TryParseStats(chunk, out var stats))
                    {
                        assistantMessage.Stats = stats;
                        if (this.NotifyStateChanged != null)
                        {
                            await this.NotifyStateChanged();
                        }

                        await this.ScrollToBottomAsync();
                        continue;
                    }

                    assistantMessage.Content += NormalizeFormatting(chunk);
                    if (this.NotifyStateChanged != null)
                    {
                        await this.NotifyStateChanged();
                    }

                    await this.ScrollToBottomAsync();
                }
            }
            else
            {
                var response = await this.api.GenerateTextAsync(request, this.generationCts.Token);
                if (response != null)
                {
                    assistantMessage.Content = NormalizeFormatting(ExtractStats(response, out var stats));
                    assistantMessage.Stats = stats;
                }
            }
        }
        catch (OperationCanceledException) { this.ErrorMessage = "Generation canceled."; }
        catch (Exception ex) { this.ErrorMessage = ex.Message; }
        finally
        {
            this.IsGenerating = false;
            this.generationCts?.Dispose();
            this.generationCts = null;
            await this.ScrollToBottomAsync();
        }
    }

    public Task CancelAsync()
    {
        this.generationCts?.Cancel();



        return Task.CompletedTask;
    }

    public Task ScrollToBottomAsync()
    {
        if (this.messageContainer.Equals(default(ElementReference)))
        {
            return Task.CompletedTask;
        }

        return this.js.InvokeVoidAsync("sharpAiScrollToBottom", this.messageContainer).AsTask();
    }

    public async Task ApplyScrollAsync()
    {
        if (!this.scrollPending)
        {
            return;
        }

        this.scrollPending = false;
        await this.ScrollToBottomAsync();
    }

    // Minimal manual sanitizer: remove script/style tags and on* attributes, and strip potentially dangerous tags.
    public MarkupString RenderMarkdown(string? content)
    {
        var html = Markdown.ToHtml(content ?? string.Empty, MarkdownPipeline);

        // remove script/style blocks
        html = Regex.Replace(html, "<script\\b[^<]*(?:(?!<\\/script>)<[^<]*)*<\\/script>", string.Empty, RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "<style\\b[^<]*(?:(?!<\\/style>)<[^<]*)*<\\/style>", string.Empty, RegexOptions.IgnoreCase);

        // remove on* attributes (onclick, onload, etc.)
        html = Regex.Replace(html, @"\son[^""]+=""[^""]*""", string.Empty, RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "\\son[^=]+=('[^']*'|\"[^\"]*\")", string.Empty, RegexOptions.IgnoreCase);

        // remove javascript: URIs
        html = Regex.Replace(html, "javascript:\\s*[^\'\"]*", string.Empty, RegexOptions.IgnoreCase);

        // Optionally further strip tags that we don't want (keep basic formatting and code blocks)
        // Allowed tags: p, br, strong, em, code, pre, ul, ol, li, h1-h6, a
        html = Regex.Replace(html, "<\\/?(?!p|br|strong|b|em|i|code|pre|ul|ol|li|h[1-6]|a)([^>]+)>", string.Empty, RegexOptions.IgnoreCase);

        return new MarkupString(html);
    }

    public string RemoveMarkdown(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }
        // Remove markdown syntax to get plain text (basic approach)
        var text = Regex.Replace(content, @"(\*\*|__)(.*?)\1", "$2"); // bold
        text = Regex.Replace(text, @"(\*|_)(.*?)\1", "$2"); // italic
        text = Regex.Replace(text, @"`{1,3}(.*?)`{1,3}", "$1"); // code
        text = Regex.Replace(text, @"^>+\s?", string.Empty, RegexOptions.Multiline); // blockquotes
        text = Regex.Replace(text, @"!\[.*?\]\(.*?\)", string.Empty); // images
        text = Regex.Replace(text, @"\[([^\]]+)\]\(([^)]+)\)", "$1"); // links
        return NormalizeFormatting(text);
    }

    public string RemoveMarkup(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        // First remove common markdown constructs
        var text = this.RemoveMarkdown(content);

        // Remove any remaining HTML tags
        text = Regex.Replace(text, "<[^>]+>", string.Empty, RegexOptions.IgnoreCase);

        // Decode HTML entities
        try
        {
            text = System.Net.WebUtility.HtmlDecode(text);
        }
        catch
        {
            // ignore decode errors
        }

        // Normalize line endings and collapse multiple blank lines
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");
        text = Regex.Replace(text, "\n{2,}", "\n\n");

        // Trim excessive whitespace
        text = Regex.Replace(text, "[ \t]{2,}", " ");
        text = text.Trim();

        return text;
    }

    private static string NormalizeFormatting(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return text
            .Replace("\\r\\n", "\n", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal)
            .Replace("\t", "    ", StringComparison.Ordinal);
    }

    private static bool TryParseStats(string text, out LlamaContextStats? stats)
    {
        stats = null;
        var markerIndex = text.IndexOf("[Stats]", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        stats = ParseStats(text[markerIndex..]);
        return stats != null;
    }

    private static string ExtractStats(string text, out LlamaContextStats? stats)
    {
        var markerIndex = text.IndexOf("[Stats]", StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            stats = null;
            return text;
        }

        stats = ParseStats(text[markerIndex..]);
        return text[..markerIndex].TrimEnd();
    }

    private static LlamaContextStats? ParseStats(string statsText)
    {
        var tokensUsed = TryParseStat(statsText, "tokens=");
        var elapsed = TryParseStat(statsText, "elapsed=");
        if (tokensUsed == null || elapsed == null)
        {
            return null;
        }

        return new LlamaContextStats
        {
            TokensUsed = (int) tokensUsed.Value,
            SecondsElapsed = elapsed.Value
        };
    }

    private static double? TryParseStat(string statsText, string label)
    {
        var start = statsText.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += label.Length;
        var end = statsText.IndexOf(',', start);
        if (end < 0)
        {
            end = statsText.Length;
        }

        var valueText = statsText[start..end].Trim().TrimEnd('s');
        if (double.TryParse(valueText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return null;
    }

    public async Task TranslateMessageAsync(ChatMessageView message, string targetLanguage)
    {
        if (message == null || string.IsNullOrWhiteSpace(message.Content))
        {
            return;
        }

        try
        {
            if (!message.IsTranslated)
            {
                // Strip markdown/HTML to provide clean plain text to the translation API
                var sourceText = this.RemoveMarkup(message.Content);
                var translated = await this.api.GoogleTranslateAsync(sourceText, null, targetLanguage);
                // Only accept non-empty translations
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    message.Translation = translated;
                    message.IsTranslated = true;
                }
            }
            else
            {
                // Toggle back to original
                message.IsTranslated = false;
                message.Translation = null;
            }

            if (this.NotifyStateChanged != null)
            {
                await this.NotifyStateChanged();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    public async Task OnInputFocusAsync()
    {
        await this.js.InvokeVoidAsync("keystrokeLogger.startLogging", "chatInput");
    }




    public sealed class ChatMessageView
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string? Translation { get; set; } = null;
        public bool IsTranslated { get; set; } = false;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public LlamaContextStats? Stats { get; set; }
    }

    public sealed class ImageSelection
    {
        public ImageObjInfo Image { get; set; } = new();
        public bool IsSelected { get; set; }
    }

    public class KeystrokeEntry
    {
        [JsonPropertyName("k")] public string Key { get; set; } = "";     // Die Taste
        [JsonPropertyName("t")] public double Timestamp { get; set; }    // Zeit seit Start in ms
        [JsonPropertyName("p")] public int CursorPos { get; set; }       // Cursor-Position
        [JsonPropertyName("type")] public string Type { get; set; } = "down";
    }
}
