using System.Collections;
using System.Text;
using UnityExplorer.UI;
using UnityExplorer.UI.Panels;
using UniverseLib.Input;
using UniverseLib.UI.Models;
#if NET472
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Scripting;
#else
using Mono.CSharp;
#endif

namespace UnityExplorer.CSConsole
{
    public static class ConsoleController
    {
#if NET472
        public static RoslynScriptEvaluator Evaluator { get; private set; }
#else
        public static McsScriptEvaluator Evaluator { get; private set; }
#endif
        public static LexerBuilder Lexer { get; private set; }
        public static CSAutoCompleter Completer { get; private set; }

        public static bool SRENotSupported { get; private set; }
        public static int LastCaretPosition { get; private set; }
        public static float DefaultInputFieldAlpha { get; set; }

        public static bool EnableCtrlRShortcut { get; private set; } = true;
        public static bool EnableAutoIndent { get; private set; } = true;
        public static bool EnableSuggestions { get; private set; } = true;

        public static CSConsolePanel Panel => UIManager.GetPanel<CSConsolePanel>(UIManager.Panels.CSConsole);
        public static InputFieldRef Input => Panel.Input;

        public static string ScriptsFolder => Path.Combine(ExplorerCore.ExplorerFolder, "Scripts");

#if !NET472
        static HashSet<string> usingDirectives;
        static StringBuilder evaluatorOutput;
        static StringWriter evaluatorStringWriter;
#endif

        static float timeOfLastCtrlR;

        static bool settingCaretCoroutine;
        static string previousInput;
        static int previousContentLength = 0;

        static readonly string[] DefaultUsing =
        [
            "System",
            "System.Linq",
            "System.Text",
            "System.Collections",
            "System.Collections.Generic",
            "System.Reflection",
            "UnityEngine",
            "UniverseLib",
#if IL2CPP
            "Il2CppInterop.Runtime",
            "Il2CppInterop.Runtime.Attributes",
            "Il2CppInterop.Runtime.Injection",
            "Il2CppInterop.Runtime.InteropTypes.Arrays",
#endif
        ];

        const int CSCONSOLE_LINEHEIGHT = 18;

#if NET472
        public static async Task Init()
#else
        public static void Init()
#endif
        {
            try
            {
                ResetConsole(false);
                // ensure the compiler is supported (if this fails then SRE is probably stripped)
#if NET472
                var state = await Evaluator.Compile("0 == 0");
                if (state.Exception is not null)
                    throw state.Exception;
#else
                Evaluator.Compile("0 == 0");
#endif
            }
            catch (Exception ex)
            {
                DisableConsole(ex);
                return;
            }

#if NET472
            try
            {
                // Prepare the autocomplete so it gets faster after
                await Evaluator.GetCompletions("", 0, null);
            }
            catch (Exception ex)
            {
                ExplorerCore.LogError(ex.ToString());
            }
#endif

            // Setup console
            Lexer = new LexerBuilder();
            Completer = new CSAutoCompleter();

            SetupHelpInteraction();

            Panel.OnInputChanged += OnInputChanged;
            Panel.InputScroller.OnScroll += OnInputScrolled;
            Panel.OnCompileClicked += Evaluate;
            Panel.OnResetClicked += ResetConsole;
            Panel.OnHelpDropdownChanged += HelpSelected;
            Panel.OnAutoIndentToggled += OnToggleAutoIndent;
            Panel.OnCtrlRToggled += OnToggleCtrlRShortcut;
            Panel.OnSuggestionsToggled += OnToggleSuggestions;
            Panel.OnPanelResized += OnInputScrolled;

            // Run startup script
            try
            {
                if (!Directory.Exists(ScriptsFolder))
                    Directory.CreateDirectory(ScriptsFolder);

                string startupPath = Path.Combine(ScriptsFolder, "startup.cs");
                if (File.Exists(startupPath))
                {
                    ExplorerCore.Log($"Executing startup script from '{startupPath}'...");
                    string text = File.ReadAllText(startupPath);
                    Input.Text = text;
#if NET472
                    await Evaluate(Input.Text);
#else
                    Evaluate();
#endif
                }
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning($"Exception executing startup script: {ex}");
            }
        }

