using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Win32;
using NuGet.Versioning;
using RoslynPad.Hosting;
using RoslynPad.Roslyn.Diagnostics;
using RoslynPad.Runtime;
using RoslynPad.Utilities;

namespace RoslynPad
{
    internal class OpenDocumentViewModel : NotificationObject
    {
        private readonly string _workingDirectory;
        private readonly Dispatcher _dispatcher;

        private ExecutionHost _executionHost;
        private ObservableCollection<ResultObject> _results;
        private CancellationTokenSource _cts;
        private bool _isRunning;
        private Action<object> _executionHostOnDumped;
        private bool _isDirty;
        private Platform _platform;
        private bool _isSaving;
        private IDisposable _viewDisposable;
        private Action<ExceptionResultObject> _onError;
        private Func<TextSpan> _getSelection;

        public ObservableCollection<ResultObject> Results
        {
            get { return _results; }
            private set { SetProperty(ref _results, value); }
        }

        public DocumentViewModel Document { get; private set; }

        public OpenDocumentViewModel(MainViewModel mainViewModel, DocumentViewModel document)
        {
            Document = document;
            MainViewModel = mainViewModel;
            NuGet = new NuGetDocumentViewModel(mainViewModel.NuGet);
            _dispatcher = Dispatcher.CurrentDispatcher;

            var roslynHost = mainViewModel.RoslynHost;

            IsDirty = document?.IsAutoSave == true;

            _workingDirectory = Document != null
                ? Path.GetDirectoryName(Document.Path)
                : MainViewModel.DocumentRoot.Path;

            Platform = Platform.X86;
            _executionHost = new ExecutionHost(GetHostExeName(), _workingDirectory,
                roslynHost.DefaultReferences.OfType<PortableExecutableReference>().Select(x => x.FilePath),
                roslynHost.DefaultImports, mainViewModel.NuGetConfiguration);

            SaveCommand = new DelegateCommand(() => Save(promptSave: false));
            RunCommand = new DelegateCommand(Run, () => !IsRunning);
            CompileAndSaveCommand = new DelegateCommand(CompileAndSave);
            RestartHostCommand = new DelegateCommand(RestartHost);
            FormatDocumentCommand = new DelegateCommand(FormatDocument);
            CommentSelectionCommand = new DelegateCommand(() => CommentUncommentSelection(CommentAction.Comment));
            UncommentSelectionCommand = new DelegateCommand(() => CommentUncommentSelection(CommentAction.Uncomment));
        }

        private enum CommentAction
        {
            Comment,
            Uncomment
        }

        private async Task CommentUncommentSelection(CommentAction action)
        {
            const string singleLineCommentString = "//";
            var document = MainViewModel.RoslynHost.GetDocument(DocumentId);
            var selection = _getSelection();
            var documentText = await document.GetTextAsync().ConfigureAwait(false);
            var changes = new List<TextChange>();
            var lines = documentText.Lines.SkipWhile(x => !x.Span.IntersectsWith(selection))
                .TakeWhile(x => x.Span.IntersectsWith(selection)).ToArray();

            if (action == CommentAction.Comment)
            {
                foreach (var line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(documentText.GetSubText(line.Span).ToString()))
                    {
                        changes.Add(new TextChange(new TextSpan(line.Start, 0), singleLineCommentString));
                    }
                }
            }
            else if (action == CommentAction.Uncomment)
            {
                foreach (var line in lines)
                {
                    var text = documentText.GetSubText(line.Span).ToString();
                    if (text.TrimStart().StartsWith(singleLineCommentString, StringComparison.Ordinal))
                    {
                        changes.Add(new TextChange(new TextSpan(
                            line.Start + text.IndexOf(singleLineCommentString, StringComparison.Ordinal),
                            singleLineCommentString.Length), string.Empty));
                    }
                }
            }

            if (changes.Count == 0) return;

            MainViewModel.RoslynHost.UpdateDocument(document.WithText(documentText.WithChanges(changes)));
            if (action == CommentAction.Uncomment)
            {
                await FormatDocument().ConfigureAwait(false);
            }
        }

        private async Task FormatDocument()
        {
            var document = MainViewModel.RoslynHost.GetDocument(DocumentId);
            var formattedDocument = await Formatter.FormatAsync(document).ConfigureAwait(false);
            MainViewModel.RoslynHost.UpdateDocument(formattedDocument);
        }

        private string GetHostExeName()
        {
            switch (Platform)
            {
                case Platform.X86:
                    return "RoslynPad.Host32.exe";
                case Platform.X64:
                    return "RoslynPad.Host64.exe";
                default:
                    throw new ArgumentOutOfRangeException(nameof(Platform));
            }
        }

