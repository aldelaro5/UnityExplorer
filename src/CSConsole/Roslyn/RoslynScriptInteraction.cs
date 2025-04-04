#if NET472
using HarmonyLib;
using System.Collections;
using System.Text;
using UnityExplorer.UI.Panels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Scripting;
using TypeInfo = System.Reflection.TypeInfo;

namespace UnityExplorer.CSConsole
{
    public class RoslynScriptInteraction
    {
        private readonly MethodInfo _scriptExecutionStateGetter = AccessTools.PropertyGetter(typeof(ScriptState), "ExecutionState");
        private readonly RoslynScriptEvaluator _scriptEvaluator;
        internal RoslynScriptInteraction(RoslynScriptEvaluator scriptEvaluator) => _scriptEvaluator = scriptEvaluator;

        public object CurrentTarget
            => InspectorManager.ActiveInspector?.Target;

        public object[] AllTargets
            => InspectorManager.Inspectors.Select(it => it.Target).ToArray();

        public void Log(object message)
            => ExplorerCore.Log(message);

        public void Inspect(object obj)
            => InspectorManager.Inspect(obj);

        public void Inspect(Type type)
            => InspectorManager.Inspect(type);

        public Coroutine Start(IEnumerator ienumerator)
            => RuntimeHelper.StartCoroutine(ienumerator);

        public void Stop(Coroutine coro)
            => RuntimeHelper.StopCoroutine(coro);

        public void Copy(object obj)
            => ClipboardPanel.Copy(obj);

        public object Paste()
            => ClipboardPanel.Current;

        public void GetUsing()
        {
            // This covers everything the evaluator cumulated before the executing script
            HashSet<string> usings = new(StringComparer.Ordinal);
            foreach (string import in _scriptEvaluator.UsingsDirectives)
                usings.Add(import);

            // To cover everything the executing script has, we get its compilation and find all its Usings.
            // This should be fast because the script was already compiled so the compilation is cached by this point
            Compilation comp = _scriptEvaluator.LastScript is not null
                ? _scriptEvaluator.LastScript.GetCompilation()
                : _scriptEvaluator.LastScriptState.Script.GetCompilation();
            var unit = comp.SyntaxTrees.First().GetCompilationUnitRoot();
            var usingDirectives = unit.Usings;
            foreach (var usingDirectiveSyntax in usingDirectives)
            {
                if (usingDirectiveSyntax.Name is null)
                    continue;

                usings.Add(usingDirectiveSyntax.Name.ToString());
            }

            string allUsings = string.Join("\n", usings.Select(x => $"using {x};"));
            ExplorerCore.Log($"{allUsings}");
        }

        public void GetVars()
        {
            // These only covers the cumulated vars up to this script, but getting the ones defined currently
            // is way too complex to be practical
            HashSet<string> vars = new(StringComparer.Ordinal);
            if (_scriptEvaluator.LastScriptState is not null)
            {
                foreach (var variable in _scriptEvaluator.LastScriptState.Variables)
                    vars.Add($"{variable.Type} {variable.Name} = {variable.Value}");
            }

            if (vars.Count == 0)
            {
                ExplorerCore.LogWarning("No variables seem to be defined!");
                return;
            }
            ExplorerCore.Log($"{string.Join("\n", vars)}");
        }

        public void GetClasses()
        {
            if (_scriptEvaluator.LastScriptState is null)
            {
                ExplorerCore.LogWarning("No classes seem to be defined.");
                return;
            }

            // Getting the types defined in the script is a bit trickier: we can't rely on the compilation because it
            // only contains the current script's classes. However, we can use a similar technique that Roslyn does
            // to gather the script variables where we go through the submissions, get their object and get all types
            // under them which we can then cumulate. Unfortunately, the submissions are internal so we have to
            // reflection our way through this. Once again, it only covers the types defined prior.
            object executionState = _scriptExecutionStateGetter.Invoke(_scriptEvaluator.LastScriptState, null);
            object[] submissionStates = (object[])AccessTools.Field(executionState
                .GetType(), "_submissionStates").GetValue(executionState);
            Dictionary<string, MemberInfo> members = new();
            // Roslyn excludes the first one because it's the globals object which is an instance of this class
            for (int i = 1; i < submissionStates.Length; i++)
            {
                object submissionState = submissionStates[i];
                if (submissionState is null)
                    continue;

                foreach (var member in submissionState.GetType().GetTypeInfo().DeclaredMembers)
                {
                    // This is how Roslyn filters for variables, might as well do the same here so we don't get compiler
                    // generated stuff
                    if (member.Name.Length <= 0 || (!char.IsLetterOrDigit(member.Name[0]) && member.Name[0] != '_'))
                        continue;
                    if (members.ContainsKey(member.Name))
                        continue;
                    if (member is not TypeInfo)
                        continue;

                    members.Add(member.Name, member);
                }
            }

            if (members.Count == 0)
            {
                ExplorerCore.LogWarning("No classes seem to be defined.");
                return;
            }

            StringBuilder sb = new();
            sb.Append($"There are {members.Count} defined classes:");
            foreach (var memberInfo in members.Values)
            {
                sb.Append($"\n\n{memberInfo.Name}:");
                var typeInfo = (TypeInfo)memberInfo;
                foreach (var member in typeInfo.DeclaredMembers)
                {
                    if (member.MemberType == MemberTypes.Method && ((MethodInfo)member).IsSpecialName)
                        continue;
                    // These discards compiler generated fields such as property backing fields
                    if (member.MemberType == MemberTypes.Field && (member.Name.Contains("<") || member.Name.Contains(">")))
                        continue;
                    sb.Append($"\n\t- {member.MemberType}: \"{member}\"");
                }
            }
            ExplorerCore.Log(sb.ToString());
        }
    }
}
#endif