        #region Evaluating

#if !NET472
        static void GenerateTextWriter()
        {
            evaluatorOutput = new StringBuilder();
            evaluatorStringWriter = new StringWriter(evaluatorOutput);
        }
#endif

        public static void ResetConsole() => ResetConsole(true);

        public static void ResetConsole(bool logSuccess)
        {
            if (SRENotSupported)
                return;

            if (Evaluator != null)
                Evaluator.Dispose();

#if NET472
            Evaluator = new();
#else
            GenerateTextWriter();
            Evaluator = new McsScriptEvaluator(evaluatorStringWriter)
            {
                InteractiveBaseClass = typeof(McsScriptInteraction)
            };
            usingDirectives = new HashSet<string>();
#endif

            foreach (string use in DefaultUsing)
                AddUsing(use);

            if (logSuccess)
                ExplorerCore.Log($"C# Console reset");//. Using directives:\r\n{Evaluator.GetUsing()}");
        }

        public static void AddUsing(string assemblyName)
        {
#if NET472
            Evaluator.AddUsingDirective(assemblyName);
#else
            if (!usingDirectives.Contains(assemblyName))
            {
                Evaluate($"using {assemblyName};", true);
                usingDirectives.Add(assemblyName);
            }
#endif
        }

#if NET472
        public static async void Evaluate()
#else
        public static void Evaluate()
#endif
        {
            try
            {
                if (SRENotSupported)
                    return;

#if NET472
                await Evaluate(Input.Text);
#else
                Evaluate(Input.Text);
#endif
            }
            catch (Exception e)
            {
                ExplorerCore.LogError(e);
            }
        }

#if NET472
        public static async Task Evaluate(string input, bool supressLog = false)
#else
        public static void Evaluate(string input, bool supressLog = false)
#endif
        {
            if (SRENotSupported)
                return;

#if NET472
            try
            {
                var repl = await Evaluator.Compile(input);
                if (repl.Exception is not null)
                {
                    if (!supressLog)
                    {
                        ExplorerCore.LogWarning($"An exception was thrown when executing the code: " +
                                                $"{RoslynScriptEvaluator.FormatScriptException(repl.Exception)}");
                    }
                    return;
                }
                ExplorerCore.Log(repl.ReturnValue is not null
                    ? $"Invoked REPL, result: {repl.ReturnValue}"
                    : "Invoked REPL (no return value)");
            }
            catch (CompilationErrorException ex)
            {
                if (!supressLog)
                {
                    var errors = ex.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error).Select(x => x.ToString());
                    ExplorerCore.LogWarning($"Unable to compile the code:\n{string.Join("\n", errors)}");
                }
            }
#else
            if (evaluatorStringWriter == null || evaluatorOutput == null)
            {
                GenerateTextWriter();
                Evaluator._textWriter = evaluatorStringWriter;
            }

            try
            {
                // Compile the code. If it returned a CompiledMethod, it is REPL.
                CompiledMethod repl = Evaluator.Compile(input);

                if (repl != null)
                {
                    // Valid REPL, we have a delegate to the evaluation.
                    try
                    {
                        object ret = null;
                        repl.Invoke(ref ret);
                        string result = ret?.ToString();
                        if (!string.IsNullOrEmpty(result))
                            ExplorerCore.Log($"Invoked REPL, result: {ret}");
                        else
                            ExplorerCore.Log($"Invoked REPL (no return value)");
                    }
                    catch (Exception ex)
                    {
                        ExplorerCore.LogWarning($"Exception invoking REPL: {ex}");
                    }
                }
                else
                {
                    // The compiled code was not REPL, so it was a using directive or it defined classes.

                    string output = Evaluator._textWriter.ToString().Trim();
                    evaluatorOutput.Clear();

                    if (McsScriptEvaluator._reportPrinter.ErrorsCount > 0)
                        throw new FormatException($"Unable to compile the code. Evaluator's last output was:\r\n{output}");
                    else if (!supressLog)
                        ExplorerCore.Log($"Code compiled without errors.");
                }
            }
            catch (FormatException fex)
            {
                if (!supressLog)
                    ExplorerCore.LogWarning(fex.Message);
            }
#endif
            catch (Exception ex)
            {
                if (!supressLog)
                    ExplorerCore.LogWarning(ex);
            }
        }

