using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;

namespace AvaloniaApplication1;

public sealed record BatchConversionProgress(int Completed, int Total, string FileName);

public sealed record BatchConversionSummary(int Converted, int Failed, int Skipped, IReadOnlyList<string> Errors);

public static class BatchConversionService
{
    private const int MaxReportedErrors = 20;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static BatchConversionSummary Convert(
        IEnumerable<string> inputPaths,
        BlpConversionOptions options,
        IProgress<BatchConversionProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        ArgumentNullException.ThrowIfNull(options);

        var roots = inputPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(PathComparer)
            .ToArray();
        if (roots.Length == 0)
            throw new ArgumentException("Drop at least one file or folder.", nameof(inputPaths));

        var files = new List<InputFile>();
        var errors = new List<string>();
        var failed = 0;
        var skipped = 0;
        DiscoverFiles(roots, files, errors, ref failed, ref skipped);
        files.Sort((first, second) => PathComparer.Compare(first.RelativePath, second.RelativePath));

        if (files.Count == 0)
            return new BatchConversionSummary(0, failed, skipped, errors);

        var batchInputPaths = new HashSet<string>(files.Select(file => file.FullPath), PathComparer);
        var stagedSourcePaths = new HashSet<string>(PathComparer);
        var stagedOutputs = new List<StagedOutput>();
        var temporaryPaths = new List<string>();
        var converted = 0;
        var completed = 0;
        try
        {
            foreach (var file in files)
            {
                var outputPath = GetOutputPath(file);
                var temporaryPath = GetTemporaryOutputPath(outputPath);
                temporaryPaths.Add(temporaryPath);
                try
                {
                    BlpConversionService.Convert(file.FullPath, temporaryPath, options);
                    stagedSourcePaths.Add(file.FullPath);
                    stagedOutputs.Add(new StagedOutput(temporaryPath, outputPath));
                }
                catch (Exception ex)
                {
                    failed++;
                    AddError(errors, $"{file.FullPath}: {ex.Message}");
                }

                completed++;
                progress?.Report(new BatchConversionProgress(completed, files.Count, Path.GetFileName(file.FullPath)));
            }

            foreach (var staged in stagedOutputs)
            {
                if (batchInputPaths.Contains(staged.OutputPath) && !stagedSourcePaths.Contains(staged.OutputPath))
                {
                    failed++;
                    AddError(errors, $"Did not overwrite {staged.OutputPath} because its matching source failed to convert.");
                    continue;
                }

                try
                {
                    File.Move(staged.TemporaryPath, staged.OutputPath, overwrite: true);
                    converted++;
                }
                catch (Exception ex) when (IsFileSystemException(ex))
                {
                    failed++;
                    AddError(errors, $"{staged.OutputPath}: {ex.Message}");
                }
            }
        }
        finally
        {
            foreach (var temporaryPath in temporaryPaths)
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch (Exception ex) when (IsFileSystemException(ex))
                {
                    failed++;
                    AddError(errors, $"Could not remove temporary file {temporaryPath}: {ex.Message}");
                }
            }
        }

        return new BatchConversionSummary(converted, failed, skipped, errors);
    }

    private static void DiscoverFiles(
        IReadOnlyList<string> roots,
        List<InputFile> files,
        List<string> errors,
        ref int failed,
        ref int skipped)
    {
        var rootDirectoryNames = new HashSet<string>(PathComparer);
        var discoveredFiles = new HashSet<string>(PathComparer);

        foreach (var root in roots)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(root);
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                failed++;
                AddError(errors, $"{root}: {ex.Message}");
                continue;
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                if (IsSupportedFile(root))
                    AddFile(root, Path.GetFileName(root), files, discoveredFiles);
                else
                    skipped++;
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                skipped++;
                AddError(errors, $"Skipped linked folder to prevent traversal outside the selected tree: {root}");
                continue;
            }

            var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
            if (string.IsNullOrWhiteSpace(rootName))
            {
                rootName = Path.GetPathRoot(root)?.Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace(':', '_') ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rootName))
                    rootName = "Root";
            }
            var folderName = GetUniqueDirectoryName(rootName, rootDirectoryNames);
            var pendingFolders = new Stack<(string FullPath, string RelativePath)>();
            pendingFolders.Push((root, folderName));

            while (pendingFolders.Count > 0)
            {
                var (folderPath, relativeFolder) = pendingFolders.Pop();
                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(folderPath))
                    {
                        FileAttributes entryAttributes;
                        try
                        {
                            entryAttributes = File.GetAttributes(entry);
                        }
                        catch (Exception ex) when (IsFileSystemException(ex))
                        {
                            failed++;
                            AddError(errors, $"{entry}: {ex.Message}");
                            continue;
                        }

                        if ((entryAttributes & FileAttributes.Directory) != 0)
                        {
                            if ((entryAttributes & FileAttributes.ReparsePoint) != 0)
                            {
                                skipped++;
                                continue;
                            }

                            pendingFolders.Push((entry, Path.Combine(relativeFolder, Path.GetFileName(entry))));
                            continue;
                        }

                        if (IsSupportedFile(entry))
                            AddFile(entry, Path.Combine(relativeFolder, Path.GetFileName(entry)), files, discoveredFiles);
                        else
                            skipped++;
                    }
                }
                catch (Exception ex) when (IsFileSystemException(ex))
                {
                    failed++;
                    AddError(errors, $"{folderPath}: {ex.Message}");
                }
            }
        }
    }

    private static string GetOutputPath(InputFile file)
    {
        var outputExtension = Path.GetExtension(file.FullPath).Equals(".png", StringComparison.OrdinalIgnoreCase)
            ? ".blp"
            : ".png";
        return Path.ChangeExtension(file.FullPath, outputExtension);
    }

    private static string GetTemporaryOutputPath(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath)!;
        var extension = Path.GetExtension(outputPath);
        return Path.Combine(directory, $".{Guid.NewGuid():N}.stage{extension}");
    }

    private static string GetUniqueDirectoryName(string name, HashSet<string> usedNames)
    {
        if (usedNames.Add(name))
            return name;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{name} ({suffix})";
            if (usedNames.Add(candidate))
                return candidate;
        }
    }

    private static bool IsSupportedFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".blp", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddFile(string fullPath, string relativePath, List<InputFile> files, HashSet<string> discoveredFiles)
    {
        var normalizedPath = Path.GetFullPath(fullPath);
        if (discoveredFiles.Add(normalizedPath))
            files.Add(new InputFile(normalizedPath, relativePath));
    }

    private static void AddError(List<string> errors, string error)
    {
        if (errors.Count < MaxReportedErrors)
            errors.Add(error);
    }

    private static bool IsFileSystemException(Exception exception) => exception is IOException or UnauthorizedAccessException or SecurityException;

    private sealed record InputFile(string FullPath, string RelativePath);

    private sealed record StagedOutput(string TemporaryPath, string OutputPath);
}
