using System.Globalization;

namespace Burbuja.Portal.Services;

/// <summary>
/// M13 R0 — the words a count needs, in one place.
///
/// <para><b>Why this exists.</b> F24: this console wrote its counts as <c>1 item(s)</c>, <c>8 booking(s)</c>,
/// <c>3 turno(s) hoy</c>, <c>2 contradicción(es)</c> — four placeholders pretending to be grammar, in a
/// product whose whole claim is that it does not print a number it cannot stand behind. A count with a
/// parenthesis in it reads as a number nobody finished writing.</para>
///
/// <para><b>Why an English and a Spanish overload rather than one.</b> The AKOS section is Spanish and the
/// rest of the shell is English (a decision, not an accident: the corpus this workload serves is Spanish).
/// Spanish nouns inflect for number in the article and the noun together — <c>1 turno</c> / <c>2 turnos</c>,
/// <c>1 contradicción</c> / <c>2 contradicciones</c> — so an English-shaped helper would produce
/// <c>2 turnos</c> from a rule that cannot know <c>ón</c> becomes <c>ones</c>. Both forms are passed
/// explicitly, which is the only rule that is right for both languages.</para>
/// </summary>
public static class Text
{
    /// <summary>`1 item` / `2 items` — the count and the noun, never a placeholder.</summary>
    public static string Plural(int count, string singular, string plural) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";

    /// <summary>
    /// The same, keyed on a <c>long</c> — the activity ring's totals are <c>long</c>, and a cast at every
    /// call site is how one of them eventually truncates.
    /// </summary>
    public static string Plural(long count, string singular, string plural) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";

    /// <summary>
    /// Spanish: both numbers are given because the noun's own inflection cannot be derived from the singular
    /// (<c>turno</c>/<c>turnos</c> but <c>contradicción</c>/<c>contradicciones</c> and <c>vez</c>/<c>veces</c>).
    /// </summary>
    public static string PluralEs(int count, string singular, string plural) =>
        $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";

    /// <summary>`1 of 12` — a ratio the KERNEL served, formatted and never computed here.</summary>
    public static string Ratio(long part, long whole) =>
        $"{part.ToString(CultureInfo.InvariantCulture)} / {whole.ToString(CultureInfo.InvariantCulture)}";
}
