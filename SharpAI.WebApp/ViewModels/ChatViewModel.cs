using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using SharpAI.Client;
using SharpAI.Shared;
using System.Linq;

namespace SharpAI.WebApp.ViewModels;

public sealed class ChatViewModel
{
    private readonly ApiClient api;
    private readonly IJSRuntime js;
    private ElementReference messageContainer;
    private CancellationTokenSource? generationCts;

    public ChatViewModel(ApiClient api, IJSRuntime js)
    {
        this.api = api;
        this.js = js;
    }

    public List<ChatMessageView> Messages { get; } = new();
    public string Prompt { get; set; } = string.Empty;
    public int MaxTokens { get; set; } = 512;
    public float Temperature { get; set; } = 0.7f;
    public float TopP { get; set; } = 0.95f;
    public bool StreamResponse { get; set; } = true;
    public bool Isolated { get; set; }
    public bool IsGenerating { get; private set; }
    public bool? IsModelLoaded { get; private set; }
    public string? LoadedModelName { get; private set; }
    public string ContextLabel { get; private set; } = "Temporary";
    public string? ErrorMessage { get; private set; }
    public bool ShowImages { get; private set; }
    public List<ImageSelection> ImageSelections { get; } = new();
    public Func<Task>? NotifyStateChanged { get; set; }

    public Task InitializeAsync() => this.RefreshAsync();

    public void SetMessageContainer(ElementReference container)
    {
        this.messageContainer = container;
    }

    public async Task RefreshAsync()
    {
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
                    Content = message.Content,
                    Timestamp = message.Timestamp,
                    Stats = message.Stats
                });
            }
        }

        this.ContextLabel = string.IsNullOrWhiteSpace(context?.FilePath) ? "Temporary" : "Loaded";
        await this.ScrollToBottomAsync();
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
        if (string.IsNullOrWhiteSpace(this.Prompt))
        {
            return;
        }

        this.ErrorMessage = null;
        this.IsGenerating = true;
        this.generationCts = new CancellationTokenSource();

        var userMessage = new ChatMessageView
        {
            Role = "user",
            Content = this.Prompt,
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
            Prompt = this.Prompt,
            MaxTokens = this.MaxTokens,
            Temperature = this.Temperature,
            TopP = this.TopP,
            Stream = this.StreamResponse
        };

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

        this.Prompt = string.Empty;
        await this.ScrollToBottomAsync();

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

                    var formattedChunk = chunk.Replace("\\n", "\n");
                    assistantMessage.Content += formattedChunk;
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
                    assistantMessage.Content = ExtractStats(response, out var stats);
                    assistantMessage.Stats = stats;
                }
            }
        }
        catch (OperationCanceledException)
        {
            this.ErrorMessage = "Generation canceled.";
        }
        catch (Exception ex)
        {
            this.ErrorMessage = ex.Message;
        }
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

    private Task ScrollToBottomAsync()
    {
        if (this.messageContainer.Equals(default(ElementReference)))
        {
            return Task.CompletedTask;
        }

        return this.js.InvokeVoidAsync("sharpAiScrollToBottom", this.messageContainer).AsTask();
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
            TokensUsed = (int)tokensUsed.Value,
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

    public sealed class ChatMessageView
    {
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public LlamaContextStats? Stats { get; set; }
    }

    public sealed class ImageSelection
    {
        public ImageObjInfo Image { get; set; } = new();
        public bool IsSelected { get; set; }
    }
}
