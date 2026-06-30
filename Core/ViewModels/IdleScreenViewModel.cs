using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AudioMixerWin.Core.Models;
using AudioMixerWin.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;

namespace AudioMixerWin.Core.ViewModels;

public partial class IdleScreenViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IdleGifLibraryService _library;
    private readonly Action _save;
    private readonly Func<Task<IReadOnlyList<StorageFile>>> _pickGifs;
    private readonly Func<XamlRoot?> _getXamlRoot;
    private readonly Func<IdleGifConfig, IProgress<double>?, CancellationToken, Task> _pushGif;
    private readonly Action _clearGif;
    private readonly Func<bool> _isConnected;

    private CancellationTokenSource? _pushCts;

    public ObservableCollection<IdleGifViewModel> Gifs { get; } = new();

    [ObservableProperty]
    private IdleGifViewModel? selectedGif;

    [ObservableProperty]
    private string usageText = "";

    [ObservableProperty]
    private bool isUploading;

    [ObservableProperty]
    private double uploadProgress;

    [ObservableProperty]
    private string uploadStatus = "";

    public bool HasGifs => Gifs.Count > 0;

    public IdleScreenViewModel(
        AppSettings settings,
        IdleGifLibraryService library,
        Action save,
        Func<Task<IReadOnlyList<StorageFile>>> pickGifs,
        Func<XamlRoot?> getXamlRoot,
        Func<IdleGifConfig, IProgress<double>?, CancellationToken, Task> pushGif,
        Action clearGif,
        Func<bool> isConnected)
    {
        _settings = settings;
        _library = library;
        _save = save;
        _pickGifs = pickGifs;
        _getXamlRoot = getXamlRoot;
        _pushGif = pushGif;
        _clearGif = clearGif;
        _isConnected = isConnected;

        Gifs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasGifs));
            UpdateUsage();
        };

        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        // Prune entries whose backing file is gone (e.g. cache cleared by hand),
        // so stale metadata can't strand the library.
        var pruned = false;
        for (var i = _settings.IdleGifs.Count - 1; i >= 0; i--)
        {
            if (!_library.Exists(_settings.IdleGifs[i]))
            {
                if (_settings.IdleGifs[i].Id == _settings.ActiveIdleGifId)
                    _settings.ActiveIdleGifId = null;
                _settings.IdleGifs.RemoveAt(i);
                pruned = true;
            }
        }
        if (pruned)
            _save();

        foreach (var config in _settings.IdleGifs)
            Gifs.Add(new IdleGifViewModel(config, _library.PathFor(config)));

        var active = Gifs.FirstOrDefault(g => g.Id == _settings.ActiveIdleGifId);
        if (active != null)
        {
            active.IsSelected = true;
            SelectedGif = active;
        }

        UpdateUsage();
    }

    private void UpdateUsage()
    {
        var count = Gifs.Count;
        var bytes = _library.TotalSizeBytes(Gifs.Select(g => g.Config));
        var size = bytes >= 1024 * 1024
            ? $"{bytes / (1024.0 * 1024.0):0.0} MB"
            : $"{bytes / 1024.0:0} KB";
        UsageText = count == 1 ? $"1 GIF · {size}" : $"{count} GIFs · {size}";
    }

    [RelayCommand]
    private async Task AddGifsAsync()
    {
        var files = await _pickGifs();
        if (files == null || files.Count == 0)
            return;

        foreach (var file in files)
        {
            var config = await _library.ImportAsync(file);
            if (config == null)
                continue;

            _settings.IdleGifs.Add(config);
            Gifs.Add(new IdleGifViewModel(config, _library.PathFor(config)));
        }

        _save();
    }

    [RelayCommand]
    private async Task SetActiveAsync(IdleGifViewModel? gif)
    {
        if (gif == null)
            return;

        foreach (var g in Gifs)
            g.IsSelected = ReferenceEquals(g, gif);

        SelectedGif = gif;
        _settings.ActiveIdleGifId = gif.Id;
        _save();

        await PushAsync(gif);
    }

    // Selecting a GIF sends it to the controller automatically. If the controller
    // is offline the choice is still saved; re-selecting once connected re-sends.
    private async Task PushAsync(IdleGifViewModel gif)
    {
        if (!_isConnected())
        {
            UploadProgress = 0;
            UploadStatus = "Saved. Connect the controller, then select this GIF again to send it.";
            return;
        }

        // A newer selection supersedes any upload still in flight.
        _pushCts?.Cancel();
        var cts = _pushCts = new CancellationTokenSource();

        IsUploading = true;
        UploadProgress = 0;
        UploadStatus = $"Sending \"{gif.OriginalName}\" to controller…";

        try
        {
            var progress = new Progress<double>(p => UploadProgress = p);
            await _pushGif(gif.Config, progress, cts.Token);
            UploadStatus = "Active on controller";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer push; leave the newer one to report status.
        }
        catch (Exception ex)
        {
            UploadStatus = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_pushCts, cts))
            {
                IsUploading = false;
                _pushCts = null;
            }
        }
    }

    [RelayCommand]
    private async Task DeleteGifAsync(IdleGifViewModel? gif)
    {
        if (gif == null)
            return;

        var xamlRoot = _getXamlRoot();
        if (xamlRoot == null)
            return;

        var dialog = new ContentDialog
        {
            Title = "Delete GIF",
            Content = $"Remove \"{gif.OriginalName}\" from your idle-screen library?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var wasActive = _settings.ActiveIdleGifId == gif.Id;

        _library.Delete(gif.Config);
        _settings.IdleGifs.RemoveAll(c => c.Id == gif.Id);
        Gifs.Remove(gif);

        if (wasActive)
        {
            _settings.ActiveIdleGifId = null;
            SelectedGif = null;
            UploadStatus = "";
            // Drop it from the controller too so it reverts to the built-in idle.
            _clearGif();
        }

        _save();
    }
}