        public Platform Platform
        {
            get { return _platform; }
            set
            {
                if (SetProperty(ref _platform, value))
                {
                    if (_executionHost != null)
                    {
                        _executionHost.HostPath = GetHostExeName();
                        RestartHostCommand.Execute();
                    }
                }
            }
        }

        private async Task RestartHost()
        {
            Reset();
            await _executionHost.ResetAsync().ConfigureAwait(false);
            SetIsRunning(false);
        }

        private void SetIsRunning(bool value)
        {
            _dispatcher.InvokeAsync(() => IsRunning = value);
        }

        public async Task AutoSave()
        {
            if (!IsDirty) return;
            if (Document == null)
            {
                var index = 1;
                string path;
                do
                {
                    path = Path.Combine(_workingDirectory, DocumentViewModel.GetAutoSaveName("Program" + index++));
                } while (File.Exists(path));
                Document = DocumentViewModel.CreateAutoSave(path);
            }

            await SaveDocument(Document.GetAutoSavePath()).ConfigureAwait(false);
        }

        public async Task<SaveResult> Save(bool promptSave)
        {
            if (_isSaving) return SaveResult.Cancel;
            if (!IsDirty) return SaveResult.Save;

            _isSaving = true;
            try
            {
                var result = SaveResult.Save;
                if (Document == null || Document.IsAutoSaveOnly)
                {
                    var dialog = new SaveDocumentDialog
                    {
                        ShowDontSave = promptSave,
                        AllowNameEdit = true,
                        FilePathFactory = s => DocumentViewModel.GetDocumentPathFromName(_workingDirectory, s)
                    };
                    dialog.Show();
                    result = dialog.Result;
                    if (result == SaveResult.Save)
                    {
                        if (Document?.IsAutoSave == true)
                        {
                            File.Delete(Document.Path);
                        }
                        Document = MainViewModel.AddDocument(dialog.DocumentName);
                        OnPropertyChanged(nameof(Title));
                    }
                }
                else if (promptSave)
                {
                    var dialog = new SaveDocumentDialog
                    {
                        ShowDontSave = true,
                        DocumentName = Document.Name
                    };
                    dialog.Show();
                    result = dialog.Result;
                }
                if (result == SaveResult.Save)
                {
                    // ReSharper disable once PossibleNullReferenceException
                    await SaveDocument(Document.GetSavePath()).ConfigureAwait(true);
                    IsDirty = false;
                }
                return result;
            }
            finally
            {
                _isSaving = false;
            }
        }

        private async Task SaveDocument(string path)
        {
            if (DocumentId == null) return;

            var text = await MainViewModel.RoslynHost.GetDocument(DocumentId).GetTextAsync().ConfigureAwait(false);
            using (var writer = new StreamWriter(path, append: false))
            {
                for (int lineIndex = 0; lineIndex < text.Lines.Count - 1; ++lineIndex)
                {
                    var lineText = text.Lines[lineIndex].ToString();
                    await writer.WriteLineAsync(lineText).ConfigureAwait(false);
                }
                await writer.WriteAsync(text.Lines[text.Lines.Count - 1].ToString()).ConfigureAwait(false);
            }
        }

        public async Task Initialize(SourceTextContainer sourceTextContainer,
            Action<DiagnosticsUpdatedArgs> onDiagnosticsUpdated, Action<SourceText> onTextUpdated,
            Action<ExceptionResultObject> onError,
            Func<TextSpan> getSelection, IDisposable viewDisposable)
        {
            _viewDisposable = viewDisposable;
            _onError = onError;
            _getSelection = getSelection;
            var roslynHost = MainViewModel.RoslynHost;
            // ReSharper disable once AssignNullToNotNullAttribute
            DocumentId = roslynHost.AddDocument(sourceTextContainer, _workingDirectory, onDiagnosticsUpdated,
                onTextUpdated);
            await _executionHost.ResetAsync().ConfigureAwait(false);
        }

        public DocumentId DocumentId { get; private set; }

        public MainViewModel MainViewModel { get; }

        public NuGetDocumentViewModel NuGet { get; }

        public string Title => Document != null && !Document.IsAutoSaveOnly ? Document.Name : "New";

        public DelegateCommand SaveCommand { get; }

        public DelegateCommand RunCommand { get; }

        public DelegateCommand CompileAndSaveCommand { get; }

        public DelegateCommand RestartHostCommand { get; }

        public DelegateCommand FormatDocumentCommand { get; }

        public DelegateCommand CommentSelectionCommand { get; }

        public DelegateCommand UncommentSelectionCommand { get; }

