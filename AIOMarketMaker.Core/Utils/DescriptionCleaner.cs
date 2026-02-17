using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace AIOMarketMaker.Core.Utils
{
    public static class DescriptionCleaner
    {
        private static readonly HashSet<string> TagsToRemove = new(StringComparer.OrdinalIgnoreCase)
        {
            "style", "script", "nav", "footer", "header"
        };

        private static readonly string[] BoilerplateClassPatterns = { "nav", "menu", "sidebar" };

        public static string? Clean(string? innerHtml)
        {
            if (string.IsNullOrWhiteSpace(innerHtml))
            {
                return null;
            }

            var parser = new HtmlParser();
            var doc = parser.ParseDocument($"<body>{innerHtml}</body>");

            RemoveTagsByName(doc);
            RemoveByBoilerplateClass(doc);

            var text = doc.Body?.TextContent ?? string.Empty;

            text = text.Replace('\u00A0', ' ');
            text = Regex.Replace(text, @"\s+", " ").Trim();

            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return text;
        }

        private static void RemoveTagsByName(IDocument doc)
        {
            foreach (var tagName in TagsToRemove)
            {
                var elements = doc.QuerySelectorAll(tagName).ToList();
                foreach (var el in elements)
                {
                    el.Remove();
                }
            }
        }

        private static void RemoveByBoilerplateClass(IDocument doc)
        {
            var allElements = doc.QuerySelectorAll("[class]").ToList();
            foreach (var el in allElements)
            {
                if (HasBoilerplateClass(el.GetAttribute("class")))
                {
                    el.Remove();
                }
            }
        }

        private static bool HasBoilerplateClass(string? classValue)
        {
            if (string.IsNullOrEmpty(classValue))
            {
                return false;
            }

            var classTokens = classValue.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in classTokens)
            {
                var parts = token.Split('-');
                foreach (var part in parts)
                {
                    foreach (var pattern in BoilerplateClassPatterns)
                    {
                        if (part.Equals(pattern, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }
}
