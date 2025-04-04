#if NET472
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.CodeAnalysis.Text;
using UnityExplorer.Config;

namespace UnityExplorer.CSConsole
{
    public class RoslynScriptEvaluator : IDisposable
    {
        private static readonly FieldInfo ModuleImplField = AccessTools.Field(typeof(Module), "_impl");
        private static readonly FieldInfo StackTraceFrames = AccessTools.Field(typeof(StackTrace), "frames");
        private static readonly MethodInfo TopLevelBinderFlagsGetter =
            AccessTools.PropertyGetter(typeof(CSharpCompilationOptions), "TopLevelBinderFlags");
        private static readonly MethodInfo TopLevelBinderFlagsSetter =
            AccessTools.PropertySetter(typeof(CSharpCompilationOptions), "TopLevelBinderFlags");

        // We only need rawData and rawDataLen so this is declared until these two
        [StructLayout(LayoutKind.Sequential)]
        private struct MonoImage
        {
            internal int refCount;
            internal nint rawDataHandle;
            internal unsafe byte* rawData;
            internal int rawDataLen;
        }

        // Options for scripts that will get compiled, these never changes
        private static readonly ScriptOptions DefaultScriptOptions = ScriptOptions.Default
            .WithAllowUnsafe(true)
            .WithOptimizationLevel(OptimizationLevel.Debug)
            .WithLanguageVersion(LanguageVersion.Preview)
            .WithFilePath("Script")
            .WithFileEncoding(Encoding.Unicode)
            .WithWarningLevel(0)
            .WithEmitDebugInformation(true);

        // Since the completion feature of Roslyn requires us to work with a Document which requires a project in a
        // workspace, we have to set up a whole infrastructure, but several parts never changes. The following
        // declarations are for this so it's purely for autocomplete to work
        private static readonly AdhocWorkspace Workspace = new();

        private static readonly CSharpCompilationOptions CSharpCompilationOptions = new
            (
                outputKind: OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                optimizationLevel: OptimizationLevel.Debug,
                warningLevel: 0,
                metadataImportOptions: MetadataImportOptions.All
            );

        private static readonly ProjectInfo ProjectInfo = ProjectInfo.Create(
                id: ProjectId.CreateNewId(),
                version: VersionStamp.Create(),
                name: string.Empty,
                assemblyName: string.Empty,
                language: LanguageNames.CSharp,
                hostObjectType: typeof(RoslynScriptInteraction),
                parseOptions: CSharpParseOptions.Default
                    .WithLanguageVersion(LanguageVersion.Preview)
                    .WithKind(SourceCodeKind.Script),
                isSubmission: true);

        private static readonly DocumentInfo DocumentInfo = DocumentInfo.Create(
            id: DocumentId.CreateNewId(ProjectInfo.Id),
            name: string.Empty,
            sourceCodeKind: SourceCodeKind.Script);

        // Finally, the following fields are to persist information between submissions
        private CompletionList _completionList = CompletionList.Empty;
        internal Script<object> LastScript { get; private set; }
        internal ScriptState<object> LastScriptState { get; private set; }
        internal HashSet<string> UsingsDirectives { get; } = new(StringComparer.Ordinal);
        private readonly RoslynScriptInteraction _interaction;
        private readonly List<MetadataReference> _references = [];

        // These assemblies contain a ton of definitions, enough to add about 1000 items to every completion fetch.
        // In practice, it's not very useful to have these while writing script so we just don't reference them to
        // make autocomplete less bloated
        private readonly string[] _blacklistAssembliesReferences =
        [
            "Microsoft.CodeAnalysis",
            "Microsoft.CodeAnalysis.Features",
            "Microsoft.CodeAnalysis.Workspaces",
            "Microsoft.CodeAnalysis.AnalyzerUtilities",
            "Microsoft.CodeAnalysis.Elfie",
            "Microsoft.CodeAnalysis.CSharp",
            "Microsoft.CodeAnalysis.CSharp.Features",
            "Microsoft.CodeAnalysis.CSharp.Workspaces",
            "Microsoft.CodeAnalysis.CSharp.Scripting"
        ];

        private const uint IgnoreAccessibilityBinderFlag = 1 << 22;

        static RoslynScriptEvaluator()
        {
            ExplorerCore.Harmony.PatchAll(typeof(RoslynScriptEvaluator));
        }

