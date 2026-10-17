using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using AvaloniaApplication1;
using AvaloniaApplication1.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaApplication1.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly AppConfig _config;

    public IReadOnlyList<string> CompressionOptions { get; } = ["Raw", "DXT1", "DXT3", "DXT5"];
    public IReadOnlyList<string> AlphaDepthOptions => SelectedCompression switch
    {
        "DXT1" => ["0-bit (Opaque)", "1-bit"],
        "DXT3" => ["4-bit"],
        "DXT5" => ["8-bit"],
        _ => ["0-bit (Opaque)", "1-bit", "4-bit", "8-bit"]
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlpInput))]
    [NotifyPropertyChangedFor(nameof(IsPngInput))]
    private string _inputPath = string.Empty;

    [ObservableProperty]
    private string _outputPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlphaDepthOptions))]
    private string _selectedCompression = "DXT1";

    [ObservableProperty]
    private string _selectedAlphaDepth = "1-bit";

    [ObservableProperty]
    private bool _generateMipmaps = true;

    [ObservableProperty]
    private bool _useColorKeyAwareMipmapping;

    [ObservableProperty]
    private string _colorKeyHex = "#000000";

    [ObservableProperty]
    private string _statusMessage = "Ready.";

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlpInput))]
    [NotifyPropertyChangedFor(nameof(IsPngInput))]
    [NotifyPropertyChangedFor(nameof(InputLabel))]
    [NotifyPropertyChangedFor(nameof(OutputLabel))]
    [NotifyPropertyChangedFor(nameof(OutputPickerLabel))]
    [NotifyPropertyChangedFor(nameof(OutputPathPlaceholder))]
    [NotifyPropertyChangedFor(nameof(IsSingleFileMode))]
    private bool _isBatchMode;

    private string[] _batchInputPaths = [];

    public string InputLabel => IsBatchMode ? "Batch inputs" : "Input file";
    public string OutputLabel => IsBatchMode ? "Batch output" : "Output file";
    public string OutputPickerLabel => "Save as...";
    public string OutputPathPlaceholder => IsBatchMode ? "Same directory as each source; matching files overwritten" : "Output file path";
    public bool IsSingleFileMode => !IsBatchMode;
    public bool IsBlpInput => !IsBatchMode && Path.GetExtension(InputPath).Equals(".blp", StringComparison.OrdinalIgnoreCase);
    public bool IsPngInput => IsBatchMode || Path.GetExtension(InputPath).Equals(".png", StringComparison.OrdinalIgnoreCase);
    public bool CanProcess => !IsProcessing && (IsBatchMode
        ? _batchInputPaths.Length > 0
        : File.Exists(InputPath) && !string.IsNullOrWhiteSpace(OutputPath));

    public MainWindowViewModel()
    {
        _config = AppConfig.Load(out var warning);
        InputPath = _config.LastInputPath;
        OutputPath = _config.LastOutputPath;
        if (File.Exists(InputPath))
        {
            var expectedExtension = IsBlpInput ? ".png" : ".blp";
            if (!Path.GetExtension(OutputPath).Equals(expectedExtension, StringComparison.OrdinalIgnoreCase))
                OutputPath = Path.ChangeExtension(InputPath, expectedExtension);
        }
        SelectedCompression = CompressionOptions.Contains(_config.Compression) ? _config.Compression : "DXT1";
        SelectedAlphaDepth = AlphaDepthOptions.Contains(_config.AlphaDepth) ? _config.AlphaDepth : "1-bit";
        GenerateMipmaps = _config.GenerateMipmaps;
        UseColorKeyAwareMipmapping = _config.UseColorKeyAwareMipmapping;
        ColorKeyHex = _config.ColorKeyHex;
        if (!string.IsNullOrWhiteSpace(warning))
            StatusMessage = warning;
    }

    partial void OnSelectedCompressionChanged(string value)
    {
        if (!AlphaDepthOptions.Contains(SelectedAlphaDepth))
        {
            SelectedAlphaDepth = value switch
            {
                "DXT3" => "4-bit",
                "DXT5" => "8-bit",
                _ => "1-bit"
            };
        }
    }

    partial void OnInputPathChanged(string value)
    {
        if (!IsBatchMode && File.Exists(value))
        {
            var extension = IsBlpInput ? ".png" : ".blp";
            OutputPath = Path.ChangeExtension(value, extension);
        }
        OnPropertyChanged(nameof(CanProcess));
        ProcessCommand.NotifyCanExecuteChanged();
    }

    partial void OnOutputPathChanged(string value)
    {
        OnPropertyChanged(nameof(CanProcess));
        ProcessCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsProcessingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanProcess));
        ProcessCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBatchModeChanged(bool value)
    {
        OnPropertyChanged(nameof(CanProcess));
        ProcessCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task SelectInputFileAsync()
    {
        var storage = GetStorageProvider();
        if (storage is null)
        {
            StatusMessage = "The platform file picker is unavailable.";
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select PNG or BLP textures",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Supported images") { Patterns = ["*.png", "*.blp"] }
            ]
        });

        if (files.Count == 0)
            return;

        var paths = files.Select(file => file.Path.LocalPath).ToArray();
        foreach (var file in files)
            file.Dispose();

        if (paths.Length == 1)
            SetSingleInputPath(paths[0]);
        else
            AcceptDroppedPaths(paths);
    }

    [RelayCommand]
    private async Task SelectOutputPathAsync()
    {
        var storage = GetStorageProvider();
        if (storage is null)
        {
            StatusMessage = "The platform file picker is unavailable.";
            return;
        }

        if (IsBatchMode)
            return;

        var extension = IsBlpInput ? "png" : "blp";
        var files = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Choose the converted image file",
            SuggestedFileName = Path.GetFileName(OutputPath),
            DefaultExtension = extension,
            FileTypeChoices =
            [
                new FilePickerFileType(extension.Equals("png", StringComparison.Ordinal) ? "PNG image" : "BLP texture")
                {
                    Patterns = [$"*.{extension}"]
                }
            ]
        });

        if (files is not null)
            OutputPath = files.Path.LocalPath;
    }

    [RelayCommand(CanExecute = nameof(CanProcess))]
    private async Task ProcessAsync()
    {
        if (!IsBatchMode && !File.Exists(InputPath))
        {
            StatusMessage = "Error: Select an existing PNG or BLP input file.";
            return;
        }
        if (!IsBatchMode && string.IsNullOrWhiteSpace(OutputPath))
        {
            StatusMessage = "Error: Choose an output file path.";
            return;
        }

        IsProcessing = true;
        StatusMessage = IsBatchMode ? "Scanning dropped folders..." : "Converting image...";
        try
        {
            SaveSettings();
            var options = new BlpConversionOptions(
                ParseCompression(SelectedCompression),
                ParseAlphaDepth(SelectedAlphaDepth),
                GenerateMipmaps,
                UseColorKeyAwareMipmapping,
                ColorKeyHex);

            if (IsBatchMode)
            {
                var inputPaths = _batchInputPaths.ToArray();
                var progress = new Progress<BatchConversionProgress>(update =>
                    StatusMessage = $"Converting {update.Completed}/{update.Total}: {update.FileName}");
                var summary = await Task.Run(() => BatchConversionService.Convert(inputPaths, options, progress));
                StatusMessage = FormatBatchSummary(summary);
            }
            else
            {
                var inputPath = InputPath;
                var outputPath = OutputPath;
                await Task.Run(() => BlpConversionService.Convert(inputPath, outputPath, options));
                StatusMessage = $"Conversion complete: {outputPath}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Conversion failed: {ex.Message}";
        }
        finally
        {
            IsProcessing = false;
        }
    }

    public void AcceptDroppedPaths(IEnumerable<string> paths)
    {
        if (IsProcessing)
        {
            StatusMessage = "Wait for the current conversion to finish before dropping another batch.";
            return;
        }

        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var droppedPaths = paths.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(pathComparer).ToArray();
        if (droppedPaths.Length == 0)
        {
            StatusMessage = "No local files or folders were received.";
            return;
        }

        if (droppedPaths.Length == 1 && File.Exists(droppedPaths[0]))
        {
            SetSingleInputPath(droppedPaths[0]);
            return;
        }

        _batchInputPaths = droppedPaths;
        IsBatchMode = true;
        InputPath = $"{droppedPaths.Length} dropped item(s); folders scanned recursively";
        OutputPath = "Same directory as each source; matching converted files will be overwritten";
        StatusMessage = "Batch ready. Supported files will be converted beside their sources.";
        OnPropertyChanged(nameof(CanProcess));
        ProcessCommand.NotifyCanExecuteChanged();
    }

    public void ReportDropError(string message) => StatusMessage = $"Drop failed: {message}";

    private void SetSingleInputPath(string path)
    {
        _batchInputPaths = [];
        IsBatchMode = false;
        InputPath = path;
        OnPropertyChanged(nameof(CanProcess));
        ProcessCommand.NotifyCanExecuteChanged();
    }

    private static string FormatBatchSummary(BatchConversionSummary summary)
    {
        if (summary.Converted == 0 && summary.Failed == 0)
            return $"No supported PNG or BLP files were found. {summary.Skipped} item(s) skipped.";

        var lines = new List<string>
        {
            $"Batch complete: {summary.Converted} converted in place, {summary.Failed} failed, {summary.Skipped} unsupported or linked items skipped. Matching existing files were overwritten."
        };
        lines.AddRange(summary.Errors.Take(4));
        if (summary.Failed > summary.Errors.Count)
            lines.Add($"Additional {summary.Failed - summary.Errors.Count} errors omitted.");
        return string.Join(Environment.NewLine, lines);
    }

    private void SaveSettings()
    {
        _config.LastInputPath = IsBatchMode ? _batchInputPaths.FirstOrDefault() ?? string.Empty : InputPath;
        if (!IsBatchMode)
            _config.LastOutputPath = OutputPath;
        _config.Compression = SelectedCompression;
        _config.AlphaDepth = SelectedAlphaDepth;
        _config.GenerateMipmaps = GenerateMipmaps;
        _config.UseColorKeyAwareMipmapping = UseColorKeyAwareMipmapping;
        _config.ColorKeyHex = ColorKeyHex;
        _config.Save();
    }

    private static BlpCompression ParseCompression(string value) => value switch
    {
        "Raw" => BlpCompression.Raw,
        "DXT1" => BlpCompression.DXT1,
        "DXT3" => BlpCompression.DXT3,
        "DXT5" => BlpCompression.DXT5,
        _ => throw new InvalidOperationException("Select a supported BLP compression format.")
    };

    private static BlpAlphaDepth ParseAlphaDepth(string value) => value switch
    {
        "0-bit (Opaque)" => BlpAlphaDepth.Opaque0Bit,
        "1-bit" => BlpAlphaDepth.OneBit,
        "4-bit" => BlpAlphaDepth.FourBit,
        "8-bit" => BlpAlphaDepth.EightBit,
        _ => throw new InvalidOperationException("Select a supported alpha depth.")
    };

    private static IStorageProvider? GetStorageProvider()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow?.StorageProvider;
        return null;
    }
}
