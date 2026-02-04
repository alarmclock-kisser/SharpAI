using Radzen;
using SharpAI.Client;
using SharpAI.Shared;
using System.IO;
using System.Linq;

namespace SharpAI.WebApp.ViewModels;

public sealed class ImageViewModel
{
    private readonly ApiClient api;

    public ImageViewModel(ApiClient api)
    {
        this.api = api;
    }

    public List<ImageObjInfo> Images { get; } = new();
    public Stream? SelectedFileStream { get; private set; }
    public string? SelectedFileName { get; private set; }
    public string? SelectedFileContentType { get; private set; }
    public string? StatusMessage { get; private set; }

    public Task InitializeAsync() => this.RefreshAsync();

    public async Task RefreshAsync()
    {
        var items = await this.api.ListImagesAsync();
        this.Images.Clear();
        if (items != null)
        {
            this.Images.AddRange(items);
        }
    }

    public async Task OnFileSelectedAsync(UploadChangeEventArgs args)
    {
        var file = args.Files?.FirstOrDefault();
        if (file == null)
        {
            this.SelectedFileStream = null;
            this.SelectedFileName = null;
            this.SelectedFileContentType = null;
            return;
        }

        this.SelectedFileName = file.Name;
        this.SelectedFileContentType = file.ContentType;
        this.SelectedFileStream = file.OpenReadStream();
        await this.UploadAsync();
    }

    public async Task UploadAsync()
    {
        this.StatusMessage = null;
        if (this.SelectedFileStream == null || string.IsNullOrWhiteSpace(this.SelectedFileName))
        {
            return;
        }

        await using var stream = this.SelectedFileStream;
        var fileParam = new FileParameter(stream, this.SelectedFileName, this.SelectedFileContentType);
        var uploaded = await this.api.UploadImageAsync(fileParam);
        this.StatusMessage = uploaded != null ? "Image uploaded." : "Upload failed.";
        this.SelectedFileStream = null;
        this.SelectedFileName = null;
        this.SelectedFileContentType = null;
        await this.RefreshAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        var ok = await this.api.DeleteImageAsync(id);
        this.StatusMessage = ok ? "Image deleted." : "Delete failed.";
        await this.RefreshAsync();
    }
}
