// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.ExternalAccess.Watch.Api;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.DotNet.Watch;

internal class IncrementalMSBuildWorkspace : Workspace
{
    private readonly IReporter _reporter;
    private readonly MSBuildProjectLoader _projectLoader;

    public IncrementalMSBuildWorkspace(IReporter reporter)
        : base(MSBuildMefHostServices.DefaultServices, WorkspaceKind.MSBuild)
    {
        WorkspaceFailed += (_sender, diag) =>
        {
            // Report both Warning and Failure as warnings.
            // MSBuildProjectLoader reports Failures for cases where we can safely continue loading projects
            // (e.g. non-C#/VB project is ignored).
            // https://github.com/dotnet/roslyn/issues/75170
            reporter.Warn($"msbuild: {diag.Diagnostic}", "⚠");
        };

        _reporter = reporter;

        _projectLoader = new MSBuildProjectLoader(this);
    }

    public async Task UpdateProjectConeAsync(string rootProjectPath, CancellationToken cancellationToken)
    {
        var oldSolution = CurrentSolution;
        var projectMap = ProjectMap.Create(oldSolution);

        ImmutableArray<ProjectInfo> projectInfos;
        try
        {
            projectInfos = await _projectLoader.LoadProjectInfoAsync(rootProjectPath, projectMap, progress: null, msbuildLogger: null, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // TODO: workaround for https://github.com/dotnet/roslyn/issues/75956
            projectInfos = [];
        }

        var oldProjectIds = oldSolution.ProjectIds.ToHashSet();
        var newSolution = oldSolution;

        foreach (var projectInfo in projectInfos)
        {
            if (!oldProjectIds.Contains(projectInfo.Id))
            {
                newSolution = newSolution.AddProject(projectInfo);
                continue;
            }

            newSolution = WatchHotReloadService.WithProjectInfo(newSolution, projectInfo);
        }

        await ReportSolutionFilesAsync(SetCurrentSolution(newSolution), cancellationToken);
        UpdateReferencesAfterAdd();
    }

    public async ValueTask UpdateFileContentAsync(IEnumerable<ChangedFile> changedFiles, CancellationToken cancellationToken)
    {
        var updatedSolution = CurrentSolution;

        var documentsToRemove = new List<DocumentId>();

        foreach (var (changedFile, change) in changedFiles)
        {
            // when a file is added we reevaluate the project:
            Debug.Assert(change != ChangeKind.Add);

            var documentIds = updatedSolution.GetDocumentIdsWithFilePath(changedFile.FilePath);
            if (change == ChangeKind.Delete)
            {
                documentsToRemove.AddRange(documentIds);
                continue;
            }

            foreach (var documentId in documentIds)
            {
                var textDocument = updatedSolution.GetDocument(documentId)
                    ?? updatedSolution.GetAdditionalDocument(documentId)
                    ?? updatedSolution.GetAnalyzerConfigDocument(documentId);

                if (textDocument == null)
                {
                    _reporter.Verbose($"Could not find document with path '{changedFile.FilePath}' in the workspace.");
                    continue;
                }

                var project = updatedSolution.GetProject(documentId.ProjectId);
                Debug.Assert(project?.FilePath != null);

                var oldText = await textDocument.GetTextAsync(cancellationToken);
                Debug.Assert(oldText.Encoding != null);

                var newText = await GetSourceTextAsync(changedFile.FilePath, oldText.Encoding, oldText.ChecksumAlgorithm, cancellationToken);

                updatedSolution = textDocument switch
                {
                    Document document => document.WithText(newText).Project.Solution,
                    AdditionalDocument ad => updatedSolution.WithAdditionalDocumentText(textDocument.Id, newText, PreservationMode.PreserveValue),
                    AnalyzerConfigDocument acd => updatedSolution.WithAnalyzerConfigDocumentText(textDocument.Id, newText, PreservationMode.PreserveValue),
                    _ => throw new InvalidOperationException()
                };
            }
        }

        updatedSolution = RemoveDocuments(updatedSolution, documentsToRemove);

        await ReportSolutionFilesAsync(SetCurrentSolution(updatedSolution), cancellationToken);
    }

    private static Solution RemoveDocuments(Solution solution, IEnumerable<DocumentId> ids)
        => solution
        .RemoveDocuments([.. ids.Where(id => solution.GetDocument(id) != null)])
        .RemoveAdditionalDocuments([.. ids.Where(id => solution.GetAdditionalDocument(id) != null)])
        .RemoveAnalyzerConfigDocuments([.. ids.Where(id => solution.GetAnalyzerConfigDocument(id) != null)]);

    private static async ValueTask<SourceText> GetSourceTextAsync(string filePath, Encoding encoding, SourceHashAlgorithm checksumAlgorithm, CancellationToken cancellationToken)
    {
        var zeroLengthRetryPerformed = false;
        for (var attemptIndex = 0; attemptIndex < 6; attemptIndex++)
        {
            try
            {
                // File.OpenRead opens the file with FileShare.Read. This may prevent IDEs from saving file
                // contents to disk
                SourceText sourceText;
                using (var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    sourceText = SourceText.From(stream, encoding, checksumAlgorithm);
                }

                if (!zeroLengthRetryPerformed && sourceText.Length == 0)
                {
                    zeroLengthRetryPerformed = true;

                    // VSCode (on Windows) will sometimes perform two separate writes when updating a file on disk.
                    // In the first update, it clears the file contents, and in the second, it writes the intended
                    // content.
                    // It's atypical that a file being watched for hot reload would be empty. We'll use this as a
                    // hueristic to identify this case and perform an additional retry reading the file after a delay.
                    await Task.Delay(20, cancellationToken);

                    using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    sourceText = SourceText.From(stream, encoding, checksumAlgorithm);
                }

                return sourceText;
            }
            catch (IOException) when (attemptIndex < 5)
            {
                await Task.Delay(20 * (attemptIndex + 1), cancellationToken);
            }
        }

        Debug.Fail("This shouldn't happen.");
        return null;
    }

    public async Task ReportSolutionFilesAsync(Solution solution, CancellationToken cancellationToken)
    {
        _reporter.Verbose($"Solution: {solution.FilePath}");
        foreach (var project in solution.Projects)
        {
            _reporter.Verbose($"  Project: {project.FilePath}");

            foreach (var document in project.Documents)
            {
                await InspectDocumentAsync(document, "Document");
            }

            foreach (var document in project.AdditionalDocuments)
            {
                await InspectDocumentAsync(document, "Additional");
            }

            foreach (var document in project.AnalyzerConfigDocuments)
            {
                await InspectDocumentAsync(document, "Config");
            }
        }

        async ValueTask InspectDocumentAsync(TextDocument document, string kind)
        {
            var text = await document.GetTextAsync(cancellationToken);
            _reporter.Verbose($"    {kind}: {document.FilePath} [{Convert.ToBase64String(text.GetChecksum().ToArray())}]");
        }
    }
}
