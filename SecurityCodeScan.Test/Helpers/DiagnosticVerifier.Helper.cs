﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.VisualBasic;

namespace SecurityCodeScan.Test.Helpers
{
    /// <summary>
    /// Class for turning strings into documents and getting the diagnostics on them
    /// All methods are static
    /// </summary>
    public abstract partial class DiagnosticVerifier
    {
        private static readonly MetadataReference CorlibReference        = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        private static readonly MetadataReference SystemCoreReference    = MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location);
        private static readonly MetadataReference CSharpSymbolsReference = MetadataReference.CreateFromFile(typeof(CSharpCompilation).Assembly.Location);
        private static readonly MetadataReference CodeAnalysisReference  = MetadataReference.CreateFromFile(typeof(Compilation).Assembly.Location);
        private static readonly MetadataReference SystemDiagReference    = MetadataReference.CreateFromFile(typeof(Process).Assembly.Location);

        private static readonly CompilationOptions CSharpDefaultOptions      = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
        private static readonly CompilationOptions VisualBasicDefaultOptions = new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

        internal const string DefaultFilePathPrefix = "Test";
        internal const string CSharpDefaultFileExt  = "cs";
        internal const string VisualBasicDefaultExt = "vb";
        internal const string TestProjectName       = "TestProject";

        #region  Get Diagnostics

        /// <summary>
        /// Given classes in the form of strings, their language, and an IDiagnosticAnlayzer to apply to it,
        /// return the diagnostics found in the string after converting it to a document.
        /// </summary>
        /// <param name="sources">Classes in the form of strings</param>
        /// <param name="language">The language the source classes are in</param>
        /// <param name="analyzers">The analyzers to be run on the sources</param>
        /// <param name="references">Additional referenced modules</param>
        /// <param name="includeCompilerDiagnostics">Get compiler diagnostics too</param>
        /// <returns>An IEnumerable of Diagnostics that surfaced in the source code, sorted by Location</returns>
        private static async Task<Diagnostic[]> GetSortedDiagnostics(
            string[]                           sources,
            string                             language,
            ImmutableArray<DiagnosticAnalyzer> analyzers,
            IEnumerable<MetadataReference>     references                 = null,
            bool                               includeCompilerDiagnostics = false)
        {
            return await GetSortedDiagnosticsFromDocuments(analyzers,
                                                           GetDocuments(sources, language, references),
                                                           includeCompilerDiagnostics);
        }

        /// <summary>
        /// Given an analyzer and a document to apply it to,
        /// run the analyzer and gather an array of diagnostics found in it.
        /// The returned diagnostics are then ordered by location in the source document.
        /// </summary>
        /// <param name="analyzers">The analyzers to run on the documents</param>
        /// <param name="documents">The Documents that the analyzer will be run on</param>
        /// <param name="includeCompilerDiagnostics">Get compiler diagnostics too</param>
        /// <returns>An IEnumerable of Diagnostics that surfaced in the source code, sorted by Location</returns>
        protected static async Task<Diagnostic[]> GetSortedDiagnosticsFromDocuments(
            ImmutableArray<DiagnosticAnalyzer> analyzers,
            IEnumerable<Document>              documents,
            bool                               includeCompilerDiagnostics = false)
        {
            var projects = new HashSet<Project>();
            foreach (var document in documents)
            {
                projects.Add(document.Project);
            }

            var diagnostics = new List<Diagnostic>();
            foreach (var project in projects)
            {
                var compilation              = await project.GetCompilationAsync();
                var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers);
                var diags                    = includeCompilerDiagnostics
                                                   ? await compilationWithAnalyzers.GetAllDiagnosticsAsync()
                                                   : await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();

                foreach (var diag in diags)
                {
                    if (diag.Location == Location.None || diag.Location.IsInMetadata)
                    {
                        diagnostics.Add(diag);
                    }
                    else
                    {
                        foreach (var document in documents)
                        {
                            var tree = await document.GetSyntaxTreeAsync();
                            if (tree == diag.Location.SourceTree)
                            {
                                diagnostics.Add(diag);
                            }
                        }
                    }
                }
            }

