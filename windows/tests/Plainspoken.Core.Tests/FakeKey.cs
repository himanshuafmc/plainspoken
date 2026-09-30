namespace Plainspoken.Core.Tests;

/// <summary>
/// A made-up value with the shape of a Gemini API key, for redaction tests. It is built at run time
/// so that no key-shaped string is ever committed to the repository.
/// </summary>
internal static class FakeKey
{
    public static readonly string Value = "AIza" + new string('0', 35);
}