        public bool IsRunning
        {
            get { return _isRunning; }
            private set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    _dispatcher.InvokeAsync(() => RunCommand.RaiseCanExecuteChanged());
                }
            }
        }

        private async Task CompileAndSave()
        {
            var saveDialog = new SaveFileDialog
            {
                OverwritePrompt = true,
                AddExtension = true,
                Filter = "Assemblies|*.dll",
                DefaultExt = "dll"
            };
            if (saveDialog.ShowDialog(Application.Current.MainWindow) != true) return;

            var code = await GetCode(CancellationToken.None).ConfigureAwait(true);

            var results = new ObservableCollection<ResultObject>();
            Results = results;

            HookDumped(results, CancellationToken.None);

            try
            {
                await Task.Run(() => _executionHost.CompileAndSave(code, saveDialog.FileName)).ConfigureAwait(true);
            }
            catch (CompilationErrorException ex)
            {
                foreach (var diagnostic in ex.Diagnostics)
                {
                    results.Add(ResultObject.Create(diagnostic));
                }
            }
            catch (Exception ex)
            {
                AddResult(ex, results, CancellationToken.None);
            }
        }

        private async Task Run()
        {
            if (IsRunning) return;

            try
            {
                await EnsureNuGetPackages().ConfigureAwait(true);
            }
            catch (Exception)
            {
                IsRunning = false;
                throw;
            }

            Reset();

            await MainViewModel.AutoSaveOpenDocuments().ConfigureAwait(true);

            SetIsRunning(true);

            var results = new ObservableCollection<ResultObject>();
            Results = results;

            var cancellationToken = _cts.Token;
            HookDumped(results, cancellationToken);
            try
            {
                var code = await GetCode(cancellationToken).ConfigureAwait(true);
                var errorResult = await _executionHost.ExecuteAsync(code).ConfigureAwait(true);
                _onError?.Invoke(errorResult);
                if (errorResult != null)
                {
                    results.Add(errorResult);
                }
            }
            catch (CompilationErrorException ex)
            {
                foreach (var diagnostic in ex.Diagnostics)
                {
                    results.Add(ResultObject.Create(diagnostic));
                }
            }
            catch (Exception ex)
            {
                AddResult(ex, results, cancellationToken);
            }
            finally
            {
                SetIsRunning(false);
            }
        }

        private async Task EnsureNuGetPackages()
        {
            var nugetVariable = MainViewModel.NuGetConfiguration.PathVariableName;
            var pathToRepository = MainViewModel.NuGetConfiguration.PathToRepository;
            foreach (var directive in MainViewModel.RoslynHost.GetReferencesDirectives(DocumentId))
            {
                if (directive.StartsWith(nugetVariable, StringComparison.OrdinalIgnoreCase))
                {
                    var directiveWithoutRoot = directive.Substring(nugetVariable.Length + 1);
                    if (!File.Exists(Path.Combine(pathToRepository, directiveWithoutRoot)))
                    {
                        var sections = directiveWithoutRoot.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
                        NuGetVersion version;
                        if (sections.Length > 2 && NuGetVersion.TryParse(sections[1], out version))
                        {
                            await NuGet.InstallPackage(sections[0], version, reportInstalled: false).ConfigureAwait(false);
                        }
                    }
                }
            }
        }

        private void HookDumped(ObservableCollection<ResultObject> results, CancellationToken cancellationToken)
        {
            if (_executionHostOnDumped != null)
            {
                _executionHost.Dumped -= _executionHostOnDumped;
            }
            _executionHostOnDumped = o => AddResult(o, results, cancellationToken);
            _executionHost.Dumped += _executionHostOnDumped;
        }

        private async Task<string> GetCode(CancellationToken cancellationToken)
        {
            return (await MainViewModel.RoslynHost.GetDocument(DocumentId).GetTextAsync(cancellationToken)
                .ConfigureAwait(false)).ToString();
        }

        private void Reset()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
            }
            _cts = new CancellationTokenSource();
        }

        private void AddResult(object o, ObservableCollection<ResultObject> results, CancellationToken cancellationToken)
        {
            _dispatcher.InvokeAsync(() =>
            {
                var list = o as IList<ResultObject>;
                if (list != null)
                {
                    foreach (var resultObject in list)
                    {
                        results.Add(resultObject);
                    }
                }
                else
                {
                    results.Add(ResultObject.Create(o));
                }
            }, DispatcherPriority.SystemIdle, cancellationToken);
        }

        public async Task<string> LoadText()
        {
            if (Document == null)
            {
                return string.Empty;
            }
            using (var fileStream = new StreamReader(Document.Path))
            {
                return await fileStream.ReadToEndAsync().ConfigureAwait(false);
            }
        }

        public void Close()
        {
            _viewDisposable?.Dispose();
            _executionHost?.Dispose();
            _executionHost = null;
        }

        public bool IsDirty
        {
            get { return _isDirty; }
            private set { SetProperty(ref _isDirty, value); }
        }

        public void SetDirty(int textLength)
        {
            IsDirty = textLength > 0;
        }
    }
}