        #endregion


        #region Update loop and event listeners

#if NET472
        public static async Task Update()
#else
        public static void Update()
#endif
        {
            if (SRENotSupported)
                return;

            if (!InputManager.GetKey(KeyCode.LeftControl) && !InputManager.GetKey(KeyCode.RightControl))
            {
                if (InputManager.GetKeyDown(KeyCode.Home))
                    JumpToStartOrEndOfLine(true);
                else if (InputManager.GetKeyDown(KeyCode.End))
                    JumpToStartOrEndOfLine(false);
            }

            UpdateCaret(out bool caretMoved);

            if (!settingCaretCoroutine && EnableSuggestions)
            {
                if (AutoCompleteModal.CheckEscape(Completer))
                {
                    OnAutocompleteEscaped();
                    return;
                }

                if (caretMoved)
                    AutoCompleteModal.Instance.ReleaseOwnership(Completer);
            }

            if (EnableCtrlRShortcut
                && (InputManager.GetKey(KeyCode.LeftControl) || InputManager.GetKey(KeyCode.RightControl))
                && InputManager.GetKeyDown(KeyCode.R)
                && timeOfLastCtrlR.OccuredEarlierThanDefault())
            {
                timeOfLastCtrlR = Time.realtimeSinceStartup;
#if NET472
                await Evaluate(Panel.Input.Text);
#else
                Evaluate(Panel.Input.Text);
#endif
            }

            if (EnableSuggestions && !settingCaretCoroutine
                                  && (InputManager.GetKey(KeyCode.LeftControl)
                                      || InputManager.GetKey(KeyCode.RightControl))
                                  && InputManager.GetKeyDown(KeyCode.Space)
                                  && timeOfLastCtrlR.OccuredEarlierThanDefault())
            {
                HighlightVisibleInput(out bool inStringOrComment);
                if (!inStringOrComment)
                {
                    timeOfLastCtrlR = Time.realtimeSinceStartup;
#if NET472
                    await Completer.CheckAutocompletes(null);
#else
                    Completer.CheckAutocompletes();
#endif
                }
            }
        }

        static void OnInputScrolled() => HighlightVisibleInput(out _);

#if NET472
        static async void OnInputChanged(string value)
#else
        static void OnInputChanged(string value)
#endif
        {
            try
            {
                if (SRENotSupported)
                    return;

                // If Escape was pressed, the input got cancelled which we need to undo and handle AutoComplete exit
                if (InputManager.GetKeyDown(KeyCode.Escape))
                {
                    // The cancel wipes the text so it needs to be restored
                    Input.Text = previousInput;

                    if (EnableSuggestions && AutoCompleteModal.CheckEscape(Completer))
                        OnAutocompleteEscaped();
                
                    // The cancel unfocused the input, we need to undo the cancel and give back focus
                    Input.Component.m_AllowInput = true;
                    Input.Component.m_WasCanceled = false;
                    // A cancel causes the caret to go back to the start, we need to undo that
                    Input.Component.caretPosition = LastCaretPosition;
                    return;
                }

                previousInput = value;

                if (EnableSuggestions && AutoCompleteModal.CheckEnter(Completer))
                    OnAutocompleteEnter();

                if (!settingCaretCoroutine)
                {
                    if (EnableAutoIndent)
                        DoAutoIndent();
                }

                HighlightVisibleInput(out bool inStringOrComment);

                if (!settingCaretCoroutine && EnableSuggestions)
                {
                    if (inStringOrComment)
                    {
                        AutoCompleteModal.Instance.ReleaseOwnership(Completer);
                    }
                    else if (Input.Text.Length > 0)
                    {
#if NET472
                        int caret = Math.Max(0, Math.Min(Input.Text.Length - 1, Input.Component.caretPosition - 1));
                        await Completer.CheckAutocompletes(Input.Text[caret]);
#else
                        Completer.CheckAutocompletes();
#endif
                    }
                }

                UpdateCaret(out _);
            }
            catch (Exception e)
            {
                ExplorerCore.LogError(e);
            }
        }

