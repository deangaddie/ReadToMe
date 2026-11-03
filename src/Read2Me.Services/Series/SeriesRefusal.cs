using System.Globalization;

namespace Read2Me.Services.Series
{
    /// <summary>How the API answers a refusal (#213): a malformed request (400), something unknown (404), a state refusal (409), or a domain failure (422).</summary>
    public enum SeriesRefusalKind
    {
        Malformed,
        NotFound,
        Blocked,
        Failed,
    }

    /// <summary>Why a registry call was refused, with the <see cref="Detail"/> that names the thing.</summary>
    public sealed record SeriesRefusal(SeriesRefusalKind Kind, string Detail)
    {
        public static readonly SeriesRefusal BlankName = new(SeriesRefusalKind.Malformed, "Series name cannot be blank.");
        public static readonly SeriesRefusal UnknownSeries = new(SeriesRefusalKind.NotFound, "No such series.");
        public static readonly SeriesRefusal OutOfDate = new(SeriesRefusalKind.Blocked,
            "The series' books have changed since its positions were read: read the series again.");
        public static readonly SeriesRefusal Unreadable = new(SeriesRefusalKind.Failed, "The series' registry could not be read.");
        public static readonly SeriesRefusal RulesUnchecked = new(SeriesRefusalKind.Failed,
            "A series registry in the Workspace could not be read, so names and memberships can't be checked.");
        public static readonly SeriesRefusal MemberUnreadable = new(SeriesRefusalKind.Failed,
            "A book in this series could not be read, so its positions can't be saved.");

        public static readonly SeriesRefusal UnknownSeriesCharacter = new(SeriesRefusalKind.NotFound, "No such series character.");

        public static SeriesRefusal UnknownCharacter(string title) =>
            new(SeriesRefusalKind.NotFound, $"No such Character in '{title}'.");

        public static SeriesRefusal NotLinked(string title, string label) =>
            new(SeriesRefusalKind.NotFound, $"No Character in '{title}' is linked to {label}.");

        public static SeriesRefusal CharacterAlreadyLinked(string name, string title, string label) =>
            new(SeriesRefusalKind.Blocked, $"{name} in '{title}' is already linked to {label}. Unlink it first.");

        public static SeriesRefusal BookAlreadyLinked(string title, string linked, string label) =>
            new(SeriesRefusalKind.Blocked,
                $"'{title}' already links {linked} to {label}. If they are the same person, merge the two Characters first.");

        /// <summary>The joining book's half of the Voice merge did not commit: nothing was linked.</summary>
        public static SeriesRefusal VoicesNotWritten(string title, string reason, bool blocked) =>
            new(blocked ? SeriesRefusalKind.Blocked : SeriesRefusalKind.Failed,
                $"The series' Voices could not be written into '{title}', so nothing was linked: {reason}");

        /// <summary>Push and take resolve <i>differs from series</i>, so a book holding the series copy has nothing to resolve.</summary>
        public static SeriesRefusal DoesNotDiffer(string title, string label) =>
            new(SeriesRefusalKind.Blocked, $"{label}'s Voices in '{title}' don't differ from the series.");

        /// <summary>A push or take that could not be written into some books; the rest were.</summary>
        public static SeriesRefusal CopiesFailed(IReadOnlyList<(string Title, string Reason)> failures) =>
            new(SeriesRefusalKind.Failed,
                "The Voices could not be written into " +
                string.Join("; ", failures.Select(f => $"'{f.Title}': {f.Reason}")) +
                (failures.Count == 1 ? ". That book still differs from the series." : ". Those books still differ from the series."));

        public static SeriesRefusal UnknownFolder(string folder) => new(SeriesRefusalKind.NotFound, $"No project '{folder}'.");

        public static SeriesRefusal NotAMember(string title, string series) =>
            new(SeriesRefusalKind.NotFound, $"'{title}' is not in the series '{series}'.");

        public static SeriesRefusal NameTaken(string holder) =>
            new(SeriesRefusalKind.Blocked, $"There is already a series called '{holder}'.");

        public static SeriesRefusal InAnotherSeries(string title, string series) =>
            new(SeriesRefusalKind.Blocked, $"'{title}' is already in the series '{series}'. Move it to join this one.");

        public static SeriesRefusal MalformedPosition(string folder, decimal position) =>
            new(SeriesRefusalKind.Malformed,
                $"Position {position.ToString(CultureInfo.InvariantCulture)} for '{folder}' must be 0 or more, with at most two decimal places.");

        public static SeriesRefusal Tie(string first, string second, decimal position) =>
            new(SeriesRefusalKind.Blocked,
                $"'{first}' and '{second}' are both Book {position.ToString(CultureInfo.InvariantCulture)}: two books can't share a number.");
    }

    /// <summary>A registry call's value, or why it was refused.</summary>
    public readonly struct SeriesResult<T>
    {
        private SeriesResult(T? value, SeriesRefusal? refusal) => (Value, Refusal) = (value, refusal);

        public T? Value { get; }
        public SeriesRefusal? Refusal { get; }

        public static implicit operator SeriesResult<T>(T value) => new(value, null);
        public static implicit operator SeriesResult<T>(SeriesRefusal refusal) => new(default, refusal);
    }
}
