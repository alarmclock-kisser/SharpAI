using SharpAI.Client;
using SharpAI.Shared;
using System.Linq;

namespace SharpAI.WebApp.ViewModels;

public sealed class ModelViewModel
{
    private const int MinContextSize = 128;
    private const int MinMaxTokens = 1;
    private readonly ApiClient api;

    public ModelViewModel(ApiClient api)
    {
        this.api = api;
    }

    public List<LlamaModelFile> Models { get; } = new();
    public string? SelectedModelPath { get; set; }
    public int ContextSize { get; set; } = 2048;
    public LlamaBackend Backend { get; set; } = LlamaBackend.CPU;
    public float DefaultTemperature { get; set; } = 0.7f;
    public int MaxTokens { get; set; } = 1024;
    public bool FuzzyMatch { get; set; } = true;
    public bool IsLoaded { get; private set; }
    public bool IsBusy { get; private set; }
    public string? StatusMessage { get; private set; }
    public string? ErrorMessage { get; private set; }


    private bool FirstRender { get; set; } = true;

    public Task InitializeAsync() => this.RefreshAsync();

    public async Task RefreshAsync()
    {
        var status = await this.api.GetModelStatusAsync();
        this.IsLoaded = status != null;

        if (!this.IsLoaded && this.Backend == LlamaBackend.CPU)
        {
            var cudaVersion = await this.api.GetCudaVersionAsync();
            if (!string.IsNullOrWhiteSpace(cudaVersion))
            {
                this.Backend = LlamaBackend.CUDA;
            }
        }

        var items = await this.api.GetLlamaModelFilesAsync();
        this.Models.Clear();
        if (items != null)
        {
            this.Models.AddRange(items);
        }

        if (this.FirstRender)
        {
            var appsettings = await this.api.GetAppsettingsAsync();
            var backend = appsettings?.PreferredLlamaBackend.ToLowerInvariant() switch
            {
                "cpu" => LlamaBackend.CPU,
                "cuda" => LlamaBackend.CUDA,
                "opencl" => LlamaBackend.OpenCL,
                _ => LlamaBackend.CPU
            };
            this.Backend = backend;
            this.ContextSize = appsettings?.MaxContextTokens ?? this.ContextSize;
            this.DefaultTemperature = (float)(appsettings?.Temperature ?? this.DefaultTemperature);
            this.MaxTokens = appsettings?.MaxResponseTokens ?? this.MaxTokens;
            this.SelectedModelPath = string.IsNullOrWhiteSpace(appsettings?.DefaultLlamaModel) ? this.Models.FirstOrDefault()?.FilePath : this.Models.FirstOrDefault(m => m.FilePath.Contains(appsettings.DefaultLlamaModel, StringComparison.OrdinalIgnoreCase))?.FilePath;
            
            this.FirstRender = false;
        }
    }

    public void IncrementContextSize()
    {
        this.ContextSize = Math.Max(MinContextSize, this.ContextSize) * 2;
    }

    public void DecrementContextSize()
    {
        var next = Math.Max(MinContextSize, this.ContextSize / 2);
        this.ContextSize = next == 0 ? MinContextSize : next;
    }

    public void IncrementMaxTokens()
    {
        this.MaxTokens = Math.Max(MinMaxTokens, this.MaxTokens) * 2;
    }

    public void DecrementMaxTokens()
    {
        var next = Math.Max(MinMaxTokens, this.MaxTokens / 2);
        this.MaxTokens = next == 0 ? MinMaxTokens : next;
    }

    public void OnContextSizeChanged(int value)
    {
        if (value == this.ContextSize + 1)
        {
            this.IncrementContextSize();
            return;
        }

        if (value == this.ContextSize - 1)
        {
            this.DecrementContextSize();
            return;
        }

        this.ContextSize = Math.Max(MinContextSize, value);
    }

    public void OnMaxTokensChanged(int value)
    {
        if (value == this.MaxTokens + 1)
        {
            this.IncrementMaxTokens();
            return;
        }

        if (value == this.MaxTokens - 1)
        {
            this.DecrementMaxTokens();
            return;
        }

        this.MaxTokens = Math.Max(MinMaxTokens, value);
    }

    public async Task ToggleModelAsync()
    {
        this.StatusMessage = null;
        this.ErrorMessage = null;
        this.IsBusy = true;
        this.StatusMessage = this.IsLoaded ? "Unloading model..." : "Loading model...";
        if (this.IsLoaded)
        {
            var ok = await this.api.UnloadModelAsync();
            this.StatusMessage = ok ? "Model unloaded." : "Failed to unload model.";
        }
        else
        {
            var model = this.Models.FirstOrDefault(m => string.Equals(m.FilePath, this.SelectedModelPath, StringComparison.OrdinalIgnoreCase));
            if (model == null)
            {
                this.StatusMessage = "Select a model first.";
                return;
            }

            var request = new LlamaModelLoadRequest(model, this.ContextSize, this.Backend);
            var loaded = await this.api.LoadModelAsync(request, this.FuzzyMatch, true);
            if (loaded != null)
            {
                this.StatusMessage = "Model loaded.";
            }
            else
            {
                this.StatusMessage = "Failed to load model.";
                this.ErrorMessage = this.api.LastErrorMessage ?? "Failed to load model.";
            }
        }

        await this.RefreshAsync();
        this.IsBusy = false;
    }
}
