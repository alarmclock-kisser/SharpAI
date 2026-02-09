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
    public bool Temporary { get; set; } = false;

    public bool OrderByLatest { get; set; } = true;

    public string? NewSystemPrompt { get; set; } = null;
    public string? StatusMessage { get; private set; }

    private bool FirstRender { get; set; } = true;

    public Task InitializeAsync() => this.RefreshAsync();

    public async Task RefreshAsync()
    {
        this.CurrentContext = await this.api.GetCurrentContextAsync();
        var items = await this.api.GetAllContextsAsync(this.OrderByLatest);
        this.Contexts.Clear();
        if (items != null)
        {
            this.Contexts.AddRange(items);
        }

        var ctxPrompt = this.CurrentContext?.SystemPrompt;
        if (string.IsNullOrWhiteSpace(ctxPrompt))
        {
            this.NewSystemPrompt = await this.api.GetSystemPromptAsync() ?? string.Empty;
        }
        else
        {
            this.NewSystemPrompt = ctxPrompt;
        }

        if (this.FirstRender)
        {
            var appsettings = await this.api.GetAppsettingsAsync();

            if (!string.IsNullOrEmpty(appsettings?.DefaultContext))
            {
                await this.api.LoadContextAsync(appsettings.DefaultContext);
            }

            if (!string.IsNullOrEmpty(string.Join(" ", appsettings?.SystemPrompts.Select(p => p.Trim()) ?? [])))
            {
                await this.api.SetSystemPromptAsync(string.Join(" ", appsettings?.SystemPrompts.Select(p => p.Trim()) ?? []));
            }

            this.FirstRender = false;
            await this.RefreshAsync();
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
        var created = await this.api.CreateContextAsync(use: true, save: !this.Temporary);

        // Refresh to pick up the new current context from the service
        await this.RefreshAsync();

        // If user supplied a save name, try to rename/save the newly created context
        if (!string.IsNullOrWhiteSpace(this.SaveName))
        {
            await this.RenameAsync();
        }

        // Consider creation successful if the service now has a current context
        this.StatusMessage = this.CurrentContext != null ? "Context created." : "Failed to create context.";
    }

    public async Task RenameAsync()
    {
        this.StatusMessage = null;
        if (string.IsNullOrWhiteSpace(this.SaveName))
        {
            this.StatusMessage = "Enter a new name first.";
            return;
        }
        string? path = this.SelectedContextPath ?? this.CurrentContext?.FilePath;

        var renamed = await this.api.RenameContextAsync(path, this.SaveName);
        this.StatusMessage = renamed != null ? "Context renamed." : "Failed to rename context.";
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

    public async Task SetSystemPromptAsync()
    {
        string prompt = this.NewSystemPrompt ?? string.Empty;
        var ok = await this.api.SetSystemPromptAsync(prompt);
        if (!string.IsNullOrEmpty(ok))
        {
            this.StatusMessage = "System prompt updated.";
            await this.RefreshAsync();
        }
        else
        {
            this.StatusMessage = "Failed to update system prompt.";
        }
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
