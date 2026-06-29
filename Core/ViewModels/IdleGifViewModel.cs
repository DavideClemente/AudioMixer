using System;
using AudioMixerWin.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AudioMixerWin.Core.ViewModels;

public partial class IdleGifViewModel : ObservableObject
{
    public IdleGifViewModel(IdleGifConfig config, string filePath)
    {
        Config = config;
        FilePath = filePath;
        Source = new BitmapImage(new Uri(filePath));
    }

    public IdleGifConfig Config { get; }

    public string Id => Config.Id;

    public string FilePath { get; }

    // BitmapImage over the local file: type-correct for Image.Source and plays
    // the animated GIF (AutoPlay defaults true).
    public ImageSource Source { get; }

    public string OriginalName => Config.OriginalName;

    public string DimensionsText => $"{Config.PixelWidth} × {Config.PixelHeight}";

    public string SizeText => FormatSize(Config.SizeBytes);

    public string DetailText => $"{DimensionsText} · {SizeText}";

    [ObservableProperty]
    private bool isSelected;

    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024.0):0.0} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:0} KB";
        return $"{bytes} B";
    }
}