            var results = SortDiagnostics(diagnostics);
            return results;
        }

        /// <summary>
        /// Sort diagnostics by location in source document
        /// </summary>
        /// <param name="diagnostics">The list of Diagnostics to be sorted</param>
        /// <returns>An IEnumerable containing the Diagnostics in order of Location</returns>
        private static Diagnostic[] SortDiagnostics(IEnumerable<Diagnostic> diagnostics)
        {
            return diagnostics.OrderBy(d => d.Location.SourceSpan.Start).ToArray();
        }

        #endregion

        #region Set up compilation and documents

        /// <summary>
        /// Given an array of strings as sources and a language,
        /// turn them into a project and return the documents and spans of it.
        /// </summary>
        /// <param name="sources">Classes in the form of strings</param>
        /// <param name="language">The language the source code is in</param>
        /// <returns>A Tuple containing the Documents
        ///  produced from the sources and their TextSpans if relevant</returns>
        private static IEnumerable<Document> GetDocuments(string[]                       sources,
                                                          string                         language,
                                                          IEnumerable<MetadataReference> references = null)
        {
            if (language != LanguageNames.CSharp && language != LanguageNames.VisualBasic)
            {
                throw new ArgumentException("Unsupported Language");
            }

            var project   = CreateProject(sources, language, references);
            return project.Documents;
        }

        /// <summary>
        /// Create a Document from a string through creating a project that contains it.
        /// </summary>
        /// <param name="source">Classes in the form of a string</param>
        /// <param name="language">The language the source code is in</param>
        /// <returns>A Document created from the source string</returns>
        protected static Document CreateDocument(string                         source,
                                                 string                         language   = LanguageNames.CSharp,
                                                 IEnumerable<MetadataReference> references = null)
        {
            return CreateProject(new[] { source }, language, references).Documents.First();
        }

        /// <summary>
        /// Create a project using the inputted strings as sources.
        /// </summary>
        /// <param name="sources">Classes in the form of strings</param>
        /// <param name="language">The language the source code is in</param>
        /// <returns>A Project created out of the Documents created from the source strings</returns>
        private static Project CreateProject(string[]                           sources,
                                             string                             language        = LanguageNames.CSharp,
                                             IEnumerable<MetadataReference>     references      = null)
        {
            string fileNamePrefix = DefaultFilePathPrefix;
            string fileExt        = language == LanguageNames.CSharp ? CSharpDefaultFileExt : VisualBasicDefaultExt;

            var options = language == LanguageNames.CSharp ? CSharpDefaultOptions : VisualBasicDefaultOptions;

            var projectId = ProjectId.CreateNewId(debugName: TestProjectName);

            var solution = new AdhocWorkspace()
                           .CurrentSolution
                           .AddProject(projectId, TestProjectName, TestProjectName, language)
                           .AddMetadataReference(projectId, CorlibReference)
                           .AddMetadataReference(projectId, SystemCoreReference)
                           .AddMetadataReference(projectId, CSharpSymbolsReference)
                           .AddMetadataReference(projectId, CodeAnalysisReference)
                           .AddMetadataReference(projectId, SystemDiagReference)
                           .WithProjectCompilationOptions(projectId, options);

            if (references != null)
            {
                solution = solution.AddMetadataReferences(projectId, references);
            }

            int count = 0;
            foreach (var source in sources)
            {
                var newFileName = fileNamePrefix + count + "." + fileExt;
                var documentId  = DocumentId.CreateNewId(projectId, debugName: newFileName);
                solution        = solution.AddDocument(documentId, newFileName, SourceText.From(source));
                count++;
            }

            return solution.GetProject(projectId);
        }

        #endregion
    }
}