        static void OnToggleAutoIndent(bool value)
        {
            EnableAutoIndent = value;
        }

        static void OnToggleCtrlRShortcut(bool value)
        {
            EnableCtrlRShortcut = value;
        }

        static void OnToggleSuggestions(bool value)
        {
            EnableSuggestions = value;
        }

        #endregion


        #region Caret position

        static void UpdateCaret(out bool caretMoved)
        {
            int prevCaret = LastCaretPosition;
            caretMoved = false;

            // Override up/down arrow movement when autocompleting
            if (EnableSuggestions && AutoCompleteModal.CheckNavigation(Completer))
            {
                Input.Component.caretPosition = LastCaretPosition;
                return;
            }

            if (Input.Component.isFocused)
            {
                LastCaretPosition = Input.Component.caretPosition;
                caretMoved = LastCaretPosition != prevCaret;
            }

            if (Input.Text.Length == 0)
                return;

            // If caret moved, ensure caret is visible in the viewport
            if (caretMoved)
            {
                UICharInfo charInfo = Input.TextGenerator.characters[LastCaretPosition];
                float charTop = charInfo.cursorPos.y;
                float charBot = charTop - CSCONSOLE_LINEHEIGHT;

                float viewportMin = Input.Transform.rect.height - Input.Transform.anchoredPosition.y - (Input.Transform.rect.height * 0.5f);
                float viewportMax = viewportMin - Panel.InputScroller.ViewportRect.rect.height;

                float diff = 0f;
                if (charTop > viewportMin)
                    diff = charTop - viewportMin;
                else if (charBot < viewportMax)
                    diff = charBot - viewportMax;

                if (Math.Abs(diff) > 1)
                {
                    RectTransform rect = Input.Transform;
                    rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, rect.anchoredPosition.y - diff);
                }
            }
        }

        public static void SetCaretPosition(int caretPosition)
        {
            Input.Component.caretPosition = caretPosition;

            // Fix to make sure we always really set the caret position.
            // Yields a frame and fixes text-selection issues.
            settingCaretCoroutine = true;
            Input.Component.readOnly = true;
            RuntimeHelper.StartCoroutine(DoSetCaretCoroutine(caretPosition));
        }

        static IEnumerator DoSetCaretCoroutine(int caretPosition)
        {
            Color color = Input.Component.selectionColor;
            color.a = 0f;
            Input.Component.selectionColor = color;

            EventSystemHelper.SetSelectionGuard(false);
            Input.Component.Select();

            yield return null; // ~~~~~~~ YIELD FRAME ~~~~~~~~~

            Input.Component.caretPosition = caretPosition;
            Input.Component.selectionFocusPosition = caretPosition;
            LastCaretPosition = Input.Component.caretPosition;

            color.a = DefaultInputFieldAlpha;
            Input.Component.selectionColor = color;

            Input.Component.readOnly = false;
            settingCaretCoroutine = false;
        }

        // For Home and End keys
        static void JumpToStartOrEndOfLine(bool toStart)
        {
            // Determine the current and next line
            UILineInfo thisline = default;
            UILineInfo? nextLine = null;
            for (int i = 0; i < Input.Component.cachedInputTextGenerator.lineCount; i++)
            {
                UILineInfo line = Input.Component.cachedInputTextGenerator.lines[i];

                if (line.startCharIdx > LastCaretPosition)
                {
                    nextLine = line;
                    break;
                }
                thisline = line;
            }

            if (toStart)
            {
                // Determine where the indented text begins
                int endOfLine = nextLine == null ? Input.Text.Length : nextLine.Value.startCharIdx;
                int indentedStart = thisline.startCharIdx;
                while (indentedStart < endOfLine - 1 && char.IsWhiteSpace(Input.Text[indentedStart]))
                    indentedStart++;

                // Jump to either the true start or the non-whitespace position,
                // depending on which one we are not at.
                if (LastCaretPosition == indentedStart)
                    SetCaretPosition(thisline.startCharIdx);
                else
                    SetCaretPosition(indentedStart);
            }
            else
            {
                // If there is no next line, jump to the end of this line (+1, to the invisible next character position)
                if (nextLine == null)
                    SetCaretPosition(Input.Text.Length);
                else // jump to the next line start index - 1, ie. end of this line
                    SetCaretPosition(nextLine.Value.startCharIdx - 1);
            }
        }

        #endregion


        #region Lexer Highlighting

        private static void HighlightVisibleInput(out bool inStringOrComment)
        {
            inStringOrComment = false;
            if (string.IsNullOrEmpty(Input.Text))
            {
                Panel.HighlightText.text = "";
                Panel.LineNumberText.text = "1";
                return;
            }

            // Calculate the visible lines

            int topLine = -1;
            int bottomLine = -1;

            // the top and bottom position of the viewport in relation to the text height
            // they need the half-height adjustment to normalize against the 'line.topY' value.
            float viewportMin = Input.Transform.rect.height - Input.Transform.anchoredPosition.y - (Input.Transform.rect.height * 0.5f);
            float viewportMax = viewportMin - Panel.InputScroller.ViewportRect.rect.height;

            for (int i = 0; i < Input.TextGenerator.lineCount; i++)
            {
                UILineInfo line = Input.TextGenerator.lines[i];
                // if not set the top line yet, and top of line is below the viewport top
                if (topLine == -1 && line.topY <= viewportMin)
                    topLine = i;
                // if bottom of line is below the viewport bottom
                if ((line.topY - line.height) >= viewportMax)
                    bottomLine = i;
            }

            topLine = Math.Max(0, topLine - 1);
            bottomLine = Math.Min(Input.TextGenerator.lineCount - 1, bottomLine + 1);

            int startIdx = Input.TextGenerator.lines[topLine].startCharIdx;
            int endIdx = (bottomLine >= Input.TextGenerator.lineCount - 1)
                ? Input.Text.Length - 1
                : (Input.TextGenerator.lines[bottomLine + 1].startCharIdx - 1);


            // Highlight the visible text with the LexerBuilder

            Panel.HighlightText.text = Lexer.BuildHighlightedString(Input.Text, startIdx, endIdx, topLine, LastCaretPosition, out inStringOrComment);

            // Set the line numbers

            // determine true starting line number (not the same as the cached TextGenerator line numbers)
            int realStartLine = 0;
            for (int i = 0; i < startIdx; i++)
            {
                if (LexerBuilder.IsNewLine(Input.Text[i]))
                    realStartLine++;
            }
            realStartLine++;
            char lastPrev = '\n';

            StringBuilder sb = new();

            // append leading new lines for spacing (no point rendering line numbers we cant see)
            for (int i = 0; i < topLine; i++)
                sb.Append('\n');

            // append the displayed line numbers
            for (int i = topLine; i <= bottomLine; i++)
            {
                if (i > 0)
                    lastPrev = Input.Text[Input.TextGenerator.lines[i].startCharIdx - 1];

                // previous line ended with a newline character, this is an actual new line.
                if (LexerBuilder.IsNewLine(lastPrev))
                {
                    sb.Append(realStartLine.ToString());
                    realStartLine++;
                }

                sb.Append('\n');
            }

            Panel.LineNumberText.text = sb.ToString();

            return;
        }

        #endregion


        #region Autocompletes

        public static void InsertSuggestionAtCaret(string suggestion)
        {
            settingCaretCoroutine = true;
            int startIdx = LastCaretPosition;
            // get the current composition string (from caret back to last delimiter)
            while (startIdx > 0)
            {
                startIdx--;
                char c = Input.Text[startIdx];
                if (Completer.delimiters.Contains(c) || char.IsWhiteSpace(c) || c == '.')
                {
                    startIdx++;
                    break;
                }
            }
            string trimmedInput = Input.Text.Remove(startIdx, LastCaretPosition - startIdx);
            Input.Text = trimmedInput.Insert(startIdx, suggestion);

            SetCaretPosition(startIdx + suggestion.Length);
            LastCaretPosition = Input.Component.caretPosition;
        }

        private static void OnAutocompleteEnter()
        {
            // Remove the new line
            int lastIdx = Input.Component.caretPosition - 1;
            Input.Text = Input.Text.Remove(lastIdx, 1);

            // Use the selected suggestion
            Input.Component.caretPosition = LastCaretPosition;
            Completer.OnSuggestionClicked(AutoCompleteModal.SelectedSuggestion);
        }

        private static void OnAutocompleteEscaped()
        {
            AutoCompleteModal.Instance.ReleaseOwnership(Completer);
            SetCaretPosition(LastCaretPosition);
        }


        #endregion


        #region Auto indenting

        private static void DoAutoIndent()
        {
            if (Input.Text.Length > previousContentLength)
            {
                int inc = Input.Text.Length - previousContentLength;

                if (inc == 1)
                {
                    int caret = Input.Component.caretPosition;
                    Input.Text = Lexer.IndentCharacter(Input.Text, ref caret);
                    Input.Component.caretPosition = caret;
                    LastCaretPosition = caret;
                }
                else
                {
                    // todo indenting for copy+pasted content

                    //ExplorerCore.Log("Content increased by " + inc);
                    //var comp = Input.Text.Substring(PreviousCaretPosition, inc);
                    //ExplorerCore.Log("composition string: " + comp);
                }
            }

            previousContentLength = Input.Text.Length;
        }

        #endregion


        #region "Help" interaction

        private static void DisableConsole(Exception ex)
        {
            SRENotSupported = true;
            Input.Component.readOnly = true;
            Input.Component.textComponent.color = "5d8556".ToColor();

            if (ex is NotSupportedException)
            {
                Input.Text = $@"The C# Console has been disabled because System.Reflection.Emit threw a NotSupportedException.

Easy, dirty fix: (will likely break on game updates)
    * Download the corlibs for the game's Unity version from here: https://unity.bepinex.dev/corlibs/
    * Unzip and copy mscorlib.dll (and System.Reflection.Emit DLLs, if present) from the folder
    * Paste and overwrite the files into <Game>_Data/Managed/

With UnityDoorstop: (BepInEx only, or if you use UnityDoorstop + Standalone release):
    * Download the corlibs for the game's Unity version from here: https://unity.bepinex.dev/corlibs/
    * Unzip and copy mscorlib.dll (and System.Reflection.Emit DLLs, if present) from the folder
    * Find the folder which contains doorstop_config.ini (the game folder, or your r2modman/ThunderstoreModManager profile folder)
    * Make a subfolder called 'corlibs' inside this folder.
    * Paste the DLLs inside the corlibs folder.
    * In doorstop_config.ini, set 'dllSearchPathOverride=corlibs'.

Doorstop example:
- <Game>\
    - <Game>_Data\...
    - BepInEx\...
    - corlibs\
        - mscorlib.dll
    - doorstop_config.ini (with dllSearchPathOverride=corlibs)
    - <Game>.exe
    - winhttp.dll";
            }
            else
            {
                Input.Text = $"The C# Console has been disabled. {ex}";
            }
        }

        private static readonly Dictionary<string, string> helpDict = new();

        public static void SetupHelpInteraction()
        {
            Dropdown drop = Panel.HelpDropdown;

            helpDict.Add(DEFAULT_HELP_ITEM, "");
            helpDict.Add("Usings", HELP_USINGS);
            helpDict.Add("REPL", HELP_REPL);
            helpDict.Add("Classes", HELP_CLASSES);
            helpDict.Add("Coroutines", HELP_COROUTINES);

            foreach (KeyValuePair<string, string> opt in helpDict)
                drop.options.Add(new Dropdown.OptionData(opt.Key));
        }

        public static void HelpSelected(int index)
        {
            if (index == 0)
                return;

            KeyValuePair<string, string> helpText = helpDict.ElementAt(index);

            Input.Text = helpText.Value;

            Panel.HelpDropdown.value = 0;
        }

        internal const string DEFAULT_HELP_ITEM = "(Select Help)";

        internal const string STARTUP_TEXT = @"<color=#5d8556>// Welcome to the UnityExplorer C# Console!

