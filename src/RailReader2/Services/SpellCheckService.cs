using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using RailReader.Core;
using RailReader.Core.Services;
using WeCantSpell.Hunspell;

namespace RailReader2.Services;

/// <summary>A word the dictionary rejected: <see cref="Start"/>/<see cref="Length"/> index into the
/// checked text, <see cref="Word"/> is that slice.</summary>
public readonly record struct Misspelling(int Start, int Length, string Word)
{
    public int End => Start + Length;
}

/// <summary>
/// Hunspell spell checking for annotation note text, via WeCantSpell.Hunspell (a pure-managed port —
/// no native library, identical on every platform). One shared instance: the dictionary is loaded
/// once, in the background, the first time a note editor asks for it.
/// <para>
/// Dictionaries are Hunspell <c>.aff</c>/<c>.dic</c> pairs. <c>en_GB</c> and <c>en_US</c> ship in
/// <c>{AppContext.BaseDirectory}/Dictionaries</c>; any pair dropped into
/// <see cref="UserDictionariesDir"/> (e.g. LibreOffice's <c>en_ZA</c>, <c>de_DE</c>) is offered too,
/// and wins over a bundled one of the same name. The personal word list is a plain UTF-8 file, one
/// word per line, shared across languages.
/// </para>
/// <para>
/// Threading: <see cref="Check"/>/<see cref="Suggest"/> and the word-list mutators are called on the
/// UI thread. The dictionary loads on the thread pool and is published as a single immutable
/// <see cref="LoadedDictionary"/> reference, so a reader never sees a half-swapped state.
/// <see cref="Changed"/> can therefore fire on a pool thread — subscribers marshal themselves.
/// </para>
/// </summary>
public sealed class SpellCheckService
{
    private const int MaxSuggestions = 7;

    private static readonly SemaphoreSlim s_prefetchGate = new(Math.Max(1, Environment.ProcessorCount / 2));

    private static readonly Lazy<SpellCheckService> s_shared = new(() => new SpellCheckService());

    /// <summary>The app-wide instance, backed by <see cref="SpellCheckPreferences"/>.</summary>
    public static SpellCheckService Shared => s_shared.Value;

    public static string BundledDictionariesDir => Path.Combine(AppContext.BaseDirectory, "Dictionaries");
    public static string UserDictionariesDir => Path.Combine(AppConfig.ConfigDir, "dictionaries");
    public static string PersonalWordsPath => Path.Combine(AppConfig.ConfigDir, "spellcheck_words.txt");

    private sealed record LoadedDictionary(string Language, WordList Words)
    {
        public ConcurrentDictionary<string, bool> Cache { get; } = new(StringComparer.Ordinal);

        /// <summary>Suggestions by exact word. Hunspell takes 70-250 ms per word on en_GB, too long to
        /// block the UI thread when a context menu opens, so <see cref="PrefetchSuggestions"/>
        /// computes them on the thread pool as soon as a word is underlined.</summary>
        public ConcurrentDictionary<string, Lazy<IReadOnlyList<string>>> Suggestions { get; } = new(StringComparer.Ordinal);
    }

