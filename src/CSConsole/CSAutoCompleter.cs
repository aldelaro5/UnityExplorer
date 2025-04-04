using System.Text;
using UnityExplorer.UI.Panels;
using UnityExplorer.UI.Widgets.AutoComplete;
using UniverseLib.UI.Models;
#if NET472
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Tags;
#else
using UnityExplorer.CSConsole.Lexers;
#endif

namespace UnityExplorer.CSConsole
{
    public class CSAutoCompleter : ISuggestionProvider
    {
        public InputFieldRef InputField => ConsoleController.Input;

        public bool AnchorToCaretPosition => true;

        bool ISuggestionProvider.AllowNavigation => true;

        public void OnSuggestionClicked(Suggestion suggestion)
        {
            ConsoleController.InsertSuggestionAtCaret(suggestion.UnderlyingValue);
            AutoCompleteModal.Instance.ReleaseOwnership(this);
        }

        internal readonly HashSet<char> delimiters = new()
        {
            '{',
            '}',
            ',',
            ';',
            '<',
            '>',
            '(',
            ')',
            '[',
            ']',
            '=',
            '|',
            '&',
            '?'
        };

        private readonly List<Suggestion> suggestions = new();

#if NET472
        public async Task CheckAutocompletes(char? insertedChar)
#else
        public void CheckAutocompletes()
#endif
        {
            if (string.IsNullOrEmpty(InputField.Text))
            {
                AutoCompleteModal.Instance.ReleaseOwnership(this);
                return;
            }

            suggestions.Clear();
#if NET472
            var completions = await ConsoleController.Evaluator.GetCompletions(
                ConsoleController.Input.Text,
                ConsoleController.Input.Component.caretPosition,
                insertedChar);

            foreach (var completion in completions.items)
            {
                string customColor = string.Empty;
                if (completion.Tags.Contains(WellKnownTags.Namespace))
                    customColor = "CCCCCC";
                else if (completion.Tags.Contains(WellKnownTags.Keyword))
                    customColor = SignatureHighlighter.keywordBlueHex;

                string highlightedText = customColor == string.Empty
                    ? GetHighlightString(completion.DisplayText.Substring(0, completions.prefix.Length),
                        completion.DisplayText.Remove(0, completions.prefix.Length))
                    : $"<color=#{customColor}>{completion}</color>";
                suggestions.Add(new Suggestion(highlightedText, completion.DisplayText));
            }
#else
            int caret = Math.Max(0, Math.Min(InputField.Text.Length - 1, InputField.Component.caretPosition - 1));
            int startIdx = caret;

            // If the character at the caret index is whitespace or delimiter,
            // or if the next character (if it exists) is not whitespace,
            // then we don't want to provide suggestions.
            if (char.IsWhiteSpace(InputField.Text[caret])
                || delimiters.Contains(InputField.Text[caret])
                || (InputField.Text.Length > caret + 1 && !char.IsWhiteSpace(InputField.Text[caret + 1])))
            {
                AutoCompleteModal.Instance.ReleaseOwnership(this);
                return;
            }

            // get the current composition string (from caret back to last delimiter)
            while (startIdx > 0)
            {
                startIdx--;
                char c = InputField.Text[startIdx];
                if (delimiters.Contains(c) || char.IsWhiteSpace(c))
                {
                    startIdx++;
                    break;
                }
            }
            string input = InputField.Text.Substring(startIdx, caret - startIdx + 1);

            // Get MCS completions

            string[] evaluatorCompletions = ConsoleController.Evaluator.GetCompletions(input, out string prefix);

            if (evaluatorCompletions != null && evaluatorCompletions.Any())
            {
                suggestions.AddRange(from completion in evaluatorCompletions
                    select new Suggestion(GetHighlightString(completion.Substring(0, prefix.Length), completion.Remove(0, prefix.Length)),
                        completion));
            }

            // Get manual namespace completions

            foreach (string ns in ReflectionUtility.AllNamespaces)
            {
                if (ns.StartsWith(input, StringComparison.OrdinalIgnoreCase))
                {
                    if (!namespaceHighlights.ContainsKey(ns))
                        namespaceHighlights.Add(ns, $"<color=#CCCCCC>{ns}</color>");

                    int lastDotIndex = input.LastIndexOf('.');
                    if (ns.Length >= lastDotIndex)
                    {
                        suggestions.Add(new Suggestion(namespaceHighlights[ns], ns));
                    }
                }
            }

            // Get manual keyword completions

            foreach (string kw in KeywordLexer.keywords)
            {
                if (kw.StartsWith(input, StringComparison.OrdinalIgnoreCase))// && kw.Length > input.Length)
                {
                    if (!keywordHighlights.ContainsKey(kw))
                        keywordHighlights.Add(kw, $"<color=#{SignatureHighlighter.keywordBlueHex}>{kw}</color>");

                    suggestions.Add(new Suggestion(keywordHighlights[kw], kw));
                }
            }
#endif
            if (suggestions.Any())
            {
                AutoCompleteModal.TakeOwnership(this);
                AutoCompleteModal.Instance.SetSuggestions(suggestions);
            }
            else
            {
                AutoCompleteModal.Instance.ReleaseOwnership(this);
            }
        }


        private readonly Dictionary<string, string> namespaceHighlights = new();

        private readonly Dictionary<string, string> keywordHighlights = new();

        private readonly StringBuilder highlightBuilder = new();
        private const string OPEN_HIGHLIGHT = "<color=cyan>";

        private string GetHighlightString(string prefix, string completion)
        {
            highlightBuilder.Clear();
            highlightBuilder.Append(OPEN_HIGHLIGHT);
            highlightBuilder.Append(prefix);
            highlightBuilder.Append(SignatureHighlighter.CLOSE_COLOR);
            highlightBuilder.Append(completion);
            return highlightBuilder.ToString();
        }
    }
}