// It is recommended to use the Log panel (or a console log window) while using this tool.
// Use the Help dropdown to see detailed examples of how to use the console.

// To execute a script automatically on startup, put the script at 'sinai-dev-UnityExplorer\Scripts\startup.cs'</color>";

        internal const string HELP_USINGS = @"// You can add a using directive to any namespace, but you must compile for it to take effect.
// It will remain in effect until you Reset the console.
using UnityEngine.UI;

// To see your current usings, use the ""GetUsing();"" helper.
// Note: You cannot add usings and evaluate REPL at the same time.";

        internal const string HELP_REPL = @"/* REPL (Read-Evaluate-Print-Loop) is a way to execute code immediately.
 * REPL code cannot contain any using directives or classes.
 * The return value of the last line of your REPL will be printed to the log.
 * Variables defined in REPL will exist until you Reset the console.
*/

// eg: This code would print 'Hello, World!', and then print 6 as the return value.
Log(""Hello, world!"");
var x = 5;
++x;

/* The following helpers are available in REPL mode:
 * CurrentTarget;     - System.Object, the target of the active Inspector tab
 * AllTargets;        - System.Object[], the targets of all Inspector tabs
 * Log(obj);          - prints a message to the console log
 * Inspect(obj);      - inspect the object with the Inspector
 * Inspect(someType); - inspect a Type with static reflection
 * Start(enumerator); - Coroutine, starts the IEnumerator as a Coroutine, and returns the Coroutine.
 * Stop(coroutine);   - stop the Coroutine ONLY if it was started with Start(ienumerator).
 * Copy(obj);         - copies the object to the UnityExplorer Clipboard
 * Paste();           - System.Object, the contents of the Clipboard.
 * GetUsing();        - prints the current using directives to the console log
 * GetVars();         - prints the names and values of the REPL variables you have defined
 * GetClasses();      - prints the names and members of the classes you have defined
 * help;              - the default REPL help command, contains additional helpers.
*/";

        internal const string HELP_CLASSES = @"// Classes you compile will exist until the application closes.
// You can soft-overwrite a class by compiling it again with the same name. The old class will still technically exist in memory.

// Compiled classes can be accessed from both inside and outside this console.
// Note: in IL2CPP, you must declare a Namespace to inject these classes with ClassInjector or it will crash the game.

public class HelloWorld
{
    public static void Main()
    {
        UnityExplorer.ExplorerCore.Log(""Hello, world!"");
    }
}

// In REPL, you could call the example method above with ""HelloWorld.Main();""
// Note: The compiler does not allow you to run REPL code and define classes at the same time.

// In REPL, use the ""GetClasses();"" helper to see the classes you have defined since the last Reset.";

        internal const string HELP_COROUTINES = @"// To start a Coroutine directly, use ""Start(SomeCoroutine());"" in REPL mode.

// To declare a coroutine, you will need to compile it separately. For example:
public class MyCoro
{
    public static IEnumerator Main()
    {
        yield return null;
        UnityExplorer.ExplorerCore.Log(""Hello, world after one frame!"");
    }
}
// To run this Coroutine in REPL, it would look like ""Start(MyCoro.Main());""";

        #endregion
    }
}
