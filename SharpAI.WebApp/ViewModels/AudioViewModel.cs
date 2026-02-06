using Radzen;
using Microsoft.AspNetCore.Components.Forms;
using SharpAI.Client;
using SharpAI.Shared;
using System.IO;
using System.Linq;
using System.Threading;

namespace SharpAI.WebApp.ViewModels
{
    public class AudioViewModel
    {
        private readonly ApiClient api;
        private readonly SemaphoreSlim refreshLock = new(1, 1);
        private Task? recordingTask;

        public AudioViewModel(ApiClient api)
        {
            this.api = api;
        }

        public List<AudioObjInfo> AudioInfos { get; } = new();
        public AudioObjData? CurrentAudio { get; private set; }
        public string? SelectedAudioId { get; set; }
        public bool? IsRecording { get; set; } = null;
        public bool IncludeWaveforms { get; set; } = false;
        public int WaveformPreviewWidth { get; set; } = 300;
        public int WaveformPreviewHeight { get; set; } = 70;
        public string RecordingStatus => this.IsRecording == null ? "Unknown" : (this.IsRecording.Value ? "Recording" : "Not Recording");
        public string RecordingStatusColorHex => this.IsRecording.HasValue ? (this.IsRecording.Value ? "#4CAF50" : "#F44336") : "#9E9E9E";

        public bool OrderByLatest { get; set; } = true;

        public string ApiBaseUrl => this.api.BaseUrl;

        public string? StatusMessage { get; set; }

        // upload selection
        public Stream? SelectedFileStream { get; private set; }
        public string? SelectedFileName { get; private set; }
        public string? SelectedFileContentType { get; private set; }

        public Task InitializeAsync() => this.RefreshAsync();

        public async Task RefreshAsync()
        {
            await this.refreshLock.WaitAsync();
            try
            {
                this.StatusMessage = null;
                this.IsRecording = await this.api.GetRecordingStatusAsync();

                this.AudioInfos.Clear();
                var items = await this.api.GetAudiosAsync(this.IncludeWaveforms ? this.WaveformPreviewWidth : null, this.IncludeWaveforms ? this.WaveformPreviewHeight : null);
                if (items != null && this.OrderByLatest)
                {
                    items = items.OrderByDescending(i => i.CreatedAt).ToList();
                }
                if (items != null)
                {
                    var distinctItems = items.GroupBy(i => i.Id).Select(g => g.First()).ToList();
                    this.AudioInfos.AddRange(distinctItems);
                }
            }
            finally
            {
                this.refreshLock.Release();
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
            this.SelectedFileStream = file.OpenReadStream(maxAllowedSize: 104857600); // 100 MB
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
            var uploaded = await this.api.UploadAudioAsync(fileParam);
            this.StatusMessage = uploaded != null ? "Audio uploaded." : "Upload failed.";
            this.SelectedFileStream = null;
            this.SelectedFileName = null;
            this.SelectedFileContentType = null;
            await this.RefreshAsync();
        }

        public async Task DeleteAsync(Guid id)
        {
            var ok = await this.api.DeleteAudioAsync(id);
            this.StatusMessage = ok ? "Audio deleted." : "Delete failed.";
            await this.RefreshAsync();
        }

        public async Task DownloadAsync(Guid id)
        {
            try
            {
                await this.api.DownloadAudioAsync(id);
                this.StatusMessage = "Download triggered.";
            }
            catch (Exception ex)
            {
                this.StatusMessage = "Download failed: " + ex.Message;
            }
        }

        public async Task ExportAsync(Guid id, int bits = 32)
        {
            try
            {
                // Export on server side to user's MyMusic folder (server machine)
                string? defaultExportDir = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                var path = await this.api.ExportAudioAsync(id, defaultExportDir, null, bits);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    this.StatusMessage = "Exported to: " + path;
                }
                else
                {
                    this.StatusMessage = "Export failed.";
                }
                await this.RefreshAsync();
            }
            catch (Exception ex)
            {
                this.StatusMessage = "Export failed: " + ex.Message;
            }
        }

        public async Task ToggleRecordAsync()
        {
            this.StatusMessage = null;
            try
            {
                var currentStatus = await this.api.GetRecordingStatusAsync();
                this.IsRecording = currentStatus;

                if (this.IsRecording == true)
                {
                    var beforeCount = this.AudioInfos.Count;
                    var ok = await this.api.StopRecordingAsync();
                    this.IsRecording = false;
                    this.StatusMessage = ok ? "Recording stopped." : "Failed to stop recording.";
                    await this.RefreshAsync();
                    await this.RefreshUntilAudioAddedAsync(beforeCount, TimeSpan.FromSeconds(6));
                }
                else
                {
                    this.IsRecording = true;
                    this.StatusMessage = "Recording started.";
                    await this.RefreshAsync();
                    if (this.recordingTask == null || this.recordingTask.IsCompleted)
                    {
                        this.recordingTask = Task.Run(async () =>
                        {
                            try
                            {
                                await this.api.StartRecordingAsync();
                            }
                            catch
                            {
                            }
                        });
                    }
                    await this.RefreshUntilRecordingStateAsync(true, TimeSpan.FromSeconds(4));
                }
            }
            catch (Exception ex)
            {
                this.StatusMessage = "Record error: " + ex.Message;
            }
            finally
            {
                this.IsRecording = await this.api.GetRecordingStatusAsync();
            }
        }

        private async Task RefreshUntilAudioAddedAsync(int initialCount, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(1000);
                await this.RefreshAsync();

                if (this.IsRecording == false && this.AudioInfos.Count > initialCount)
                {
                    break;
                }
            }
        }

        private async Task RefreshUntilRecordingStateAsync(bool targetState, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                var status = await this.api.GetRecordingStatusAsync();
                if (status == targetState)
                {
                    this.IsRecording = status;
                    await this.RefreshAsync();
                    return;
                }
                await Task.Delay(500);
            }
            await this.RefreshAsync();
        }

        [Microsoft.JSInterop.JSInvokable]
        public async Task OnWaveformContainerResized(string? audioId, int width, int height)
        {
            try
            {
                // If sizes changed, update VM preview size and refresh to request new previews
                if (this.IncludeWaveforms && width > 0 && height > 0 && (this.WaveformPreviewWidth != width || this.WaveformPreviewHeight != height))
                {
                    this.WaveformPreviewWidth = width;
                    this.WaveformPreviewHeight = height;
                    await this.RefreshAsync();
                }
            }
            catch
            {
                // swallow
            }
        }
    }
}
