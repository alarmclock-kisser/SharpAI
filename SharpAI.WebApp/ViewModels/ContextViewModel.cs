using SharpAI.Client;
using SharpAI.Shared;

namespace SharpAI.WebApp.ViewModels;

public sealed class ContextViewModel
{
    private readonly ApiClient api;

    public ContextViewModel(ApiClient api)
    {
        this.api = api;
    }

    public List<LlamaContextData> Contexts { get; } = new();
    public LlamaContextData? CurrentContext { get; private set; }
    public string? SelectedContextPath { get; set; }
    public string? SaveName { get; set; }
    public string? StatusMessage { get; private set; }

    public Task InitializeAsync() => this.RefreshAsync();

    public async Task RefreshAsync()
    {
        this.CurrentContext = await this.api.GetCurrentContextAsync();
        var items = await this.api.GetAllContextsAsync();
        this.Contexts.Clear();
        if (items != null)
        {
            this.Contexts.AddRange(items);
        }
    }

    public async Task LoadAsync()
    {
        this.StatusMessage = null;
        if (string.IsNullOrWhiteSpace(this.SelectedContextPath))
        {
            this.StatusMessage = "Select a context first.";
            return;
        }

        var loaded = await this.api.LoadContextAsync(this.SelectedContextPath);
        this.StatusMessage = loaded != null ? "Context loaded." : "Failed to load context.";
        await this.RefreshAsync();
    }

    public async Task SaveAsync()
    {
        this.StatusMessage = null;
        var saved = await this.api.SaveContextAsync(string.IsNullOrWhiteSpace(this.SaveName) ? null : this.SaveName);
        this.StatusMessage = saved != null ? "Context saved." : "Failed to save context.";
        await this.RefreshAsync();
    }

    public async Task CreateAsync()
    {
        this.StatusMessage = null;
        var created = await this.api.CreateContextAsync(use: true, save: true);
        this.StatusMessage = created != null ? "Context created." : "Failed to create context.";
        await this.RefreshAsync();
    }

    public async Task DeleteAsync()
    {
        this.StatusMessage = null;
        if (string.IsNullOrWhiteSpace(this.SelectedContextPath))
        {
            this.StatusMessage = "Select a context first.";
            return;
        }

        var ok = await this.api.DeleteContextAsync(this.SelectedContextPath);
        this.StatusMessage = ok ? "Context deleted." : "Failed to delete context.";
        await this.RefreshAsync();
    }

    public static string GetContextLabel(LlamaContextData context)
    {
        if (string.IsNullOrWhiteSpace(context.FilePath))
        {
            return "Temporary";
        }

        return System.IO.Path.GetFileName(context.FilePath);
    }
}