    private readonly SpellCheckPreferences? _prefs;
    private readonly string _personalWordsPath;
    private readonly HashSet<string> _personal = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);
    private volatile LoadedDictionary? _dictionary;
    private string? _loadingLanguage;
    private string _language;

    /// <summary>Raised when the dictionary finishes loading, the language or enabled state changes,
    /// or the personal word list changes. May be raised off the UI thread.</summary>
    public event Action? Changed;

    private SpellCheckService()
    {
        _prefs = SpellCheckPreferences.Load();
        _personalWordsPath = PersonalWordsPath;
        _language = _prefs.Language is { } lang && AvailableLanguages().Contains(lang)
            ? lang
            : DefaultLanguage(AvailableLanguages(), CultureInfo.CurrentUICulture);
        LoadPersonalWords();
    }

    /// <summary>Test seam: a service over an in-memory word list, with no preferences file.</summary>
    internal SpellCheckService(WordList words, string personalWordsPath)
    {
        _personalWordsPath = personalWordsPath;
        _language = "test";
        _dictionary = new LoadedDictionary(_language, words);
        LoadPersonalWords();
    }

    public bool Enabled
    {
        get => _prefs?.Enabled ?? true;
        set
        {
            if (_prefs is null || _prefs.Enabled == value) return;
            _prefs.Enabled = value;
            _prefs.Save();
            Changed?.Invoke();
        }
    }

    public string Language
    {
        get => _language;
        set
        {
            if (_language == value) return;
            _language = value;
            // A failure belongs to the language that failed; switching back to an already-loaded
            // one makes EnsureLoaded return early, so clear it here rather than there.
            LoadError = null;
            if (_prefs is not null)
            {
                _prefs.Language = value;
                _prefs.Save();
            }
            EnsureLoaded();
            Changed?.Invoke();
        }
    }

    /// <summary>True once the dictionary for <see cref="Language"/> is loaded and
    /// <see cref="Check"/> returns real results.</summary>
    public bool IsReady => _dictionary?.Language == _language;

    /// <summary>Why the current language's dictionary couldn't be loaded, or null.</summary>
    public string? LoadError { get; private set; }

    /// <summary>Starts loading the dictionary for <see cref="Language"/> in the background if it
    /// isn't loaded or loading already. Cheap to call repeatedly.</summary>
    public void EnsureLoaded()
    {
        var language = _language;
        if (_dictionary?.Language == language || _loadingLanguage == language) return;
        _loadingLanguage = language;
        LoadError = null;

        _ = Task.Run(() =>
        {
            try
            {
                var (dic, aff) = FindDictionaryFiles(language)
                    ?? throw new FileNotFoundException($"No Hunspell dictionary named '{language}' was found.");
                var words = WordList.CreateFromFiles(dic, aff);
                // A language switch while this loaded makes the result stale — drop it.
                if (_language == language)
                    _dictionary = new LoadedDictionary(language, words);
            }
            catch (Exception ex)
            {
                RailReaderLogging.Logger.Error($"Failed to load spelling dictionary '{language}'", ex);
                if (_language == language) LoadError = ex.Message;
            }
            finally
            {
                if (_loadingLanguage == language) _loadingLanguage = null;
            }
            Changed?.Invoke();
        });
    }

    /// <summary>Misspelled words in <paramref name="text"/>, in order. Empty while disabled or
    /// before the dictionary has loaded.</summary>
    public IReadOnlyList<Misspelling> Check(string? text)
    {
        if (string.IsNullOrEmpty(text) || !Enabled) return [];
        var dictionary = _dictionary;
        if (dictionary is null || dictionary.Language != _language) return [];

        List<Misspelling>? result = null;
        foreach (var (start, length) in Tokenize(text))
        {
            var word = text.Substring(start, length);
            if (!IsCorrect(dictionary, word))
                (result ??= []).Add(new Misspelling(start, length, word));
        }
        return result ?? (IReadOnlyList<Misspelling>)[];
    }

    /// <summary>Up to <see cref="MaxSuggestions"/> replacements for <paramref name="word"/>, best
    /// first. Instant when <see cref="PrefetchSuggestions"/> already ran for the word; otherwise it
    /// computes (or waits for an in-flight prefetch) on the calling thread.</summary>
    public IReadOnlyList<string> Suggest(string word)
    {
        var dictionary = _dictionary;
        if (dictionary is null || string.IsNullOrEmpty(word)) return [];
        return SuggestionsFor(dictionary, word).Value;
    }

    /// <summary>Computes suggestions for <paramref name="words"/> in the background so a later
    /// <see cref="Suggest"/> returns immediately.</summary>
    public void PrefetchSuggestions(IEnumerable<string> words)
    {
        var dictionary = _dictionary;
        if (dictionary is null) return;
        foreach (var word in words)
        {
            if (dictionary.Suggestions.ContainsKey(word)) continue;
            var lazy = SuggestionsFor(dictionary, word);
            // Each search is 70-250 ms of CPU; cap concurrency so pasting a paragraph of technical
            // terms doesn't flood the thread pool the page renderer also relies on.
            _ = Task.Run(async () =>
            {
                await s_prefetchGate.WaitAsync().ConfigureAwait(false);
                try { _ = lazy.Value; }
                finally { s_prefetchGate.Release(); }
            });
        }
    }

    private static Lazy<IReadOnlyList<string>> SuggestionsFor(LoadedDictionary dictionary, string word)
        => dictionary.Suggestions.GetOrAdd(word, static (w, list) => new Lazy<IReadOnlyList<string>>(() =>
        {
            // Dictionaries spell apostrophes as ASCII; hand back suggestions in the typist's style.
            bool curly = w.Contains('\u2019');
            var options = new QueryOptions { MaxSuggestions = MaxSuggestions };
            return list.Suggest(Normalize(w), options)
                .Take(MaxSuggestions)
                .Select(s => curly ? s.Replace('\'', '\u2019') : s)
                .ToList();
        }), dictionary.Words);

    public IReadOnlyList<string> PersonalWords => _personal.Order(StringComparer.OrdinalIgnoreCase).ToList();

    public void AddToPersonalDictionary(string word)
    {
        if (string.IsNullOrWhiteSpace(word) || !_personal.Add(Normalize(word.Trim()))) return;
        SavePersonalWords();
        Changed?.Invoke();
    }

    public void RemoveFromPersonalDictionary(string word)
    {
        if (!_personal.Remove(word)) return;
        SavePersonalWords();
        Changed?.Invoke();
    }

    /// <summary>Accepts <paramref name="word"/> until the app exits, without saving it.</summary>
    public void Ignore(string word)
    {
        if (_ignored.Add(Normalize(word))) Changed?.Invoke();
    }

    /// <summary>Names of every dictionary with both files present, bundled and user-added.</summary>
    public static IReadOnlyList<string> AvailableLanguages()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var dir in new[] { UserDictionariesDir, BundledDictionariesDir })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var dic in Directory.EnumerateFiles(dir, "*.dic"))
            {
                if (File.Exists(Path.ChangeExtension(dic, ".aff")))
                    names.Add(Path.GetFileNameWithoutExtension(dic));
            }
        }
        return names.ToList();
    }

    /// <summary>
    /// The dictionary matching <paramref name="culture"/> exactly (<c>en-ZA</c> → <c>en_ZA</c>), else
    /// the first one in the same language (so British before American English), else <c>en_GB</c>,
    /// else whatever is available.
    /// </summary>
    internal static string DefaultLanguage(IReadOnlyList<string> available, CultureInfo culture)
    {
        var exact = culture.Name.Replace('-', '_');
        if (available.Contains(exact)) return exact;
        var sameLanguage = available.FirstOrDefault(
            a => a.StartsWith(culture.TwoLetterISOLanguageName + "_", StringComparison.Ordinal));
        if (sameLanguage is not null) return sameLanguage;
        if (available.Contains("en_GB")) return "en_GB";
        return available.FirstOrDefault() ?? "en_GB";
    }

    /// <summary>
    /// Splits <paramref name="text"/> into the words worth spell-checking. Hyphens split words;
    /// apostrophes inside a word are kept (<c>don't</c>, <c>Fisher's</c>). Skipped, because in
    /// academic notes they are almost never misspellings and would otherwise drown the real ones:
    /// URLs and e-mail addresses; anything containing a digit or underscore (<c>H0</c>, <c>x_i</c>,
    /// <c>2nd</c>); single letters (maths variables); all-caps acronyms (<c>ANOVA</c>); mixed-case
    /// names (<c>LaTeX</c>, <c>SciPy</c>); and LaTeX commands (<c>\alpha</c>).
    /// </summary>
    internal static IEnumerable<(int Start, int Length)> Tokenize(string text)
    {
        int i = 0, n = text.Length;
        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(text[i])) i++;
            int chunkStart = i;
            while (i < n && !char.IsWhiteSpace(text[i])) i++;
            if (i == chunkStart) yield break;
            if (IsUrlOrEmail(text.AsSpan(chunkStart, i - chunkStart))) continue;

            int j = chunkStart;
            while (j < i)
            {
                if (!IsLetter(text[j])) { j++; continue; }

                int start = j;
                bool rejected = false;
                while (j < i && (IsLetter(text[j]) || IsApostrophe(text[j])
                                 || char.IsDigit(text[j]) || text[j] == '_'))
                {
                    if (char.IsDigit(text[j]) || text[j] == '_') rejected = true;
                    j++;
                }
                int end = j;
                while (end > start && IsApostrophe(text[end - 1])) end--;

                // A letter run glued to a preceding digit/underscore is part of the same token
                // ("2nd", "10th", "_foo"), so it is skipped like one containing them.
                if (rejected || (start > chunkStart && (char.IsDigit(text[start - 1]) || text[start - 1] == '_'))
                    || (start > 0 && text[start - 1] is '\\' or '@' or '#')) continue;
                var word = text.AsSpan(start, end - start);
                if (CountLetters(word) < 2 || HasInnerCapital(word)) continue;
                yield return (start, end - start);
            }
        }
    }

    private bool IsCorrect(LoadedDictionary dictionary, string word)
    {
        var normalized = Normalize(word);
        if (_personal.Contains(normalized) || _ignored.Contains(normalized)) return true;
        return dictionary.Cache.GetOrAdd(normalized, static (w, list) =>
            list.Check(w)
            // Possessives of names the dictionary knows but has no 's form for (Bayes's, Tukey's).
            || (w.Length > 2 && w.EndsWith("'s", StringComparison.OrdinalIgnoreCase) && list.Check(w[..^2])),
            dictionary.Words);
    }

    private static string Normalize(string word) => word.Replace('’', '\'');

    private static bool IsLetter(char c) => char.GetUnicodeCategory(c) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
        or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    private static bool IsApostrophe(char c) => c is '\'' or '’';

    private static int CountLetters(ReadOnlySpan<char> word)
    {
        int count = 0;
        foreach (var c in word) if (char.IsLetter(c)) count++;
        return count;
    }

    /// <summary>True for all-caps words and camel/mixed case — an uppercase letter anywhere after
    /// the first character. (A capitalised sentence-start word has none.)</summary>
    private static bool HasInnerCapital(ReadOnlySpan<char> word)
    {
        for (int k = 1; k < word.Length; k++)
            if (char.IsUpper(word[k])) return true;
        return false;
    }

    private static bool IsUrlOrEmail(ReadOnlySpan<char> chunk)
        => chunk.Contains("://", StringComparison.Ordinal)
           || chunk.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
           || (chunk.IndexOf('@') > 0 && chunk.IndexOf('.') > 0);

    private static (string Dic, string Aff)? FindDictionaryFiles(string language)
    {
        foreach (var dir in new[] { UserDictionariesDir, BundledDictionariesDir })
        {
            var dic = Path.Combine(dir, language + ".dic");
            var aff = Path.Combine(dir, language + ".aff");
            if (File.Exists(dic) && File.Exists(aff)) return (dic, aff);
        }
        return null;
    }

    private void LoadPersonalWords()
    {
        try
        {
            if (!File.Exists(_personalWordsPath)) return;
            foreach (var line in File.ReadLines(_personalWordsPath, Encoding.UTF8))
            {
                var word = line.Trim();
                if (word.Length > 0) _personal.Add(Normalize(word));
            }
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("Failed to load personal spelling dictionary", ex);
        }
    }

    private void SavePersonalWords()
    {
        var tmpPath = _personalWordsPath + ".tmp";
        try
        {
            if (Path.GetDirectoryName(_personalWordsPath) is { Length: > 0 } dir)
                Directory.CreateDirectory(dir);
            File.WriteAllLines(tmpPath, PersonalWords, new UTF8Encoding(false));
            File.Move(tmpPath, _personalWordsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            RailReaderLogging.Logger.Error("Failed to save personal spelling dictionary", ex);
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { /* best effort */ }
        }
    }
}