        // This is necessary to have every private or other restricted access modifiers be ignored. The metadata import
        // option is to make them visible when compiling and the binder flag to ignore the restrictions if any.
        // The patch is needed because it's not possible to specify the metadata import option for a script and even if
        // we could, the binder flag setting is internal so we have to patch either way.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CSharpCompilation), nameof(CSharpCompilation.CreateScriptCompilation),
            typeof(string), typeof(SyntaxTree), typeof(IEnumerable<MetadataReference>),
            typeof(CSharpCompilationOptions), typeof(CSharpCompilation), typeof(Type), typeof(Type))]
        public static bool prefix_CreateScriptCompilation(ref CSharpCompilationOptions __3)
        {            
            TopLevelBinderFlagsSetter.Invoke(__3, [IgnoreAccessibilityBinderFlag | (uint)TopLevelBinderFlagsGetter.Invoke(__3, null)]);
            __3 = __3.WithMetadataImportOptions(MetadataImportOptions.All);
            return true;
        }

        public RoslynScriptEvaluator()
        {
            _interaction = new RoslynScriptInteraction(this);
            ImportAppdomainAssemblies();
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        }

        public void AddUsingDirective(string directive)
        {
            UsingsDirectives.Add(directive);
        }

        public async Task<ScriptState<object>> Compile(string code)
        {
            // First compilation
            if (LastScriptState is null)
            {
                var firstScript = CSharpScript.Create<object>(code,
                    DefaultScriptOptions
                        .AddImports(UsingsDirectives)
                        .WithReferences(_references),
                    typeof(RoslynScriptInteraction));
                // Doing this guarantees that we have a valid script so our Interaction class can interact with it
                CompileAndCheckScript(firstScript);
                LastScript = firstScript;
                var firstScriptState = await LastScript.RunAsync(_interaction, _ => true);
                if (firstScriptState.Exception is not null)
                    return firstScriptState;
                LastScriptState = firstScriptState;
                return LastScriptState;
            }

            // Here, this is the second or further compilation so we have to chain the script from its previous state
            var script = LastScript.ContinueWith(code,
                DefaultScriptOptions
                    .WithImports(UsingsDirectives)
                    .WithReferences(_references));
            // Doing this guarantees that we have a valid script so our Interaction class can interact with it
            CompileAndCheckScript(script);
            LastScript = script;
            var lastScriptState = await LastScript.RunFromAsync(LastScriptState, _ => true);
            if (lastScriptState.Exception is not null)
            {
                LastScript = (Script<object>)script.Previous;
                return lastScriptState;
            }
            LastScriptState = lastScriptState;
            // Since it's not possible to get every import of every compilation, we just cumulate them manually
            var comp = LastScriptState.Script.GetCompilation();
            var unit = comp.SyntaxTrees.First().GetCompilationUnitRoot();
            var usings = unit.Usings;
            foreach (var usingDirectiveSyntax in usings)
            {
                if (usingDirectiveSyntax.Name is null)
                    continue;

                UsingsDirectives.Add(usingDirectiveSyntax.Name.ToString());
            }

            return LastScriptState;
        }

        // Running the script normally would have done this throw for us, but doing it early with this method lets us
        // make sure that the last script is valid
        private static void CompileAndCheckScript(Script<object> firstScript)
        {
            var diagnostics = firstScript.Compile();
            var firstError = diagnostics.FirstOrDefault(x => x.Severity == DiagnosticSeverity.Error);
            if (firstError is not null)
                throw new CompilationErrorException(firstError.GetMessage(), diagnostics);
        }

        // Exceptions thrown by scripts have traces that are overly verbose because of the whole await machinery.
        // To make them more presentable, we strip any additional stack frames and format the rest similarly to how
        // it would be formatted
        public static string FormatScriptException(Exception ex)
        {
            var strippedStackTrace = new StackTrace(ex, true);
            StackTraceFrames.SetValue(strippedStackTrace, (StackFrame[])[]);
            StringBuilder sb = new();
            sb.Append(ex.GetType());
            if (!string.IsNullOrEmpty(ex.Message))
            {
                sb.Append(": ");
                sb.Append(ex.Message);
            }

            if (ex.InnerException is not null)
            {
                sb.Append(" ---> ");
                sb.AppendLine(ex.InnerException.ToString());
                sb.Append("   --- End of inner exception stack trace ---");
            }

            string strippedStackTraceString = strippedStackTrace.ToString();
            if (!string.IsNullOrEmpty(strippedStackTraceString))
            {
                sb.AppendLine();
                sb.Append(strippedStackTraceString);
            }

            // Removes the "end of previous location" message so it appears as if the stacktrace ends at the script
            string formattedMessage = sb.ToString().TrimEnd();
            int lastNewLineIndex = formattedMessage.LastIndexOf('\n');
            return formattedMessage.Substring(0, lastNewLineIndex);
        }

        public async Task<(ImmutableArray<CompletionItem> items, string prefix)> GetCompletions(string code, int caretPosition, char? insertChar)
        {
            // We need to tear everything down and build it back, but everything that never changes are static fields
            Workspace.ClearSolution();

            var scriptProjectInfo = ProjectInfo
                .WithCompilationOptions(CSharpCompilationOptions
                    .WithUsings(UsingsDirectives))
                .WithMetadataReferences(_references);
            var scriptProject = Workspace.AddProject(scriptProjectInfo);

            var sourceText = SourceText.From(code);
            var scriptDocumentInfo = DocumentInfo
                .WithId(DocumentId.CreateNewId(scriptProject.Id))
                .WithTextLoader(TextLoader.From(TextAndVersion.Create(sourceText, VersionStamp.Create())));
            var scriptDocument = Workspace.AddDocument(scriptDocumentInfo);

            var completionService = CompletionService.GetService(scriptDocument);
            if (completionService is null)
                return ([], string.Empty);

            CompletionTrigger trigger = CompletionTrigger.Invoke;
            if (insertChar is not null)
                trigger = CompletionTrigger.CreateInsertionTrigger(insertChar.Value);

            // The way completion work is very different from mcs because it's meant to be a more incremental process.
            // We don't want to fetch everytime and Roslyn can tell us if we should be fetching depending on how the
            // completion was triggered (which is always for invoking manually and sometimes when inserting a character).
            // If Roslyn tells us that we don't need to fetch, it means the user is likely typing and the result of the
            // last fetch is still valid, but the filtering needs to be updated. In such case, Roslyn can tell us the new
            // span to get the filter for so that we can run the filtering on the last CompletionList. This allows completion
            // to be VERY fast when typing a word because only the filtering part happens
            string prefix;
            ImmutableArray<CompletionItem> items;
            if (_completionList.ItemsList.Count > 0 && !completionService.ShouldTriggerCompletion(sourceText, caretPosition, trigger))
            {
                var textSpan = completionService.GetDefaultCompletionListSpan(sourceText, caretPosition);
                prefix = code.Substring(textSpan.Start, textSpan.Length);
                items = completionService.FilterItems(scriptDocument, [.._completionList.ItemsList], prefix);
                return (items, prefix);
            }

            _completionList = await completionService.GetCompletionsAsync(scriptDocument, caretPosition, trigger);
            if (_completionList.Span.IsEmpty)
                return ([.._completionList.ItemsList], string.Empty);

            prefix = code.Substring(_completionList.Span.Start, _completionList.Span.Length);
            items = completionService.FilterItems(scriptDocument, [.._completionList.ItemsList], prefix);
            return (items, prefix);
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        }

        private bool AssemblyIsBackedByFile(Assembly assembly)
        {
            return !assembly.IsDynamic
                   && !string.IsNullOrEmpty(assembly.Location)
                   && !assembly.Location.StartsWith("data-");
        }

        private void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            if (_blacklistAssembliesReferences.Contains(args.LoadedAssembly.GetName().Name))
                return;

            if (!AssemblyIsBackedByFile(args.LoadedAssembly))
            {
                ReferenceAssemblyInMemory(args.LoadedAssembly);
                return;
            }

            ReferenceAssemblyWithLocation(args.LoadedAssembly);
        }

        private void ReferenceAssemblyWithLocation(Assembly asm)
        {
            string name = asm.GetName().Name;

            foreach (string blacklisted in ConfigManager.CSConsole_Assembly_Blacklist.Value.Split(';'))
            {
                string bl = blacklisted;
                if (bl.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    bl = blacklisted.Substring(0, bl.Length - 4);
                if (string.Equals(bl, name, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            _references.Add(MetadataReference.CreateFromFile(asm.Location));
        }

        // Since we can't load from a file, we need to find the PE image in memory. This isn't possible to obtain in a
        // managed way, but we can get it via the ManifestModule's backing MonoImage struct since it contains the data
        // pointer and its length. Mono internally will store the pointer as the "_impl" field.
        private unsafe void ReferenceAssemblyInMemory(Assembly asm)
        {
            // This trick only works for MonoAssembly so an AssemblyBuilder wouldn't work
            if (asm.GetType().Name != "MonoAssembly")
                return;
            // This prevents script generated assemblies from being referenced unnecessarily
            if (asm.GetName().Name.StartsWith("ℛ*"))
                return;

            MonoImage* monoImage = (MonoImage*)(nint)ModuleImplField.GetValue(asm.ManifestModule);
            var memoryStream = new UnmanagedMemoryStream(monoImage->rawData, monoImage->rawDataLen);
            var metaRef = MetadataReference.CreateFromStream(memoryStream);
            _references.Add(metaRef);
        }

        private void ImportAppdomainAssemblies()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (_blacklistAssembliesReferences.Contains(assembly.GetName().Name))
                    continue;

                if (!AssemblyIsBackedByFile(assembly))
                {
                    ReferenceAssemblyInMemory(assembly);
                    continue;
                }

                ReferenceAssemblyWithLocation(assembly);
            }
        }
    }
}
#endif
