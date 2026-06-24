using System.Text.RegularExpressions;

namespace Read2Me.Services.Text;

public sealed class ToSentenceCaseStep(bool paragraphEnabled, bool wordEnabled, int wordMinLength) : ITextProcessingStep
{
    public string Process(string text)
    {
        if (paragraphEnabled && IsAllCaps(text))
        {
            var lower = text.ToLowerInvariant();
            for (int i = 0; i < lower.Length; i++)
            {
                if (char.IsLetter(lower[i]))
                    return lower[..i] + char.ToUpperInvariant(lower[i]) + lower[(i + 1)..];
            }
            return lower;
        }

        if (wordEnabled)
        {
            return Regex.Replace(text, @"\S+", m =>
            {
                var token = m.Value;
                if (token.Length >= wordMinLength && IsAllCaps(token))
                    return token.ToLowerInvariant();
                return token;
            });
        }

        return text;
    }

    private static bool IsAllCaps(string s)
    {
        bool hasLetter = false;
        foreach (var c in s)
        {
            if (char.IsLetter(c))
            {
                hasLetter = true;
                if (!char.IsUpper(c)) return false;
            }
        }
        return hasLetter;
    }
}
